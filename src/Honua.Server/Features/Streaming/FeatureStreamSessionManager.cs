// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Honua.Server.Features.Streaming;

/// <summary>
/// Manages feature-stream sessions with bounded channels, heartbeat, and slow-consumer disconnect.
/// Each connected client (WebSocket or SSE) gets a session backed by a bounded channel.
/// </summary>
internal sealed class FeatureStreamSessionManager : IDisposable
{
    internal const string DefaultSubscriptionId = "default";
    private static readonly RedisChannel BroadcastChannel = new("featurechange:stream:broadcast", RedisChannel.PatternMode.Literal);
    private static readonly TimeSpan ClusterBroadcastRecoveryInterval = TimeSpan.FromSeconds(5);
    private const int RecentEventIdCapacity = 128;
    private readonly ConcurrentDictionary<Guid, SessionEntry> _sessions = new();
    private readonly ConcurrentQueue<string> _clusterBroadcastBacklog = new();
    private readonly object _clusterBroadcastLock = new();
    private readonly IOptions<FeatureStreamOptions> _options;
    private readonly ILogger<FeatureStreamSessionManager> _logger;
    private readonly IConnectionMultiplexer? _redis;
    private ISubscriber? _subscriber;
    private readonly Timer? _clusterBroadcastRecoveryTimer;
    private readonly string _instanceId = Guid.NewGuid().ToString("N");
    private bool _clusterBroadcastEnabled;
    private int _activeSessionCount;
    private long _slowConsumerDrops;
    private long _heartbeatsSent;
    private int _clusterBroadcastUnavailableLogged;
    private int _clusterBroadcastFailedLogged;
    private int _clusterBroadcastRecoveryInProgress;

    public FeatureStreamSessionManager(
        IOptions<FeatureStreamOptions> options,
        ILogger<FeatureStreamSessionManager> logger,
        IConnectionMultiplexer? redis = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _redis = redis;

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
        catch (Exception ex)
        {
            LogClusterBroadcastUnavailableOnce(ex);
        }

        _clusterBroadcastRecoveryTimer = new Timer(
            _ => TryRecoverClusterBroadcast(),
            null,
            ClusterBroadcastRecoveryInterval,
            ClusterBroadcastRecoveryInterval);
    }

    /// <summary>
    /// Total slow-consumer disconnections since startup.
    /// </summary>
    public long SlowConsumerDrops => Interlocked.Read(ref _slowConsumerDrops);

    /// <summary>
    /// Total heartbeat frames sent since startup.
    /// </summary>
    public long HeartbeatsSent => Interlocked.Read(ref _heartbeatsSent);

    /// <summary>
    /// Current number of active sessions (non-allocating).
    /// </summary>
    public int SessionCount => Volatile.Read(ref _activeSessionCount);

    /// <summary>
    /// Creates a new session and returns its channel reader for the transport loop.
    /// Live broadcasts are queued into the bounded channel immediately. When a session
    /// is replaying, the transport writes replay events directly while the channel
    /// accumulates live events; the drain loop deduplicates using the replay cursor.
    /// </summary>
    public FeatureStreamSession CreateSession(string transport, string? clientLabel)
        => CreateSession(transport, clientLabel, filter: null);

    public FeatureStreamSession CreateSession(string transport, string? clientLabel, IStreamSubscriptionFilter? filter = null)
        => TryCreateSession(transport, clientLabel, filter)
            ?? throw new InvalidOperationException(
                $"Feature stream session limit of {_options.Value.MaxConcurrentSessions} concurrent sessions reached.");

    /// <summary>
    /// Attempts to create a new session. Returns null when the global concurrent-session
    /// cap has been reached.
    /// </summary>
    public FeatureStreamSession? TryCreateSession(
        string transport,
        string? clientLabel,
        IStreamSubscriptionFilter? filter = null,
        bool addDefaultSubscription = true)
    {
        var opts = _options.Value;
        if (!TryReserveSessionSlot(opts.MaxConcurrentSessions))
        {
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
        var entry = new SessionEntry(id, channel, cts, DateTimeOffset.UtcNow, clientLabel, transport, filter, addDefaultSubscription);
        if (!_sessions.TryAdd(id, entry))
        {
            ReleaseSessionSlot();
            cts.Dispose();
            throw new InvalidOperationException("Failed to register feature stream session.");
        }

        FeatureStreamLog.SessionCreated(_logger, id, transport);
        return new FeatureStreamSession(id, channel.Reader, this, cts.Token);
    }

    private bool TryReserveSessionSlot(int maxConcurrentSessions)
    {
        while (true)
        {
            var currentCount = Volatile.Read(ref _activeSessionCount);
            if (currentCount >= maxConcurrentSessions)
            {
                return false;
            }

            if (Interlocked.CompareExchange(ref _activeSessionCount, currentCount + 1, currentCount) == currentCount)
            {
                return true;
            }
        }
    }

    private void ReleaseSessionSlot()
    {
        Interlocked.Decrement(ref _activeSessionCount);
    }

    /// <summary>
    /// Marks a session's drain loop as active. Grace is set to the larger of the current
    /// channel depth and <paramref name="minimumGrace"/>. Overflow events within the grace
    /// window are silently dropped (no disconnect); once exhausted, a full channel triggers
    /// a slow-consumer disconnect.
    /// </summary>
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

    /// <summary>
    /// Resets the drain grace window to zero so any subsequent overflow is treated
    /// as a genuine slow consumer. Call after the replay handoff is complete and
    /// the live drain loop is about to start.
    /// </summary>
    public void ClearDrainGrace(Guid sessionId)
    {
        if (_sessions.TryGetValue(sessionId, out var entry))
        {
            entry.SetDrainGrace(0);
        }
    }

    /// <summary>
    /// Removes a session. Called by the transport loop on disconnect.
    /// </summary>
    public void RemoveSession(Guid sessionId, FeatureStreamDisconnectReason reason)
    {
        if (_sessions.TryRemove(sessionId, out var entry))
        {
            ReleaseSessionSlot();
            FeatureStreamLog.SessionRemoved(_logger, sessionId, reason);
            entry.Cts.Cancel();
            entry.Cts.Dispose();
        }
    }

    /// <summary>
    /// Fan-out a live event to all connected sessions. When a session's bounded channel
    /// is full: pre-drain overflows are silently dropped; post-drain overflows consume a
    /// grace window (equal to the inherited replay-era backlog depth) before triggering a
    /// slow-consumer disconnect. Sessions with subscription filters only receive matching events.
    /// Returns the number of local sessions that accepted the message; cross-node fan-out is
    /// handled separately through Redis.
    /// </summary>
    public int Broadcast(FeatureStreamMessage message)
    {
        var delivered = BroadcastLocally(message);

        if (message.IsHeartbeat)
        {
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

            _clusterBroadcastBacklog.Enqueue(payload);
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
            _subscriber.Publish(BroadcastChannel, payload);
            return true;
        }
        catch (Exception ex)
        {
            LogClusterBroadcastFailedOnce(ex);
            return false;
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

            foreach (var match in matches)
            {
                // Dedup is claimed at send time (writer task and per-subscription
                // replay), not here. Claiming the (event, subscription) slot before
                // queueing would mark events delivered that the bounded channel
                // later drops as pre-drain overflow — and replay would then skip
                // them too, losing the event entirely. Queue speculatively; the
                // writer/replay path with the atomic test-and-set decides who
                // actually sends. The generation observed at match time travels
                // with the frame so the writer can reject queued frames whose
                // subscription was unsubscribed or replaced before drain.
                var subscriptionMessage = message with
                {
                    Envelope = message.Envelope with { SubscriptionId = match.SubscriptionId },
                    SubscriptionGeneration = match.Generation
                };
                if (TryQueueMessage(id, entry, subscriptionMessage))
                {
                    delivered++;
                }
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

            BroadcastLocally(FeatureStreamMessage.Data(
                payload.Envelope,
                payload.GeometryEnvelope,
                payload.PropertiesJson));
        }
        catch (Exception ex)
        {
            LogClusterBroadcastFailedOnce(ex);
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
        catch (Exception ex)
        {
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
            }
            catch (Exception ex)
            {
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
                _subscriber.Publish(BroadcastChannel, payload);
                _clusterBroadcastBacklog.TryDequeue(out _);
            }
            catch (Exception ex)
            {
                LogClusterBroadcastFailedOnce(ex);
                return;
            }
        }
    }

    /// <summary>
    /// Sends a heartbeat to all connected sessions.
    /// </summary>
    public void BroadcastHeartbeat()
    {
        var heartbeat = FeatureStreamMessage.Heartbeat();
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
            }
        }
    }

    /// <summary>
    /// Writes a message directly to a specific session's channel (used for replay).
    /// </summary>
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

    /// <summary>
    /// Registers a subscription on an existing WebSocket session and returns the generation
    /// assigned to it. The generation is monotonic per session and changes on every add
    /// (including same-id replacement), so the caller must pin the returned generation when
    /// issuing replay or send-time delivery claims for this subscription. Frames stamped with
    /// an older generation are rejected at send time, fencing stale queued frames after
    /// unsubscribe or replacement.
    ///
    /// When <paramref name="paused"/> is true, the broadcast fan-out skips this subscription
    /// until <see cref="TryUnpauseSubscription"/> is called. This lets the subscribe handler
    /// replay missed events from the durable store without the live channel writer
    /// interleaving frames at the same cursor range.
    ///
    /// Returns <see cref="AddSubscriptionResult.SessionGone"/> when the session is closed
    /// and <see cref="AddSubscriptionResult.LimitReached"/> when the per-session cap is hit;
    /// the caller should reject the subscribe frame with a client-safe error.
    /// </summary>
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

    /// <summary>
    /// Returns the generation assigned to the default subscription on the named session, or
    /// 0 if the session does not exist or has no default subscription. Used by the WebSocket
    /// writer's replay callbacks for the default subscription (which is never replaced and so
    /// has a stable generation for the life of the session).
    /// </summary>
    public long GetDefaultSubscriptionGeneration(Guid sessionId)
        => _sessions.TryGetValue(sessionId, out var entry) ? entry.DefaultSubscriptionGeneration : 0;

    /// <summary>
    /// Clears the broadcast-paused flag on a subscription so live events resume queueing.
    /// </summary>
    public bool TryUnpauseSubscription(Guid sessionId, string subscriptionId)
    {
        if (!_sessions.TryGetValue(sessionId, out var entry))
        {
            return false;
        }

        return entry.SetSubscriptionPaused(subscriptionId, paused: false);
    }

    /// <summary>
    /// Atomically records that an event was delivered for a specific subscription
    /// on a session. Returns true if the caller should send the event, false if it
    /// was already recorded (caller should skip to avoid duplicate delivery).
    /// Used by the per-subscription replay path which writes directly to the
    /// transport, bypassing the channel/broadcast dedup. Does not check generation —
    /// callers that own the subscription's current generation should prefer
    /// <see cref="TryClaimSubscriptionDelivery"/>, which atomically rejects stale-
    /// generation frames before claiming dedup.
    /// </summary>
    public bool TryRememberSubscriptionDelivery(Guid sessionId, string subscriptionId, string eventId)
    {
        if (!_sessions.TryGetValue(sessionId, out var entry))
        {
            return false;
        }

        return entry.TryRememberEvent(string.Concat(eventId, ":", subscriptionId));
    }

    /// <summary>
    /// Atomically verifies the subscription still exists with the supplied generation and,
    /// if so, claims the (event, subscription) delivery slot. Returns
    /// <see cref="SubscriptionDeliveryClaim.StaleGeneration"/> when the session is gone or
    /// the subscription was removed or replaced (so callers must drop the queued frame
    /// without claiming dedup), and <see cref="SubscriptionDeliveryClaim.AlreadyDelivered"/>
    /// when a concurrent send-time path already claimed the slot. Used by the WebSocket writer
    /// and per-subscription replay so stale frames queued before an unsubscribe or same-id
    /// resubscribe are dropped without claiming dedup, leaving future replay free to deliver
    /// the event under the new generation.
    /// </summary>
    public SubscriptionDeliveryClaim TryClaimSubscriptionDelivery(Guid sessionId, string subscriptionId, long generation, string eventId)
    {
        if (!_sessions.TryGetValue(sessionId, out var entry))
        {
            return SubscriptionDeliveryClaim.StaleGeneration;
        }

        return entry.TryClaimSubscriptionDelivery(subscriptionId, generation, eventId);
    }

    /// <summary>
    /// Removes a subscription from an existing WebSocket session.
    /// </summary>
    public bool TryRemoveSubscription(Guid sessionId, string subscriptionId)
    {
        if (!_sessions.TryGetValue(sessionId, out var entry))
        {
            return false;
        }

        return entry.RemoveSubscription(subscriptionId);
    }

    /// <summary>
    /// Force-disconnect a session (admin action).
    /// </summary>
    public bool DisconnectSession(Guid sessionId)
    {
        if (!_sessions.TryRemove(sessionId, out var entry))
        {
            return false;
        }

        ReleaseSessionSlot();
        entry.Cts.Cancel();
        entry.Cts.Dispose();
        FeatureStreamLog.SessionRemoved(_logger, sessionId, FeatureStreamDisconnectReason.AdminDisconnect);
        return true;
    }

    /// <summary>
    /// Returns a snapshot of all active sessions for admin/health visibility.
    /// </summary>
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
            catch
            {
                // Best-effort shutdown; the connection owner will close the Redis subscription anyway.
            }
        }

        foreach (var (_, entry) in _sessions)
        {
            entry.Cts.Cancel();
            entry.Cts.Dispose();
        }

        _sessions.Clear();
        Interlocked.Exchange(ref _activeSessionCount, 0);
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
            bool addDefaultSubscription = true)
        {
            Id = id;
            Channel = channel;
            Cts = cts;
            ConnectedAt = connectedAt;
            ClientLabel = clientLabel;
            Transport = transport;
            if (addDefaultSubscription)
            {
                var generation = ++_subscriptionGenerationCounter;
                _subscriptions[FeatureStreamSessionManager.DefaultSubscriptionId] =
                    new SubscriptionState(subscriptionFilter, Paused: false, Generation: generation);
                DefaultSubscriptionGeneration = generation;
            }
        }

        private sealed record SubscriptionState(IStreamSubscriptionFilter? Filter, bool Paused, long Generation);

        /// <summary>
        /// Generation assigned to the default subscription at session creation. Stable for the
        /// life of the session because the default subscription is never replaced (control-frame
        /// subscribes always create distinct ids).
        /// </summary>
        public long DefaultSubscriptionGeneration { get; }

        public Guid Id { get; }
        public Channel<FeatureStreamMessage> Channel { get; }
        public CancellationTokenSource Cts { get; }
        public DateTimeOffset ConnectedAt { get; }
        public string? ClientLabel { get; }
        public string Transport { get; }
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
        public volatile bool DrainStarted;
        public long LastQueuedCursor => Interlocked.Read(ref _lastQueuedCursor);

        public void UpdateLastQueuedCursor(long cursor)
        {
            Interlocked.Exchange(ref _lastQueuedCursor, cursor);
        }

        /// <summary>
        /// Sets the number of post-drain overflow events to tolerate before
        /// treating a full channel as a genuine slow consumer.
        /// </summary>
        public void SetDrainGrace(long count)
        {
            Interlocked.Exchange(ref _drainGraceRemaining, count);
        }

        /// <summary>
        /// Returns true if grace remains (overflow is tolerated); false when exhausted.
        /// </summary>
        public bool TryConsumeDrainGrace()
        {
            return Interlocked.Decrement(ref _drainGraceRemaining) >= 0;
        }

        /// <summary>
        /// Atomically records that an event was delivered for this session.
        /// Returns true if the event is newly recorded (caller should deliver),
        /// false if it was already recorded (caller should skip to avoid duplicate
        /// delivery). Used by both the broadcast fan-out and the per-subscription
        /// replay path to keep the replay-to-live handoff exactly-once.
        /// </summary>
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

        /// <summary>
        /// Adds or replaces a subscription, allocating a new generation each call. Callers must
        /// pin the returned generation when issuing later replay or send-time delivery claims so
        /// stale frames queued before a replacement or unsubscribe are rejected at send time.
        /// </summary>
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
                _subscriptions[subscriptionId] = new SubscriptionState(filter, paused, generation);
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

        /// <summary>
        /// Atomically verifies the subscription still exists with the supplied generation and,
        /// if so, claims the (event, subscription) delivery slot. Returns
        /// <see cref="SubscriptionDeliveryClaim.StaleGeneration"/> when the generation no
        /// longer matches (subscription removed or replaced) — the caller must drop the queued
        /// frame without sending and without claiming dedup, so a future replay for the same
        /// id can still deliver the event under a new generation. Returns
        /// <see cref="SubscriptionDeliveryClaim.AlreadyDelivered"/> when a concurrent send-time
        /// path already claimed the slot (benign — the other path is sending the frame).
        /// </summary>
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
                    return TryRememberEventLocked(string.Concat(eventId, ":", subscriptionId))
                        ? SubscriptionDeliveryClaim.Claimed
                        : SubscriptionDeliveryClaim.AlreadyDelivered;
                }
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
                    matches.Add(new SubscriptionMatch(subscriptionId, state.Generation));
                }
            }

            return matches.ToArray();
        }
    }

    /// <summary>
    /// Subscription match returned by <see cref="SessionEntry.GetMatchingSubscriptions"/>:
    /// id plus the generation observed at match time. The generation travels with the
    /// queued frame so the writer can verify the subscription has not been replaced or
    /// removed before sending.
    /// </summary>
    internal readonly record struct SubscriptionMatch(string SubscriptionId, long Generation);
}

/// <summary>
/// Handle returned to the transport loop. The reader drains messages; the disconnect token
/// signals when the session has been removed.
/// </summary>
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

    /// <summary>
    /// Unique session identifier.
    /// </summary>
    public Guid SessionId { get; }

    /// <summary>
    /// Channel reader for the transport loop to consume messages.
    /// </summary>
    public ChannelReader<FeatureStreamMessage> Reader { get; }

    /// <summary>
    /// Fires when the session is removed (admin disconnect, slow consumer, etc.).
    /// </summary>
    public CancellationToken DisconnectToken { get; }

    /// <summary>
    /// Serializes WebSocket transport writes for this session. The transport spec
    /// requires that only one <c>SendAsync</c> is in flight at a time, and per-subscription
    /// replay must be atomic against live channel drains; both paths acquire this lock
    /// before sending so cursors stay ordered and frames never interleave on the wire.
    /// </summary>
    public SemaphoreSlim WriteLock => _writeLock;

    public void Dispose()
    {
        _manager.RemoveSession(SessionId, FeatureStreamDisconnectReason.ClientClosed);
        _writeLock.Dispose();
    }
}

/// <summary>
/// Message placed on a session's bounded channel.
/// </summary>
internal readonly record struct FeatureStreamMessage
{
    /// <summary>
    /// True if this is a heartbeat frame rather than a data envelope.
    /// </summary>
    public bool IsHeartbeat { get; init; }

    /// <summary>
    /// The event envelope (only meaningful when <see cref="IsHeartbeat"/> is false).
    /// </summary>
    public FeatureStreamEnvelope Envelope { get; init; }

    /// <summary>
    /// Geometry bounding box from the enriched event (internal only, never serialized to clients).
    /// </summary>
    public double[]? GeometryEnvelope { get; init; }

    /// <summary>
    /// Pre-serialized attribute JSON from the enriched event (internal only, never serialized to clients).
    /// </summary>
    public string? PropertiesJson { get; init; }

    /// <summary>
    /// Generation of the subscription this frame was queued for. The writer drain rejects
    /// frames whose generation no longer matches the subscription's current generation,
    /// fencing stale frames after unsubscribe or same-id replacement. Zero on heartbeats and
    /// on legacy paths that do not pin a generation (the SSE single-default-subscription
    /// drain falls back to the session-wide watermark).
    /// </summary>
    public long SubscriptionGeneration { get; init; }

    /// <summary>
    /// Creates a data message with optional enrichment for subscription filtering.
    /// </summary>
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

    /// <summary>
    /// Creates a heartbeat message.
    /// </summary>
    public static FeatureStreamMessage Heartbeat() =>
        new() { IsHeartbeat = true };
}

/// <summary>
/// Redis pub/sub envelope used to fan out live stream events across nodes.
/// </summary>
internal sealed record FeatureStreamBroadcastMessage
{
    /// <summary>
    /// Unique instance identifier for the originating node.
    /// </summary>
    public required string OriginInstanceId { get; init; }

    /// <summary>
    /// Event envelope delivered to subscribers.
    /// </summary>
    public required FeatureStreamEnvelope Envelope { get; init; }

    /// <summary>
    /// Optional geometry envelope used by subscription filters.
    /// </summary>
    public double[]? GeometryEnvelope { get; init; }

    /// <summary>
    /// Optional pre-serialized feature properties used by subscription filters.
    /// </summary>
    public string? PropertiesJson { get; init; }
}
