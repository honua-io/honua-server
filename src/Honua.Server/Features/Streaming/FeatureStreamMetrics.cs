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
/// and reconnect/replay load. Instruments attach to the shared
/// <see cref="HonuaTelemetry.Meter"/> ("Honua"), which is already on the exporter
/// allow-list, so no meter-registration change is required. Observable gauges read a
/// snapshot the session manager refreshes rather than walking the session table on
/// every scrape. Modeled on <c>AlertPipelineMetrics</c> (#2427).
/// </summary>
internal static class FeatureStreamMetrics
{
    private const string TransportTag = "transport";
    private const string ReasonTag = "reason";

    private static readonly Counter<long> SessionsOpenedCounter = HonuaTelemetry.Meter.CreateCounter<long>(
        "honua.streaming.sessions_opened_total",
        "sessions",
        "Feature-stream sessions accepted, tagged by transport (WebSocket/SSE).");

    private static readonly Counter<long> SessionsClosedCounter = HonuaTelemetry.Meter.CreateCounter<long>(
        "honua.streaming.sessions_closed_total",
        "sessions",
        "Feature-stream sessions closed, tagged by transport and disconnect reason.");

    private static readonly Counter<long> SlowConsumerDropsCounter = HonuaTelemetry.Meter.CreateCounter<long>(
        "honua.streaming.slow_consumer_drops_total",
        "sessions",
        "Feature-stream sessions force-disconnected because the client could not keep up "
        + "with the bounded send buffer (backpressure), tagged by transport.");

    private static readonly Counter<long> SessionsRejectedCounter = HonuaTelemetry.Meter.CreateCounter<long>(
        "honua.streaming.sessions_rejected_total",
        "sessions",
        "Feature-stream connection attempts rejected because the concurrent-session cap was reached.");

    private static readonly Counter<long> HeartbeatsSentCounter = HonuaTelemetry.Meter.CreateCounter<long>(
        "honua.streaming.heartbeats_sent_total",
        "frames",
        "Heartbeat frames queued to connected feature-stream sessions.");

    private static readonly Counter<long> ReplayEventsDeliveredCounter = HonuaTelemetry.Meter.CreateCounter<long>(
        "honua.streaming.replay_events_delivered_total",
        "events",
        "Durably-stored feature-change events replayed to a reconnecting client from its "
        + "resume cursor, tagged by transport.");

    private static readonly Counter<long> ClusterBroadcastDroppedCounter = HonuaTelemetry.Meter.CreateCounter<long>(
        "honua.streaming.cluster_broadcast_dropped_total",
        "events",
        "Cross-node broadcast payloads dropped because the retry backlog overflowed while "
        + "Redis publish was unavailable (recoverable through the durable store and cross-node poll).");

    private static long _activeSessions;
    private static long _clusterBroadcastBacklog;

    static FeatureStreamMetrics()
    {
        HonuaTelemetry.Meter.CreateObservableGauge(
            "honua.streaming.active_sessions",
            () => Interlocked.Read(ref _activeSessions),
            unit: "sessions",
            description: "Currently connected feature-stream sessions.");

        HonuaTelemetry.Meter.CreateObservableGauge(
            "honua.streaming.cluster_broadcast_backlog",
            () => Interlocked.Read(ref _clusterBroadcastBacklog),
            unit: "events",
            description: "Cross-node broadcast payloads buffered awaiting a Redis publish retry.");
    }

    /// <summary>Records one accepted session for the given transport.</summary>
    public static void RecordSessionOpened(string transport)
        => SessionsOpenedCounter.Add(1, new KeyValuePair<string, object?>(TransportTag, transport));

    /// <summary>Records one closed session for the given transport and reason.</summary>
    public static void RecordSessionClosed(string transport, FeatureStreamDisconnectReason reason)
        => SessionsClosedCounter.Add(
            1,
            new KeyValuePair<string, object?>(TransportTag, transport),
            new KeyValuePair<string, object?>(ReasonTag, reason.ToString()));

    /// <summary>Records one slow-consumer backpressure disconnect for the given transport.</summary>
    public static void RecordSlowConsumerDrop(string transport)
        => SlowConsumerDropsCounter.Add(1, new KeyValuePair<string, object?>(TransportTag, transport));

    /// <summary>Records one connection rejected because the concurrent-session cap was reached.</summary>
    public static void RecordSessionRejected()
        => SessionsRejectedCounter.Add(1);

    /// <summary>Records heartbeat frames queued to sessions.</summary>
    public static void RecordHeartbeatsSent(long count)
    {
        if (count > 0)
        {
            HeartbeatsSentCounter.Add(count);
        }
    }

    /// <summary>Records events replayed from the durable store to a reconnecting client.</summary>
    public static void RecordReplayEventsDelivered(string transport, long count)
    {
        if (count > 0)
        {
            ReplayEventsDeliveredCounter.Add(count, new KeyValuePair<string, object?>(TransportTag, transport));
        }
    }

    /// <summary>Records one dropped cross-node broadcast payload (backlog overflow).</summary>
    public static void RecordClusterBroadcastDropped()
        => ClusterBroadcastDroppedCounter.Add(1);

    /// <summary>Refreshes the cached snapshot the observable gauges read.</summary>
    public static void RecordGaugeSnapshot(long activeSessions, long clusterBroadcastBacklog)
    {
        Interlocked.Exchange(ref _activeSessions, activeSessions);
        Interlocked.Exchange(ref _clusterBroadcastBacklog, clusterBroadcastBacklog);
    }
}
