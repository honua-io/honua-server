// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Text.Json;
using System.Threading.Channels;
using Honua.Core.Features.SensorThings.Abstractions;
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
/// are fanned out across nodes via pub/sub. It also implements
/// <see cref="IObservationChangeEventPublisher"/> so the Phase 2 ingest path pushes
/// straight into the live fan-out. Observations are scoped per Datastream so a client
/// can tail a single sensor feed.
/// </summary>
internal sealed class ObservationStreamSessionManager : IObservationChangeEventPublisher, IDisposable
{
    /// <summary>
    /// The <see cref="Meter"/> name emitted for OpenTelemetry. Must be registered in the
    /// service defaults meter allow-list (<c>Honua.ServiceDefaults.Extensions._meterNames</c>)
    /// or the drop counter below is silently excluded from export (PA-112).
    /// </summary>
    internal const string MeterName = "Honua.SensorThings";

    private static readonly RedisChannel BroadcastChannel =
        new("sta:observation:stream:broadcast", RedisChannel.PatternMode.Literal);

    private readonly ConcurrentDictionary<Guid, SessionEntry> _sessions = new();
    private readonly ILogger<ObservationStreamSessionManager> _logger;
    private readonly IConnectionMultiplexer? _redis;
    private readonly ISubscriber? _subscriber;
    private readonly Meter _meter;
    private readonly Counter<long> _observationStreamDrops;
    private readonly string _instanceId = Guid.NewGuid().ToString("N");
    private readonly int _maxConcurrentSessions;
    private readonly int _maxBufferPerConnection;
    private int _activeSessionCount;
    private long _slowConsumerDrops;
    private readonly bool _clusterEnabled;

    public ObservationStreamSessionManager(
        ILogger<ObservationStreamSessionManager> logger,
        IConnectionMultiplexer? redis = null,
        int maxConcurrentSessions = 256,
        int maxBufferPerConnection = 256)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _redis = redis;
        _maxConcurrentSessions = maxConcurrentSessions;
        _maxBufferPerConnection = maxBufferPerConnection;
        _meter = new Meter(MeterName);
        _observationStreamDrops = _meter.CreateCounter<long>(
            "honua_sensorthings_observation_stream_drops_total",
            description: "Observation-stream frames dropped because a slow consumer's bounded channel was full.");

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
        catch (Exception ex)
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

    /// <summary>Current active session count.</summary>
    public int SessionCount => Volatile.Read(ref _activeSessionCount);

    /// <summary>
    /// Attempts to register a new session filtered to <paramref name="datastreamId"/>
    /// (null = all datastreams). Returns null when the concurrent-session cap is reached.
    /// </summary>
    public ObservationStreamSession? TryCreateSession(string transport, long? datastreamId)
    {
        if (!TryReserveSlot())
        {
            return null;
        }

        var id = Guid.NewGuid();
        var channel = Channel.CreateBounded<ObservationStreamFrame>(new BoundedChannelOptions(_maxBufferPerConnection)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        });
        var cts = new CancellationTokenSource();
        var entry = new SessionEntry(channel, cts, datastreamId, transport);
        if (!_sessions.TryAdd(id, entry))
        {
            Interlocked.Decrement(ref _activeSessionCount);
            cts.Dispose();
            return null;
        }

        ObservationStreamLog.SessionCreated(_logger, id, transport, datastreamId);
        return new ObservationStreamSession(id, channel.Reader, this, cts.Token);
    }

    private bool TryReserveSlot()
    {
        while (true)
        {
            var current = Volatile.Read(ref _activeSessionCount);
            if (current >= _maxConcurrentSessions)
            {
                return false;
            }

            if (Interlocked.CompareExchange(ref _activeSessionCount, current + 1, current) == current)
            {
                return true;
            }
        }
    }

    /// <summary>Removes a session and releases its slot.</summary>
    public void RemoveSession(Guid sessionId)
    {
        if (_sessions.TryRemove(sessionId, out var entry))
        {
            Interlocked.Decrement(ref _activeSessionCount);
            entry.Cts.Cancel();
            entry.Cts.Dispose();
        }
    }

    /// <inheritdoc />
    public void PublishObservations(IReadOnlyList<SensorThingsObservation> observations)
    {
        if (observations is null || observations.Count == 0)
        {
            return;
        }

        // Not a candidate for .Select(...): each mapped frame is immediately fanned out via
        // the side-effecting BroadcastLocally/TryPublishCluster calls below rather than
        // collected, so this is a side-effecting loop, not a projection.
        foreach (var observation in observations)
        {
            var frame = ObservationStreamFrame.FromObservation(observation);
            BroadcastLocally(frame);

            if (_clusterEnabled && _subscriber is not null)
            {
                TryPublishCluster(frame);
            }
        }
    }

    private void TryPublishCluster(ObservationStreamFrame frame)
    {
        try
        {
            var payload = JsonSerializer.Serialize(
                new ClusterBroadcastDto(_instanceId, frame),
                ObservationStreamJsonContext.Default.ClusterBroadcastDto);
            _subscriber!.Publish(BroadcastChannel, payload, CommandFlags.FireAndForget);
        }
        catch (Exception ex)
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
            if (message is null || string.Equals(message.OriginInstanceId, _instanceId, StringComparison.Ordinal))
            {
                return;
            }

            BroadcastLocally(message.Frame);
        }
        catch (Exception ex)
        {
            // Intentional catch-all: a malformed/incompatible cluster broadcast message must
            // not tear down the Redis subscriber callback; log and drop this message only.
            ObservationStreamLog.ClusterPublishFailed(_logger, ex);
        }
    }

    private void BroadcastLocally(ObservationStreamFrame frame)
    {
        foreach (var (id, entry) in _sessions)
        {
            if (entry.Cts.IsCancellationRequested)
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
            catch (Exception ex)
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
        Interlocked.Exchange(ref _activeSessionCount, 0);
        _meter.Dispose();
    }

    private sealed class SessionEntry
    {
        public SessionEntry(
            Channel<ObservationStreamFrame> channel,
            CancellationTokenSource cts,
            long? datastreamId,
            string transport)
        {
            Channel = channel;
            Cts = cts;
            DatastreamId = datastreamId;
            Transport = transport;
        }

        public Channel<ObservationStreamFrame> Channel { get; }
        public CancellationTokenSource Cts { get; }
        public long? DatastreamId { get; }
        public string Transport { get; }
    }
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
