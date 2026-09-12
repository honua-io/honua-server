// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Text.Json;
using System.Threading.Channels;
using Honua.Core.Features.SensorThings.Domain;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Honua.Protocols.SensorThings.Streaming;

/// <summary>
/// Manages OGC SensorThings real-time observation-stream sessions (Phase 3, #1747).
/// This mirrors the proven transport pattern of the server feature-change stream
/// (<c>FeatureStreamSessionManager</c>): each connected client (SSE or WebSocket) gets
/// a bounded channel, a heartbeat keeps idle connections alive, slow consumers whose
/// buffer fills are dropped, and — when Redis is present — newly-ingested observations
/// are fanned out across nodes via pub/sub. The request-scoped publisher captures the
/// resolved tenant and schema before calling this singleton. Observations are scoped
/// to that boundary and optionally a Datastream so a client can tail a single feed.
/// </summary>
internal sealed class ObservationStreamSessionManager : IDisposable
{
    /// <summary>
    /// The <see cref="Meter"/> name emitted for OpenTelemetry. Must be registered in the
    /// service defaults meter allow-list (<c>Honua.ServiceDefaults.Extensions._meterNames</c>)
    /// or the drop counter below is silently excluded from export (PA-112).
    /// </summary>
    internal const string MeterName = "Honua.SensorThings";

    private const string UntenantedPartition = "untenanted";
    private const string UnidentifiedPrincipal = "unidentified";

    private static readonly RedisChannel BroadcastChannel =
        // Isolate the scoped protocol from older nodes that broadcast every frame to
        // every tenant. They must never receive scoped messages during rolling upgrades.
        new("sta:observation:stream:v2:broadcast", RedisChannel.PatternMode.Literal);

    private readonly ConcurrentDictionary<Guid, SessionEntry> _sessions = new();
    private readonly ILogger<ObservationStreamSessionManager> _logger;
    private readonly IConnectionMultiplexer? _redis;
    private readonly ISubscriber? _subscriber;
    private readonly Meter _meter;
    private readonly Counter<long> _observationStreamDrops;
    private readonly Counter<long> _sessionRejections;
    private readonly string _instanceId = Guid.NewGuid().ToString("N");
    private readonly ObservationStreamOptions _options;
    private readonly Lock _admissionLock = new();
    private readonly Dictionary<string, int> _tenantSessionCounts = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Tenant, string Principal), int> _principalSessionCounts = new();
    private int _activeSessionCount;
    private long _slowConsumerDrops;
    private readonly bool _clusterEnabled;

    public ObservationStreamSessionManager(
        ILogger<ObservationStreamSessionManager> logger,
        IConnectionMultiplexer? redis = null,
        ObservationStreamOptions? options = null)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _redis = redis;
        _options = options ?? new ObservationStreamOptions();
        _meter = new Meter(MeterName);
        _observationStreamDrops = _meter.CreateCounter<long>(
            "honua_sensorthings_observation_stream_drops_total",
            description: "Observation-stream frames dropped because a slow consumer's bounded channel was full.");
        _sessionRejections = _meter.CreateCounter<long>(
            "honua_sensorthings_observation_stream_rejections_total",
            description: "Observation-stream sessions refused because a principal, tenant, or node admission cap was reached.");

        if (_redis is null)
        {
            return;
        }

        try
        {
            _subscriber = _redis.GetSubscriber();
            _subscriber.Subscribe(BroadcastChannel, HandleClusterBroadcast);
            _clusterEnabled = true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Best-effort: degrade to single-node fan-out when Redis is unavailable.
            ObservationStreamLog.ClusterUnavailable(_logger, ex);
        }
    }

    /// <summary>Number of slow-consumer disconnects since startup.</summary>
    public long SlowConsumerDrops => Interlocked.Read(ref _slowConsumerDrops);

    /// <summary>
    /// The underlying <see cref="Meter"/> instance. Exposed to tests so a per-instance
    /// <see cref="MeterListener"/> can scope capture to this object rather than the shared
    /// meter name, which prevents cross-test contamination under parallel execution.
    /// </summary>
    internal Meter Meter => _meter;

    /// <summary>The admission limits this manager enforces.</summary>
    internal ObservationStreamOptions Options => _options;

    /// <summary>Current active session count.</summary>
    public int SessionCount => Volatile.Read(ref _activeSessionCount);

    /// <summary>
    /// Attempts to register a new session filtered to <paramref name="datastreamId"/>
    /// (null = all datastreams). Returns null when an admission cap is reached.
    /// </summary>
    public ObservationStreamSession? TryCreateSession(
        string transport, long? datastreamId, ObservationStreamScope scope, string? principalId = null)
        => TryCreateSession(transport, datastreamId, scope, principalId, out _);

    /// <summary>
    /// Attempts to register a new session for <paramref name="principalId"/> within the
    /// tenant of <paramref name="scope"/>. A caller is admitted only while it is under its
    /// own cap, its tenant's cap, and the node cap, so no single credential or tenant can
    /// pin the node budget (#4198). Unidentified principals within a tenant share one
    /// principal partition. On refusal <paramref name="rejectedBy"/> names the cap hit.
    /// </summary>
    public ObservationStreamSession? TryCreateSession(
        string transport,
        long? datastreamId,
        ObservationStreamScope scope,
        string? principalId,
        out ObservationStreamAdmissionLimit rejectedBy)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var tenant = string.IsNullOrWhiteSpace(scope.TenantId) ? UntenantedPartition : "tenant:" + scope.TenantId;
        var principal = (tenant, string.IsNullOrWhiteSpace(principalId) ? UnidentifiedPrincipal : principalId);
        rejectedBy = TryReserveSlot(tenant, principal);
        if (rejectedBy != ObservationStreamAdmissionLimit.None)
        {
            _sessionRejections.Add(1,
                new KeyValuePair<string, object?>("limit", rejectedBy.ToString()),
                new KeyValuePair<string, object?>("transport", transport));
            ObservationStreamLog.SessionRejected(_logger, transport, rejectedBy);
            return null;
        }

        var id = Guid.NewGuid();
        var channel = Channel.CreateBounded<ObservationStreamFrame>(new BoundedChannelOptions(_options.MaxBufferPerConnection)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        });
        var cts = new CancellationTokenSource();
        var entry = new SessionEntry(channel, cts, datastreamId, transport, scope, principal);
        if (!_sessions.TryAdd(id, entry))
        {
            ReleaseSlot(principal);
            cts.Dispose();
            rejectedBy = ObservationStreamAdmissionLimit.Node;
            return null;
        }

        ObservationStreamLog.SessionCreated(_logger, id, transport, datastreamId);
        return new ObservationStreamSession(id, channel.Reader, this, cts.Token);
    }

    private ObservationStreamAdmissionLimit TryReserveSlot(string tenant, (string Tenant, string Principal) principal)
    {
        lock (_admissionLock)
        {
            // Most specific first: a caller over its own quota is told so (429) even when the
            // node is also saturated, because only its own disconnects can admit it.
            if (_principalSessionCounts.GetValueOrDefault(principal) >= _options.MaxSessionsPerPrincipal)
            {
                return ObservationStreamAdmissionLimit.Principal;
            }

            if (_tenantSessionCounts.GetValueOrDefault(tenant) >= _options.MaxSessionsPerTenant)
            {
                return ObservationStreamAdmissionLimit.Tenant;
            }

            if (_activeSessionCount >= _options.MaxConcurrentSessions)
            {
                return ObservationStreamAdmissionLimit.Node;
            }

            _principalSessionCounts[principal] = _principalSessionCounts.GetValueOrDefault(principal) + 1;
            _tenantSessionCounts[tenant] = _tenantSessionCounts.GetValueOrDefault(tenant) + 1;
            Volatile.Write(ref _activeSessionCount, _activeSessionCount + 1);
            return ObservationStreamAdmissionLimit.None;
        }
    }

    private void ReleaseSlot((string Tenant, string Principal) principal)
    {
        lock (_admissionLock)
        {
            Decrement(_principalSessionCounts, principal);
            Decrement(_tenantSessionCounts, principal.Tenant);
            Volatile.Write(ref _activeSessionCount, _activeSessionCount - 1);
        }

        static void Decrement<TKey>(Dictionary<TKey, int> counts, TKey key)
            where TKey : notnull
        {
            if (!counts.TryGetValue(key, out var count))
            {
                return;
            }

            if (count <= 1)
            {
                counts.Remove(key);
            }
            else
            {
                counts[key] = count - 1;
            }
        }
    }

    /// <summary>Removes a session and releases its slot.</summary>
    public void RemoveSession(Guid sessionId)
    {
        if (_sessions.TryRemove(sessionId, out var entry))
        {
            ReleaseSlot(entry.Principal);
            entry.Cts.Cancel();
            entry.Cts.Dispose();
        }
    }

    /// <summary>Publishes committed observations only within their captured request scope.</summary>
    public void PublishObservations(IReadOnlyList<SensorThingsObservation> observations, ObservationStreamScope scope)
    {
        if (observations is null || observations.Count == 0)
        {
            return;
        }

        // Not a candidate for .Select(...): each mapped frame is immediately fanned out via
        // the side-effecting BroadcastLocally/TryPublishCluster calls below rather than
        // collected, so this is a side-effecting loop, not a projection.
        foreach (var frame in (observations).Select(observation => ObservationStreamFrame.FromObservation(observation)))
        {
            BroadcastLocally(frame, scope);

            if (_clusterEnabled && _subscriber is not null)
            {
                TryPublishCluster(frame, scope);
            }
        }
    }

    private void TryPublishCluster(ObservationStreamFrame frame, ObservationStreamScope scope)
    {
        try
        {
            var payload = JsonSerializer.Serialize(
                new ClusterBroadcastDto(_instanceId, frame, scope),
                ObservationStreamJsonContext.Default.ClusterBroadcastDto);
            _subscriber!.Publish(BroadcastChannel, payload, CommandFlags.FireAndForget);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Intentional catch-all: cluster fan-out is best-effort (single-node delivery
            // already happened via BroadcastLocally); log and continue rather than fail ingest.
            ObservationStreamLog.ClusterPublishFailed(_logger, ex);
        }
    }

    private void HandleClusterBroadcast(RedisChannel channel, RedisValue value)
    {
        if (value.IsNullOrEmpty)
        {
            return;
        }

        try
        {
            var message = JsonSerializer.Deserialize(
                value.ToString(), ObservationStreamJsonContext.Default.ClusterBroadcastDto);
            if (message?.Scope is null || message.Frame is null ||
                string.Equals(message.OriginInstanceId, _instanceId, StringComparison.Ordinal))
            {
                return;
            }

            BroadcastLocally(message.Frame, message.Scope);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Intentional catch-all: a malformed/incompatible cluster broadcast message must
            // not tear down the Redis subscriber callback; log and drop this message only.
            ObservationStreamLog.ClusterPublishFailed(_logger, ex);
        }
    }

    private void BroadcastLocally(ObservationStreamFrame frame, ObservationStreamScope scope)
    {
        foreach (var (id, entry) in _sessions)
        {
            if (entry.Cts.IsCancellationRequested || entry.Scope != scope)
            {
                continue;
            }

            if (entry.DatastreamId is { } datastreamId && datastreamId != frame.DatastreamId)
            {
                continue;
            }

            // Wait-mode bounded channel with non-blocking TryWrite: a full buffer
            // reports false, allowing this fan-out path to drop the new frame and
            // count the slow-consumer gap without blocking ingest.
            if (!entry.Channel.Writer.TryWrite(frame))
            {
                Interlocked.Increment(ref _slowConsumerDrops);
                _observationStreamDrops.Add(1, new KeyValuePair<string, object?>("datastream.id", frame.DatastreamId));
            }
        }
    }

    /// <summary>Sends a heartbeat frame to every active session.</summary>
    public void BroadcastHeartbeat()
    {
        foreach (var (_, entry) in _sessions)
        {
            if (!entry.Cts.IsCancellationRequested)
            {
                entry.Channel.Writer.TryWrite(ObservationStreamFrame.Heartbeat());
            }
        }
    }

    public void Dispose()
    {
        if (_subscriber is not null)
        {
            try
            {
                _subscriber.Unsubscribe(BroadcastChannel, HandleClusterBroadcast);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Best-effort shutdown: Dispose() must not throw, so log and continue.
                ObservationStreamLog.ClusterUnsubscribeFailed(_logger, ex);
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
            _principalSessionCounts.Clear();
            _tenantSessionCounts.Clear();
            Volatile.Write(ref _activeSessionCount, 0);
        }

        _meter.Dispose();
    }

    private sealed class SessionEntry
    {
        public SessionEntry(
            Channel<ObservationStreamFrame> channel,
            CancellationTokenSource cts,
            long? datastreamId,
            string transport,
            ObservationStreamScope scope,
            (string Tenant, string Principal) principal)
        {
            Channel = channel;
            Cts = cts;
            DatastreamId = datastreamId;
            Transport = transport;
            Scope = scope;
            Principal = principal;
        }

        public Channel<ObservationStreamFrame> Channel { get; }
        public CancellationTokenSource Cts { get; }
        public long? DatastreamId { get; }
        public string Transport { get; }
        public ObservationStreamScope Scope { get; }
        public (string Tenant, string Principal) Principal { get; }
    }
}

/// <summary>The admission cap that refused an observation-stream session.</summary>
internal enum ObservationStreamAdmissionLimit
{
    /// <summary>The session was admitted.</summary>
    None,

    /// <summary>The calling principal already holds its maximum number of sessions.</summary>
    Principal,

    /// <summary>The caller's tenant already holds its maximum number of sessions.</summary>
    Tenant,

    /// <summary>This node already holds its maximum number of sessions.</summary>
    Node
}

/// <summary>
/// Handle returned to the transport loop: drains frames and signals disconnect.
/// </summary>
internal sealed class ObservationStreamSession : IDisposable
{
    private readonly ObservationStreamSessionManager _manager;

    public ObservationStreamSession(
        Guid sessionId,
        ChannelReader<ObservationStreamFrame> reader,
        ObservationStreamSessionManager manager,
        CancellationToken disconnectToken)
    {
        SessionId = sessionId;
        Reader = reader;
        DisconnectToken = disconnectToken;
        _manager = manager;
    }

    /// <summary>Unique session identifier.</summary>
    public Guid SessionId { get; }

    /// <summary>Channel reader for the transport loop.</summary>
    public ChannelReader<ObservationStreamFrame> Reader { get; }

    /// <summary>Fires when the session is removed.</summary>
    public CancellationToken DisconnectToken { get; }

    public void Dispose() => _manager.RemoveSession(SessionId);
}
