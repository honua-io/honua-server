// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Threading.Channels;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Honua.Server.Features.Streaming;

internal sealed class FeatureStreamSessionManager : IDisposable
{
    internal const string DefaultSubscriptionId = "default";
    private static readonly RedisChannel BroadcastChannel = new("featurechange:stream:broadcast", RedisChannel.PatternMode.Literal);
    private static readonly TimeSpan ClusterBroadcastRecoveryInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan RoutabilityRefreshInterval = TimeSpan.FromSeconds(1);
    private const int RecentEventIdCapacity = 128;
    private const int SubscriberSecurityRefreshParallelism = 8;

    private const int MaxClusterBroadcastBacklog = 1024;
    private readonly ConcurrentDictionary<Guid, SessionEntry> _sessions = new();
    private readonly ConcurrentQueue<string> _clusterBroadcastBacklog = new();
    private readonly object _clusterBroadcastLock = new();
    private readonly IOptions<FeatureStreamOptions> _options;
    private readonly ILogger<FeatureStreamSessionManager> _logger;
    private readonly FeatureStreamMetrics _metrics;
    private readonly IConnectionMultiplexer? _redis;
    private readonly FeatureStreamRoutabilityGuard? _routabilityGuard;
    private readonly IServiceScopeFactory? _serviceScopeFactory;
    private readonly SemaphoreSlim _routabilityRefreshGate = new(1, 1);
    private readonly SemaphoreSlim _clusterBroadcastDispatchGate = new(1, 1);
    private ISubscriber? _subscriber;
    private readonly Timer? _clusterBroadcastRecoveryTimer;
    private readonly string _instanceId = Guid.NewGuid().ToString("N");
    private bool _clusterBroadcastEnabled;
    private int _activeSessionCount;
    private readonly object _admissionLock = new();
    private readonly Dictionary<string, int> _partitionSessionCounts = new(StringComparer.Ordinal);
    private long _slowConsumerDrops;
    private long _heartbeatsSent;
    private int _clusterBroadcastUnavailableLogged;
    private int _clusterBroadcastFailedLogged;
    private int _clusterBroadcastRecoveryInProgress;
    private int _clusterBroadcastBacklogCount;
    private long _clusterBroadcastBacklogDropped;
    private bool _clusterBroadcastBacklogDropLogged;
    private long _nextRoutabilityRefreshTimestamp;
    private int _lastRoutabilityRefreshSucceeded = 1;

    public FeatureStreamSessionManager(
        IOptions<FeatureStreamOptions> options,
        ILogger<FeatureStreamSessionManager> logger,
        FeatureStreamMetrics metrics,
        IConnectionMultiplexer? redis = null,
        FeatureStreamRoutabilityGuard? routabilityGuard = null,
        IServiceScopeFactory? serviceScopeFactory = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
        _redis = redis;
        _routabilityGuard = routabilityGuard;
        _serviceScopeFactory = serviceScopeFactory;

        if (_redis is null)
        {
            return;
        }

        try
        {
            _subscriber = _redis.GetSubscriber();
            _subscriber.Subscribe(BroadcastChannel, HandleClusterBroadcast);
            _clusterBroadcastEnabled = true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Intentional broad catch: cluster broadcast subscribe is best-effort during
            // construction. A Redis outage here must not prevent the session manager (and
            // single-node feature streaming) from starting; the recovery timer retries.
            LogClusterBroadcastUnavailableOnce(ex);
        }

        _clusterBroadcastRecoveryTimer = new Timer(
            _ => TryRecoverClusterBroadcast(),
            null,
            ClusterBroadcastRecoveryInterval,
            ClusterBroadcastRecoveryInterval);
    }

    public long SlowConsumerDrops => Interlocked.Read(ref _slowConsumerDrops);

    public long HeartbeatsSent => Interlocked.Read(ref _heartbeatsSent);

    public int SessionCount => Volatile.Read(ref _activeSessionCount);

    public int MaxConcurrentSessions => _options.Value.MaxConcurrentSessions;

    public int MaximumPartitionSessionCount
    {
        get
        {
            lock (_admissionLock)
            {
                return _partitionSessionCounts.Count == 0 ? 0 : _partitionSessionCounts.Values.Max();
            }
        }
    }

    public async ValueTask<bool> RefreshRoutabilityAsync(CancellationToken cancellationToken = default)
    {
        if (_serviceScopeFactory is null || _routabilityGuard is null)
        {
            return true;
        }

        var now = Environment.TickCount64;
        if (CanReuseRoutabilityRefresh(now))
        {
            return Volatile.Read(ref _lastRoutabilityRefreshSucceeded) != 0;
        }

        await _routabilityRefreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        var throttleNextRefresh = true;
        try
        {
            now = Environment.TickCount64;
            if (CanReuseRoutabilityRefresh(now))
            {
                return Volatile.Read(ref _lastRoutabilityRefreshSucceeded) != 0;
            }

            await _routabilityGuard.RefreshAsync(
                ReadScopedMetadataAsync,
                cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref _lastRoutabilityRefreshSucceeded, 1);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throttleNextRefresh = false;
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Volatile.Write(ref _lastRoutabilityRefreshSucceeded, 0);
            FeatureStreamLog.RoutabilityRefreshFailed(_logger, ex);
            return false;
        }
        finally
        {
            if (throttleNextRefresh)
            {
                Volatile.Write(
                    ref _nextRoutabilityRefreshTimestamp,
                    Environment.TickCount64 + (long)RoutabilityRefreshInterval.TotalMilliseconds);
            }

            _routabilityRefreshGate.Release();
        }
    }

    // A layer the refresh denies, or that cannot be re-resolved, ends the session, and policy is read once per tick.
    public async Task RefreshSubscriberSecurityAsync(CancellationToken cancellationToken = default)
    {
        var work = new List<(Guid SessionId, StreamSubscriberSecurity Security)>();
        foreach (var (sessionId, entry) in _sessions)
        {
            foreach (var security in entry.GetRefreshableSubscriberSecurities())
            {
                work.Add((sessionId, security));
            }
        }

        if (work.Count == 0)
        {
            return;
        }

        var timeout = _options.Value.SubscriberPolicyRefreshInterval;
        await Parallel.ForEachAsync(
            work,
            new ParallelOptions
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = SubscriberSecurityRefreshParallelism
            },
            async (item, token) =>
            {
                if (!_sessions.ContainsKey(item.SessionId))
                {
                    return;
                }

                StreamSubscriberSecurityRefreshOutcome outcome;
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                deadline.CancelAfter(timeout);
                try
                {
                    outcome = await item.Security.RefreshAsync(deadline.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    // Fail closed: the filter already denies every route, and the session ends.
                    if (TryRemoveSession(item.SessionId, FeatureStreamDisconnectReason.PolicyRevoked))
                    {
                        FeatureStreamLog.SubscriberPolicyRefreshFailed(_logger, item.SessionId, ex);
                    }

                    return;
                }

                if (outcome == StreamSubscriberSecurityRefreshOutcome.Revoked &&
                    TryRemoveSession(item.SessionId, FeatureStreamDisconnectReason.PolicyRevoked))
                {
                    FeatureStreamLog.SubscriberAccessRevoked(_logger, item.SessionId);
                }
            }).ConfigureAwait(false);
    }

    private bool CanReuseRoutabilityRefresh(long now)
    {
        if (now >= Volatile.Read(ref _nextRoutabilityRefreshTimestamp))
        {
            return false;
        }

        return Volatile.Read(ref _lastRoutabilityRefreshSucceeded) == 0 || _routabilityGuard!.HasValidState;
    }

    private async ValueTask<MetadataV2GraphSnapshot> ReadScopedMetadataAsync(CancellationToken cancellationToken)
    {
        await using var scope = _serviceScopeFactory!.CreateAsyncScope();
        var provider = scope.ServiceProvider.GetRequiredService<IMetadataV2GraphProvider>();
        return await provider.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
    }

    public ClusterBroadcastBacklogSnapshot GetClusterBroadcastBacklogSnapshot()
    {
        lock (_clusterBroadcastLock)
        {
            return new ClusterBroadcastBacklogSnapshot(
                Configured: _redis is not null,
                Enabled: _clusterBroadcastEnabled,
                BacklogDepth: _clusterBroadcastBacklogCount,
                Dropped: _clusterBroadcastBacklogDropped);
        }
    }

    private void UpdateGaugeSnapshot()
        => _metrics.RecordGaugeSnapshot(
            Volatile.Read(ref _activeSessionCount),
            Volatile.Read(ref _clusterBroadcastBacklogCount));

    public void RecordReplayEventsDelivered(string transport, long count)
        => _metrics.RecordReplayEventsDelivered(transport, count);

    public FeatureStreamSession CreateSession(string transport, string? clientLabel)
        => CreateSession(transport, clientLabel, filter: null);

    public FeatureStreamSession CreateSession(string transport, string? clientLabel, IStreamSubscriptionFilter? filter = null)
        => TryCreateSession(transport, clientLabel, filter)
            ?? throw new InvalidOperationException(
                $"Feature stream session limit of {_options.Value.MaxConcurrentSessions} concurrent sessions reached.");

    public FeatureStreamSession? TryCreateSession(
        string transport,
        string? clientLabel,
        IStreamSubscriptionFilter? filter = null,
        bool addDefaultSubscription = true,
        string? admissionPartition = null)
    {
        admissionPartition ??= "anonymous";
        var opts = _options.Value;
        if (!TryReserveSessionSlot(admissionPartition, opts.MaxConcurrentSessions))
        {
            _metrics.RecordSessionRejected();
            return null;
        }

        var id = Guid.NewGuid();
        var channel = Channel.CreateBounded<FeatureStreamMessage>(new BoundedChannelOptions(opts.MaxBufferPerConnection)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleWriter = false,
            SingleReader = true
        });
        var cts = new CancellationTokenSource();
        var entry = new SessionEntry(id, channel, cts, DateTimeOffset.UtcNow, clientLabel, transport, filter, addDefaultSubscription, admissionPartition);
        if (!_sessions.TryAdd(id, entry))
        {
            ReleaseSessionSlot(admissionPartition);
            cts.Dispose();
            throw new InvalidOperationException("Failed to register feature stream session.");
        }

        FeatureStreamLog.SessionCreated(_logger, id, transport);
        _metrics.RecordSessionOpened(transport);
        UpdateGaugeSnapshot();
        return new FeatureStreamSession(id, channel.Reader, this, cts.Token);
    }

    private bool TryReserveSessionSlot(string partition, int maxConcurrentSessions)
    {
        lock (_admissionLock)
        {
            var count = _partitionSessionCounts.GetValueOrDefault(partition);
            if (count >= maxConcurrentSessions)
            {
                return false;
            }

            _partitionSessionCounts[partition] = count + 1;
            Interlocked.Increment(ref _activeSessionCount);
            return true;
        }
    }

    private void ReleaseSessionSlot(string partition)
    {
        lock (_admissionLock)
        {
            if (!_partitionSessionCounts.TryGetValue(partition, out var count))
            {
                return;
            }

            if (count == 1)
            {
                _partitionSessionCounts.Remove(partition);
            }
            else
            {
                _partitionSessionCounts[partition] = count - 1;
            }

            Interlocked.Decrement(ref _activeSessionCount);
        }
    }

    // Overflow inside the grace window is dropped without a disconnect.
    public void MarkDrainStarted(Guid sessionId, long minimumGrace = 0)
    {
        if (_sessions.TryGetValue(sessionId, out var entry))
        {
            // Set grace BEFORE the flag so concurrent Broadcast sees it immediately.
            var currentCount = entry.Channel.Reader.CanCount ? entry.Channel.Reader.Count : 0;
            entry.SetDrainGrace(Math.Max(currentCount, minimumGrace));
            entry.DrainStarted = true;
        }
    }

    public void ClearDrainGrace(Guid sessionId)
    {
        if (_sessions.TryGetValue(sessionId, out var entry))
        {
            entry.SetDrainGrace(0);
        }
    }

    public void RemoveSession(Guid sessionId, FeatureStreamDisconnectReason reason)
        => TryRemoveSession(sessionId, reason);

    private bool TryRemoveSession(Guid sessionId, FeatureStreamDisconnectReason reason)
    {
        if (!_sessions.TryRemove(sessionId, out var entry))
        {
            return false;
        }

        ReleaseSessionSlot(entry.AdmissionPartition);
        FeatureStreamLog.SessionRemoved(_logger, sessionId, reason);
        _metrics.RecordSessionClosed(entry.Transport, reason);
        if (reason == FeatureStreamDisconnectReason.SlowConsumer)
        {
            _metrics.RecordSlowConsumerDrop(entry.Transport);
        }

        UpdateGaugeSnapshot();
        entry.Cts.Cancel();
        entry.Cts.Dispose();
        return true;
    }

    public int Broadcast(FeatureStreamMessage message)
    {
        var delivered = BroadcastLocally(message);

        if (message.IsHeartbeat)
        {
            return delivered;
        }

        if (_redis is null)
        {
            // Single-node mode: there are no cross-node consumers, so skip the
            // cluster serialization and never accumulate a backlog that nothing
            // could ever flush.
            return delivered;
        }

        var payload = JsonSerializer.Serialize(
            new FeatureStreamBroadcastMessage
            {
                OriginInstanceId = _instanceId,
                Envelope = message.Envelope,
                GeometryEnvelope = message.GeometryEnvelope,
                PropertiesJson = message.PropertiesJson
            },
            FeatureStreamJsonContext.Default.FeatureStreamBroadcastMessage);

        lock (_clusterBroadcastLock)
        {
            if (_clusterBroadcastEnabled && _subscriber is not null)
            {
                TryFlushClusterBroadcastBacklogLocked();
                if (TryPublishClusterBroadcastLocked(payload))
                {
                    return delivered;
                }
            }

            EnqueueClusterBroadcastBacklogLocked(payload);
            TryRecoverClusterBroadcastLocked();
        }

        return delivered;
    }

    private bool TryPublishClusterBroadcastLocked(string payload)
    {
        if (!_clusterBroadcastEnabled || _subscriber is null)
        {
            return false;
        }

        try
        {
            // Fire-and-forget: Broadcast() runs synchronously on the feature-edit
            // hot path while holding the process-wide cluster lock, so it must not
            // block on a Redis round trip. Cross-node delivery is best-effort —
            // missed live events are recovered through the durable event store and
            // the cross-node poll.
            _subscriber.Publish(BroadcastChannel, payload, CommandFlags.FireAndForget);
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Intentional broad catch: this is a best-effort cross-node publish on the
            // feature-edit hot path. Failure falls back to the retry backlog instead of
            // propagating and interrupting the local broadcast.
            LogClusterBroadcastFailedOnce(ex);
            return false;
        }
    }

    // Callers must hold the lock while a retry is queued.
    private void EnqueueClusterBroadcastBacklogLocked(string payload)
    {
        _clusterBroadcastBacklog.Enqueue(payload);
        _clusterBroadcastBacklogCount++;
        UpdateGaugeSnapshot();

        while (_clusterBroadcastBacklogCount > MaxClusterBroadcastBacklog &&
               _clusterBroadcastBacklog.TryDequeue(out _))
        {
            _clusterBroadcastBacklogCount--;
            _clusterBroadcastBacklogDropped++;
            _metrics.RecordClusterBroadcastDropped();
            if (!_clusterBroadcastBacklogDropLogged)
            {
                _clusterBroadcastBacklogDropLogged = true;
                FeatureStreamLog.ClusterBroadcastBacklogOverflowed(
                    _logger,
                    MaxClusterBroadcastBacklog,
                    _clusterBroadcastBacklogDropped);
            }
        }
    }

    private int BroadcastLocally(FeatureStreamMessage message)
    {
        var delivered = 0;
        IReadOnlyDictionary<string, JsonElement>? parsedProperties = null;
        bool propertiesParsed = false;
        foreach (var (id, entry) in _sessions)
        {
            if (entry.Cts.IsCancellationRequested)
            {
                continue;
            }

            if (message.IsHeartbeat)
            {
                if (TryQueueMessage(id, entry, message))
                {
                    delivered++;
                }

                continue;
            }

            var matches = entry.GetMatchingSubscriptions(
                message.Envelope,
                message.GeometryEnvelope,
                message.PropertiesJson,
                ref parsedProperties,
                ref propertiesParsed);
            if (matches.Length == 0)
            {
                continue;
            }

            // Dedup is claimed at send time (writer task and per-subscription
            // replay), not here. Claiming the (event, subscription) slot before
            // queueing would mark events delivered that the bounded channel
            // later drops as pre-drain overflow — and replay would then skip
            // them too, losing the event entirely. Queue speculatively; the
            // writer/replay path with the atomic test-and-set decides who
            // actually sends. The generation observed at match time travels
            // with the frame so the writer can reject queued frames whose
            // subscription was unsubscribed or replaced before drain.
            var matchIndex = 0;
            while (matchIndex < matches.Length)
            {
                var match = matches[matchIndex];
                var subscriptionMessage = message with
                {
                    Envelope = (match.Envelope ?? message.Envelope) with { SubscriptionId = match.SubscriptionId },
                    SubscriptionGeneration = match.Generation
                };
                delivered += TryQueueMessage(id, entry, subscriptionMessage) ? 1 : 0;
                matchIndex++;
            }
        }

        return delivered;
    }

    private bool TryQueueMessage(Guid sessionId, SessionEntry entry, FeatureStreamMessage message)
    {
        if (!entry.Channel.Writer.TryWrite(message))
        {
            if (entry.DrainStarted)
            {
                if (entry.TryConsumeDrainGrace())
                {
                    // Still clearing inherited replay backlog — silently drop.
                    return false;
                }

                // Grace exhausted — genuine slow consumer.
                Interlocked.Increment(ref _slowConsumerDrops);
                FeatureStreamLog.SlowConsumerDropped(_logger, sessionId);
                RemoveSession(sessionId, FeatureStreamDisconnectReason.SlowConsumer);
            }

            // Pre-drain (replay window): silently drop to avoid disconnecting
            // a healthy reconnecting client. The drain dedup will handle overlap.
            return false;
        }

        if (!message.IsHeartbeat)
        {
            entry.UpdateLastQueuedCursor(message.Envelope.Cursor);
        }

        return true;
    }

    private void HandleClusterBroadcast(RedisChannel channel, RedisValue value)
    {
        if (!_clusterBroadcastEnabled || value.IsNullOrEmpty)
        {
            return;
        }

        try
        {
            var payload = JsonSerializer.Deserialize(
                value.ToString(),
                FeatureStreamJsonContext.Default.FeatureStreamBroadcastMessage);

            if (payload is null ||
                string.Equals(payload.OriginInstanceId, _instanceId, StringComparison.Ordinal))
            {
                return;
            }

            _ = HandleClusterBroadcastAsync(payload);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Intentional broad catch: this handles an inbound Redis pub/sub callback for
            // a cross-node broadcast. A malformed or unexpected payload must not throw back
            // into StackExchange.Redis's dispatch thread; log and drop the message.
            LogClusterBroadcastFailedOnce(ex);
        }
    }

    private async Task HandleClusterBroadcastAsync(FeatureStreamBroadcastMessage payload)
    {
        await _clusterBroadcastDispatchGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!await RefreshRoutabilityAsync().ConfigureAwait(false))
            {
                return;
            }

            BroadcastLocally(FeatureStreamMessage.Data(
                payload.Envelope,
                payload.GeometryEnvelope,
                payload.PropertiesJson));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            LogClusterBroadcastFailedOnce(ex);
        }
        finally
        {
            _clusterBroadcastDispatchGate.Release();
        }
    }

    private void TryRecoverClusterBroadcast()
    {
        try
        {
            lock (_clusterBroadcastLock)
            {
                TryRecoverClusterBroadcastLocked();
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Intentionally generic: this runs on the recurring cluster-broadcast recovery
            // timer callback. A single failed recovery attempt must not kill the timer or
            // the host; log and let the next tick retry.
            LogClusterBroadcastUnavailableOnce(ex);
        }
    }

    private void TryRecoverClusterBroadcastLocked()
    {
        if (_redis is null)
        {
            return;
        }

        if (!_clusterBroadcastEnabled)
        {
            if (Interlocked.Exchange(ref _clusterBroadcastRecoveryInProgress, 1) != 0)
            {
                return;
            }

            try
            {
                _subscriber ??= _redis.GetSubscriber();
                _subscriber.Subscribe(BroadcastChannel, HandleClusterBroadcast);
                _clusterBroadcastEnabled = true;

                // Re-arm the once-per-episode failure logs so the next outage is
                // visible to operators instead of degrading silently after the
                // first incident in the process lifetime.
                Interlocked.Exchange(ref _clusterBroadcastUnavailableLogged, 0);
                Interlocked.Exchange(ref _clusterBroadcastFailedLogged, 0);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Intentional broad catch: this is a best-effort re-subscribe attempt inside
                // the recovery loop; Redis may still be unavailable, so log and leave
                // cluster broadcast disabled for the next recovery tick to retry.
                LogClusterBroadcastUnavailableOnce(ex);
                return;
            }
            finally
            {
                Volatile.Write(ref _clusterBroadcastRecoveryInProgress, 0);
            }
        }

        TryFlushClusterBroadcastBacklogLocked();
    }

    private void TryFlushClusterBroadcastBacklogLocked()
    {
        if (!_clusterBroadcastEnabled || _subscriber is null)
        {
            return;
        }

        while (_clusterBroadcastBacklog.TryPeek(out var payload))
        {
            try
            {
                _subscriber.Publish(BroadcastChannel, payload, CommandFlags.FireAndForget);
                if (_clusterBroadcastBacklog.TryDequeue(out _))
                {
                    _clusterBroadcastBacklogCount--;
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Intentional broad catch: this is a per-payload attempt inside the backlog
                // flush loop; one failed publish must not abort the flush of the remaining
                // backlog entries, so log and leave the rest queued for the next attempt.
                LogClusterBroadcastFailedOnce(ex);
                return;
            }
        }

        // Backlog fully drained: re-arm the overflow log for the next episode.
        _clusterBroadcastBacklogDropLogged = false;
        UpdateGaugeSnapshot();
    }

    public void BroadcastHeartbeat()
    {
        var heartbeat = FeatureStreamMessage.Heartbeat();
        long sent = 0;
        foreach (var (id, entry) in _sessions)
        {
            if (entry.Cts.IsCancellationRequested)
            {
                continue;
            }

            if (!entry.Channel.Writer.TryWrite(heartbeat))
            {
                if (entry.DrainStarted)
                {
                    if (entry.TryConsumeDrainGrace())
                    {
                        continue;
                    }

                    Interlocked.Increment(ref _slowConsumerDrops);
                    FeatureStreamLog.SlowConsumerDropped(_logger, id);
                    RemoveSession(id, FeatureStreamDisconnectReason.SlowConsumer);
                }
            }
            else
            {
                Interlocked.Increment(ref _heartbeatsSent);
                sent++;
            }
        }

        _metrics.RecordHeartbeatsSent(sent);
    }

    public bool TryWriteToSession(Guid sessionId, FeatureStreamMessage message)
    {
        if (!_sessions.TryGetValue(sessionId, out var entry))
        {
            return false;
        }

        if (!entry.Channel.Writer.TryWrite(message))
        {
            Interlocked.Increment(ref _slowConsumerDrops);
            FeatureStreamLog.SlowConsumerDropped(_logger, sessionId);
            RemoveSession(sessionId, FeatureStreamDisconnectReason.SlowConsumer);
            return false;
        }

        entry.UpdateLastQueuedCursor(message.Envelope.Cursor);
        return true;
    }

    // The caller must pin the generation, and live fan-out waits until replay finishes so the two do not interleave.
    public AddSubscriptionOutcome TryAddSubscription(Guid sessionId, string subscriptionId, IStreamSubscriptionFilter? filter, bool paused = false)
    {
        if (!_sessions.TryGetValue(sessionId, out var entry))
        {
            return new AddSubscriptionOutcome(AddSubscriptionResult.SessionGone, 0);
        }

        return entry.TryAddSubscription(subscriptionId, filter, paused, _options.Value.MaxSubscriptionsPerSession, out var generation)
            ? new AddSubscriptionOutcome(AddSubscriptionResult.Added, generation)
            : new AddSubscriptionOutcome(AddSubscriptionResult.LimitReached, 0);
    }

    public long GetDefaultSubscriptionGeneration(Guid sessionId)
        => _sessions.TryGetValue(sessionId, out var entry) ? entry.DefaultSubscriptionGeneration : 0;

    public bool TryUnpauseSubscription(Guid sessionId, string subscriptionId)
    {
        if (!_sessions.TryGetValue(sessionId, out var entry))
        {
            return false;
        }

        return entry.SetSubscriptionPaused(subscriptionId, paused: false);
    }

    public bool TryRememberSubscriptionDelivery(Guid sessionId, string subscriptionId, string eventId)
    {
        if (!_sessions.TryGetValue(sessionId, out var entry))
        {
            return false;
        }

        return entry.TryRememberEvent(string.Concat(eventId, ":", subscriptionId));
    }

    // A replaced subscription drops the queued frame without claiming delivery, so a later replay can still deliver it.
    public SubscriptionDeliveryClaim TryClaimSubscriptionDelivery(Guid sessionId, string subscriptionId, long generation, string eventId)
    {
        if (!_sessions.TryGetValue(sessionId, out var entry))
        {
            return SubscriptionDeliveryClaim.StaleGeneration;
        }

        return entry.TryClaimSubscriptionDelivery(subscriptionId, generation, eventId);
    }

    public PollableSubscription[] GetActivePollableSubscriptions(Guid sessionId)
    {
        return _sessions.TryGetValue(sessionId, out var entry)
            ? entry.GetActivePollableSubscriptions()
            : [];
    }

    public bool TryAdvanceSubscriptionPollCursor(Guid sessionId, string subscriptionId, long generation, long cursor)
    {
        return _sessions.TryGetValue(sessionId, out var entry)
            && entry.TryAdvanceSubscriptionPollCursor(subscriptionId, generation, cursor);
    }

    // The sequence is allocated immediately before the write so a filtered cursor does not burn a number.
    public long NextSubscriptionSequence(Guid sessionId, string subscriptionId, long generation = 0)
    {
        return _sessions.TryGetValue(sessionId, out var entry)
            && entry.TryNextSubscriptionSequence(subscriptionId, generation, out var sequence)
            ? sequence
            : -1;
    }

    public bool TryRemoveSubscription(Guid sessionId, string subscriptionId)
    {
        if (!_sessions.TryGetValue(sessionId, out var entry))
        {
            return false;
        }

        return entry.RemoveSubscription(subscriptionId);
    }

    public bool DisconnectSession(Guid sessionId)
        => TryRemoveSession(sessionId, FeatureStreamDisconnectReason.AdminDisconnect);

    public IReadOnlyList<FeatureStreamSessionInfo> GetSessions()
    {
        return _sessions.Select(kvp =>
        {
            var streamFilter = kvp.Value.FirstStreamSubscriptionFilter;
            return new FeatureStreamSessionInfo(
                kvp.Key,
                kvp.Value.ConnectedAt,
                kvp.Value.ClientLabel,
                kvp.Value.Transport,
                kvp.Value.LastQueuedCursor,
                kvp.Value.HasFilter,
                kvp.Value.FilterSummary,
                streamFilter?.ServiceId,
                streamFilter?.LayerIds?.ToArray());
        }).ToArray();
    }

    public void Dispose()
    {
        _clusterBroadcastRecoveryTimer?.Dispose();
        if (_subscriber is not null)
        {
            try
            {
                _subscriber.Unsubscribe(BroadcastChannel, HandleClusterBroadcast);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Intentional broad catch: best-effort shutdown; the connection owner will
                // close the Redis subscription anyway.
                FeatureStreamLog.ClusterUnsubscribeFailed(_logger, ex);
            }
        }

        foreach (var (_, entry) in _sessions)
        {
            entry.Cts.Cancel();
            entry.Cts.Dispose();
        }

        _sessions.Clear();
        lock (_admissionLock)
        {
            _partitionSessionCounts.Clear();
            Interlocked.Exchange(ref _activeSessionCount, 0);
        }
    }

    private void LogClusterBroadcastUnavailableOnce(Exception exception)
    {
        if (Interlocked.Exchange(ref _clusterBroadcastUnavailableLogged, 1) == 0)
        {
            FeatureStreamLog.ClusterBroadcastUnavailable(_logger, exception);
        }
    }

    private void LogClusterBroadcastFailedOnce(Exception exception)
    {
        if (Interlocked.Exchange(ref _clusterBroadcastFailedLogged, 1) == 0)
        {
            FeatureStreamLog.ClusterBroadcastFailed(_logger, exception);
        }
    }

    private sealed class SessionEntry
    {
        private long _lastQueuedCursor;
        private long _drainGraceRemaining;
        private long _subscriptionGenerationCounter;
        private readonly Queue<string> _recentEventIds = new();
        private readonly HashSet<string> _recentEventIdSet = new(StringComparer.Ordinal);
        private readonly object _recentEventLock = new();
        private readonly Dictionary<string, SubscriptionState> _subscriptions = new(StringComparer.Ordinal);
        private readonly object _subscriptionLock = new();

        public SessionEntry(
            Guid id,
            Channel<FeatureStreamMessage> channel,
            CancellationTokenSource cts,
            DateTimeOffset connectedAt,
            string? clientLabel,
            string transport,
            IStreamSubscriptionFilter? subscriptionFilter = null,
            bool addDefaultSubscription = true,
            string admissionPartition = "anonymous")
        {
            Id = id;
            Channel = channel;
            Cts = cts;
            ConnectedAt = connectedAt;
            ClientLabel = clientLabel;
            Transport = transport;
            AdmissionPartition = admissionPartition;
            if (addDefaultSubscription)
            {
                var generation = ++_subscriptionGenerationCounter;
                _subscriptions[FeatureStreamSessionManager.DefaultSubscriptionId] =
                    new SubscriptionState(subscriptionFilter, Paused: false, Generation: generation, LastPolledCursor: 0);
                DefaultSubscriptionGeneration = generation;
            }
        }

        private sealed record SubscriptionState(
            IStreamSubscriptionFilter? Filter,
            bool Paused,
            long Generation,
            long LastPolledCursor,
            long NextSequence = 0);

        public long DefaultSubscriptionGeneration { get; }

        public Guid Id { get; }
        public Channel<FeatureStreamMessage> Channel { get; }
        public CancellationTokenSource Cts { get; }
        public DateTimeOffset ConnectedAt { get; }
        public string? ClientLabel { get; }
        public string Transport { get; }
        public string AdmissionPartition { get; }
        public bool HasSubscriptions
        {
            get
            {
                lock (_subscriptionLock)
                {
                    return _subscriptions.Count > 0;
                }
            }
        }

        public bool HasFilter
        {
            get
            {
                lock (_subscriptionLock)
                {
                    return _subscriptions.Values.Any(state => state.Filter is not null);
                }
            }
        }

        public string? FilterSummary
        {
            get
            {
                lock (_subscriptionLock)
                {
                    return string.Join("; ", _subscriptions.Select(s =>
                        s.Value.Filter is null ? $"{s.Key}: all" : $"{s.Key}: {s.Value.Filter.Summary}"));
                }
            }
        }

        public StreamSubscriptionFilter? FirstStreamSubscriptionFilter
        {
            get
            {
                lock (_subscriptionLock)
                {
                    return _subscriptions.Values
                        .Select(state => state.Filter)
                        .OfType<StreamSubscriptionFilter>()
                        .FirstOrDefault();
                }
            }
        }

        public StreamSubscriberSecurity[] GetRefreshableSubscriberSecurities()
        {
            lock (_subscriptionLock)
            {
                return _subscriptions.Values
                    .Select(state => (state.Filter as StreamSubscriptionFilter)?.SubscriberSecurity)
                    .OfType<StreamSubscriberSecurity>()
                    .Where(static security => security.IsRefreshable)
                    .Distinct()
                    .ToArray();
            }
        }

        public volatile bool DrainStarted;
        public long LastQueuedCursor => Interlocked.Read(ref _lastQueuedCursor);

        public void UpdateLastQueuedCursor(long cursor)
        {
            Interlocked.Exchange(ref _lastQueuedCursor, cursor);
        }

        public void SetDrainGrace(long count)
        {
            Interlocked.Exchange(ref _drainGraceRemaining, count);
        }

        public bool TryConsumeDrainGrace()
        {
            return Interlocked.Decrement(ref _drainGraceRemaining) >= 0;
        }

        public bool TryRememberEvent(string eventId)
        {
            lock (_recentEventLock)
            {
                return TryRememberEventLocked(eventId);
            }
        }

        private bool TryRememberEventLocked(string eventId)
        {
            if (!_recentEventIdSet.Add(eventId))
            {
                return false;
            }

            _recentEventIds.Enqueue(eventId);
            while (_recentEventIds.Count > RecentEventIdCapacity &&
                   _recentEventIds.TryDequeue(out var expired))
            {
                _recentEventIdSet.Remove(expired);
            }

            return true;
        }

        public bool TryAddSubscription(
            string subscriptionId,
            IStreamSubscriptionFilter? filter,
            bool paused,
            int maxSubscriptions,
            out long generation)
        {
            lock (_subscriptionLock)
            {
                // Replacing an existing id keeps the count flat — only count new ids
                // against the cap so a paused-then-resubscribe handshake on the same
                // id remains stable.
                if (!_subscriptions.ContainsKey(subscriptionId) && _subscriptions.Count >= maxSubscriptions)
                {
                    generation = 0;
                    return false;
                }

                generation = ++_subscriptionGenerationCounter;
                _subscriptions[subscriptionId] = new SubscriptionState(filter, paused, generation, LastPolledCursor: 0);
                return true;
            }
        }

        public bool SetSubscriptionPaused(string subscriptionId, bool paused)
        {
            lock (_subscriptionLock)
            {
                if (!_subscriptions.TryGetValue(subscriptionId, out var current))
                {
                    return false;
                }

                _subscriptions[subscriptionId] = current with { Paused = paused };
                return true;
            }
        }

        public bool RemoveSubscription(string subscriptionId)
        {
            lock (_subscriptionLock)
            {
                return _subscriptions.Remove(subscriptionId);
            }
        }

        // A replaced generation drops the queued frame without claiming delivery, so a later replay can still deliver it.
        public SubscriptionDeliveryClaim TryClaimSubscriptionDelivery(string subscriptionId, long generation, string eventId)
        {
            lock (_subscriptionLock)
            {
                if (!_subscriptions.TryGetValue(subscriptionId, out var state) ||
                    state.Generation != generation)
                {
                    return SubscriptionDeliveryClaim.StaleGeneration;
                }

                lock (_recentEventLock)
                {
                    var deliveryKey = string.Concat(
                        eventId,
                        ":",
                        subscriptionId,
                        ":",
                        generation.ToString(CultureInfo.InvariantCulture));
                    return TryRememberEventLocked(deliveryKey)
                        ? SubscriptionDeliveryClaim.Claimed
                        : SubscriptionDeliveryClaim.AlreadyDelivered;
                }
            }
        }

        // The sequence is assigned at send time, so a frame that is filtered or fenced does not burn a number.
        public bool TryNextSubscriptionSequence(string subscriptionId, long generation, out long sequence)
        {
            lock (_subscriptionLock)
            {
                if (!_subscriptions.TryGetValue(subscriptionId, out var current) ||
                    (generation > 0 && current.Generation != generation))
                {
                    sequence = -1;
                    return false;
                }

                sequence = current.NextSequence;
                _subscriptions[subscriptionId] = current with { NextSequence = sequence + 1 };
                return true;
            }
        }

        public PollableSubscription[] GetActivePollableSubscriptions()
        {
            lock (_subscriptionLock)
            {
                if (_subscriptions.Count == 0)
                {
                    return [];
                }

                var snapshot = new List<PollableSubscription>(_subscriptions.Count);
                foreach (var (subscriptionId, state) in _subscriptions)
                {
                    if (state.Paused)
                    {
                        continue;
                    }

                    if (string.Equals(subscriptionId, FeatureStreamSessionManager.DefaultSubscriptionId, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    snapshot.Add(new PollableSubscription(
                        subscriptionId,
                        state.Generation,
                        state.Filter,
                        state.LastPolledCursor));
                }

                return snapshot.ToArray();
            }
        }

        public bool TryAdvanceSubscriptionPollCursor(string subscriptionId, long generation, long cursor)
        {
            lock (_subscriptionLock)
            {
                if (!_subscriptions.TryGetValue(subscriptionId, out var current) ||
                    current.Generation != generation)
                {
                    return false;
                }

                if (cursor <= current.LastPolledCursor)
                {
                    return false;
                }

                _subscriptions[subscriptionId] = current with { LastPolledCursor = cursor };
                return true;
            }
        }

        public SubscriptionMatch[] GetMatchingSubscriptions(
            FeatureStreamEnvelope envelope,
            double[]? geometryEnvelope,
            string? propertiesJson,
            ref IReadOnlyDictionary<string, JsonElement>? parsedProperties,
            ref bool propertiesParsed)
        {
            KeyValuePair<string, SubscriptionState>[] subscriptions;
            lock (_subscriptionLock)
            {
                if (_subscriptions.Count == 0)
                {
                    return [];
                }

                subscriptions = _subscriptions.ToArray();
            }

            var matches = new List<SubscriptionMatch>(subscriptions.Length);
            foreach (var (subscriptionId, state) in subscriptions)
            {
                // Paused subscriptions still exist (so the writer-task drain knows about them),
                // but the live broadcaster must not queue events while replay is delivering
                // the same cursor range directly through the per-subscription replay path.
                if (state.Paused)
                {
                    continue;
                }

                if (state.Filter is null)
                {
                    matches.Add(new SubscriptionMatch(subscriptionId, state.Generation));
                    continue;
                }

                var matched = state.Filter is StreamSubscriptionFilter streamFilter
                    ? streamFilter.Matches(
                        envelope,
                        geometryEnvelope,
                        propertiesJson,
                        ref parsedProperties,
                        ref propertiesParsed)
                    : state.Filter.Matches(envelope, geometryEnvelope, propertiesJson);

                if (matched)
                {
                    matches.Add(new SubscriptionMatch(subscriptionId, state.Generation,
                        state.Filter is StreamSubscriptionFilter securedFilter ? securedFilter.Project(envelope) : envelope));
                }
            }

            return matches.ToArray();
        }
    }

    internal readonly record struct SubscriptionMatch(string SubscriptionId, long Generation, FeatureStreamEnvelope? Envelope = null);

    internal readonly record struct PollableSubscription(
        string SubscriptionId,
        long Generation,
        IStreamSubscriptionFilter? Filter,
        long LastPolledCursor);
}

internal sealed class FeatureStreamSession : IDisposable
{
    private readonly FeatureStreamSessionManager _manager;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public FeatureStreamSession(
        Guid sessionId,
        ChannelReader<FeatureStreamMessage> reader,
        FeatureStreamSessionManager manager,
        CancellationToken disconnectToken)
    {
        SessionId = sessionId;
        Reader = reader;
        DisconnectToken = disconnectToken;
        _manager = manager;
    }

    public Guid SessionId { get; }

    public ChannelReader<FeatureStreamMessage> Reader { get; }

    public CancellationToken DisconnectToken { get; }

    // Only one socket send is in flight, and replay and the live drain share that lock.
    public SemaphoreSlim WriteLock => _writeLock;

    public void Dispose()
    {
        _manager.RemoveSession(SessionId, FeatureStreamDisconnectReason.ClientClosed);
        _writeLock.Dispose();
    }
}

internal readonly record struct FeatureStreamMessage
{
    public bool IsHeartbeat { get; init; }

    public FeatureStreamEnvelope Envelope { get; init; }

    public double[]? GeometryEnvelope { get; init; }

    public string? PropertiesJson { get; init; }

    public long SubscriptionGeneration { get; init; }

    public static FeatureStreamMessage Data(
        FeatureStreamEnvelope envelope,
        double[]? geometryEnvelope = null,
        string? propertiesJson = null) =>
        new()
        {
            IsHeartbeat = false,
            Envelope = envelope,
            GeometryEnvelope = geometryEnvelope,
            PropertiesJson = propertiesJson
        };

    public static FeatureStreamMessage Heartbeat() =>
        new() { IsHeartbeat = true };
}

internal sealed record FeatureStreamBroadcastMessage
{
    public required string OriginInstanceId { get; init; }

    public required FeatureStreamEnvelope Envelope { get; init; }

    public double[]? GeometryEnvelope { get; init; }

    public string? PropertiesJson { get; init; }
}
