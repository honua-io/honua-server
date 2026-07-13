// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics.Metrics;
using Honua.ServiceDefaults;

namespace Honua.Server.Features.Streaming;

/// <summary>
/// OpenTelemetry instruments for the real-time feature-stream pipeline: session
/// lifecycle by transport, slow-consumer backpressure disconnects, heartbeat fan-out,
/// durable-replay delivery on reconnect, and cross-node broadcast backlog loss. Added
/// as part of the GA promotion (#2428) so operators can observe streaming backpressure
/// and reconnect/replay load.
///
/// <para>
/// Instruments are created from the injected <see cref="IMeterFactory"/> on a meter named
/// <see cref="HonuaTelemetry.ServiceName"/> ("Honua"), which is already on the exporter
/// allow-list, so no meter-registration change is required. The type is a DI singleton that
/// owns its instruments and gauge state on the instance (not process-global statics), so tests
/// can construct an isolated instance with its own <see cref="IMeterFactory"/> and observe it
/// without cross-test interference (#2802). Observable gauges read a snapshot the session
/// manager refreshes rather than walking the session table on every scrape.
/// </para>
/// </summary>
internal sealed class FeatureStreamMetrics
{
    private const string TransportTag = "transport";
    private const string ReasonTag = "reason";

    private readonly Meter _meter;
    private readonly Counter<long> _sessionsOpenedCounter;
    private readonly Counter<long> _sessionsClosedCounter;
    private readonly Counter<long> _slowConsumerDropsCounter;
    private readonly Counter<long> _sessionsRejectedCounter;
    private readonly Counter<long> _heartbeatsSentCounter;
    private readonly Counter<long> _replayEventsDeliveredCounter;
    private readonly Counter<long> _clusterBroadcastDroppedCounter;

    private long _activeSessions;
    private long _clusterBroadcastBacklog;

    /// <summary>
    /// Initializes a new instance of the <see cref="FeatureStreamMetrics"/> class, creating its
    /// instruments on a "Honua" meter obtained from <paramref name="meterFactory"/>.
    /// </summary>
    /// <param name="meterFactory">The DI meter factory used to create the shared "Honua" meter.</param>
    public FeatureStreamMetrics(IMeterFactory meterFactory)
    {
        ArgumentNullException.ThrowIfNull(meterFactory);
        _meter = meterFactory.Create(HonuaTelemetry.ServiceName, HonuaTelemetry.ServiceVersion);

        _sessionsOpenedCounter = _meter.CreateCounter<long>(
            "honua.streaming.sessions_opened_total",
            "sessions",
            "Feature-stream sessions accepted, tagged by transport (WebSocket/SSE).");

        _sessionsClosedCounter = _meter.CreateCounter<long>(
            "honua.streaming.sessions_closed_total",
            "sessions",
            "Feature-stream sessions closed, tagged by transport and disconnect reason.");

        _slowConsumerDropsCounter = _meter.CreateCounter<long>(
            "honua.streaming.slow_consumer_drops_total",
            "sessions",
            "Feature-stream sessions force-disconnected because the client could not keep up "
            + "with the bounded send buffer (backpressure), tagged by transport.");

        _sessionsRejectedCounter = _meter.CreateCounter<long>(
            "honua.streaming.sessions_rejected_total",
            "sessions",
            "Feature-stream connection attempts rejected because the concurrent-session cap was reached.");

        _heartbeatsSentCounter = _meter.CreateCounter<long>(
            "honua.streaming.heartbeats_sent_total",
            "frames",
            "Heartbeat frames queued to connected feature-stream sessions.");

        _replayEventsDeliveredCounter = _meter.CreateCounter<long>(
            "honua.streaming.replay_events_delivered_total",
            "events",
            "Durably-stored feature-change events replayed to a reconnecting client from its "
            + "resume cursor, tagged by transport.");

        _clusterBroadcastDroppedCounter = _meter.CreateCounter<long>(
            "honua.streaming.cluster_broadcast_dropped_total",
            "events",
            "Cross-node broadcast payloads dropped because the retry backlog overflowed while "
            + "Redis publish was unavailable (recoverable through the durable store and cross-node poll).");

        _meter.CreateObservableGauge(
            "honua.streaming.active_sessions",
            () => Interlocked.Read(ref _activeSessions),
            unit: "sessions",
            description: "Currently connected feature-stream sessions.");

        _meter.CreateObservableGauge(
            "honua.streaming.cluster_broadcast_backlog",
            () => Interlocked.Read(ref _clusterBroadcastBacklog),
            unit: "events",
            description: "Cross-node broadcast payloads buffered awaiting a Redis publish retry.");
    }

    /// <summary>
    /// The "Honua" meter that owns this instance's instruments. Exposed so unit tests can filter a
    /// <see cref="MeterListener"/> to this exact meter instance for full parallel isolation.
    /// </summary>
    public Meter Meter => _meter;

    /// <summary>Records one accepted session for the given transport.</summary>
    public void RecordSessionOpened(string transport)
        => _sessionsOpenedCounter.Add(1, new KeyValuePair<string, object?>(TransportTag, transport));

    /// <summary>Records one closed session for the given transport and reason.</summary>
    public void RecordSessionClosed(string transport, FeatureStreamDisconnectReason reason)
        => _sessionsClosedCounter.Add(
            1,
            new KeyValuePair<string, object?>(TransportTag, transport),
            new KeyValuePair<string, object?>(ReasonTag, reason.ToString()));

    /// <summary>Records one slow-consumer backpressure disconnect for the given transport.</summary>
    public void RecordSlowConsumerDrop(string transport)
        => _slowConsumerDropsCounter.Add(1, new KeyValuePair<string, object?>(TransportTag, transport));

    /// <summary>Records one connection rejected because the concurrent-session cap was reached.</summary>
    public void RecordSessionRejected()
        => _sessionsRejectedCounter.Add(1);

    /// <summary>Records heartbeat frames queued to sessions.</summary>
    public void RecordHeartbeatsSent(long count)
    {
        if (count > 0)
        {
            _heartbeatsSentCounter.Add(count);
        }
    }

    /// <summary>Records events replayed from the durable store to a reconnecting client.</summary>
    public void RecordReplayEventsDelivered(string transport, long count)
    {
        if (count > 0)
        {
            _replayEventsDeliveredCounter.Add(count, new KeyValuePair<string, object?>(TransportTag, transport));
        }
    }

    /// <summary>Records one dropped cross-node broadcast payload (backlog overflow).</summary>
    public void RecordClusterBroadcastDropped()
        => _clusterBroadcastDroppedCounter.Add(1);

    /// <summary>Refreshes the cached snapshot the observable gauges read.</summary>
    public void RecordGaugeSnapshot(long activeSessions, long clusterBroadcastBacklog)
    {
        Interlocked.Exchange(ref _activeSessions, activeSessions);
        Interlocked.Exchange(ref _clusterBroadcastBacklog, clusterBroadcastBacklog);
    }
}
