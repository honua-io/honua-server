// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Diagnostics.Metrics;
using Honua.ServiceDefaults;

namespace Honua.Server.Features.Streaming;

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

    public FeatureStreamMetrics(IMeterFactory meterFactory)
    {
        ArgumentNullException.ThrowIfNull(meterFactory);
        _meter = meterFactory.Create(HonuaTelemetry.ServiceName, HonuaTelemetry.ServiceVersion);

        // `unit: null` on the instruments below is deliberate, not an oversight. The OpenTelemetry
        // Prometheus exporter derives the exported series name from the instrument name AND its unit:
        // it maps the unit through the UCUM table and appends it, so a declared unit renames the series
        // out from under every dashboard and alert rule without breaking a single build. A PromQL query
        // against the absent name returns an empty vector, so the panel is blank and the alert never
        // fires, silently. Units are documented in the instrument name and description instead. See the
        // SLO-contract comment block in HonuaTelemetry.cs, eng/telemetry/metric-name-contract.v1.json, and
        // MetricNameContractTests, which scrapes the real /metrics exposition and fails on drift.

        _sessionsOpenedCounter = _meter.CreateCounter<long>(
            "honua.streaming.sessions_opened_total",
            unit: null,
            "Feature-stream sessions accepted, tagged by transport (WebSocket/SSE).");

        _sessionsClosedCounter = _meter.CreateCounter<long>(
            "honua.streaming.sessions_closed_total",
            unit: null,
            "Feature-stream sessions closed, tagged by transport and disconnect reason.");

        _slowConsumerDropsCounter = _meter.CreateCounter<long>(
            "honua.streaming.slow_consumer_drops_total",
            unit: null,
            "Feature-stream sessions force-disconnected because the client could not keep up "
            + "with the bounded send buffer (backpressure), tagged by transport.");

        _sessionsRejectedCounter = _meter.CreateCounter<long>(
            "honua.streaming.sessions_rejected_total",
            unit: null,
            "Feature-stream connection attempts rejected because the concurrent-session cap was reached.");

        _heartbeatsSentCounter = _meter.CreateCounter<long>(
            "honua.streaming.heartbeats_sent_total",
            unit: null,
            "Heartbeat frames queued to connected feature-stream sessions.");

        _replayEventsDeliveredCounter = _meter.CreateCounter<long>(
            "honua.streaming.replay_events_delivered_total",
            unit: null,
            "Durably-stored feature-change events replayed to a reconnecting client from its "
            + "resume cursor, tagged by transport.");

        _clusterBroadcastDroppedCounter = _meter.CreateCounter<long>(
            "honua.streaming.cluster_broadcast_dropped_total",
            unit: null,
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
            unit: null,
            description: "Cross-node broadcast payloads buffered awaiting a Redis publish retry.");
    }

    public Meter Meter => _meter;

    public void RecordSessionOpened(string transport)
        => _sessionsOpenedCounter.Add(1, new KeyValuePair<string, object?>(TransportTag, transport));

    public void RecordSessionClosed(string transport, FeatureStreamDisconnectReason reason)
        => _sessionsClosedCounter.Add(
            1,
            new KeyValuePair<string, object?>(TransportTag, transport),
            new KeyValuePair<string, object?>(ReasonTag, reason.ToString()));

    public void RecordSlowConsumerDrop(string transport)
        => _slowConsumerDropsCounter.Add(1, new KeyValuePair<string, object?>(TransportTag, transport));

    public void RecordSessionRejected()
        => _sessionsRejectedCounter.Add(1);

    public void RecordHeartbeatsSent(long count)
    {
        if (count > 0)
        {
            _heartbeatsSentCounter.Add(count);
        }
    }

    public void RecordReplayEventsDelivered(string transport, long count)
    {
        if (count > 0)
        {
            _replayEventsDeliveredCounter.Add(count, new KeyValuePair<string, object?>(TransportTag, transport));
        }
    }

    public void RecordClusterBroadcastDropped()
        => _clusterBroadcastDroppedCounter.Add(1);

    public void RecordGaugeSnapshot(long activeSessions, long clusterBroadcastBacklog)
    {
        Interlocked.Exchange(ref _activeSessions, activeSessions);
        Interlocked.Exchange(ref _clusterBroadcastBacklog, clusterBroadcastBacklog);
    }
}
