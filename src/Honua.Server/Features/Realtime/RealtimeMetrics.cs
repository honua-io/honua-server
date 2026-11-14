// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Diagnostics.Metrics;
using Honua.ServiceDefaults;

namespace Honua.Server.Features.Realtime;

// One instrument set for every realtime transport. Keep units in descriptions: adding a unit
// changes Prometheus series names. The inventory guards those names and their consumers.
internal static class RealtimeMetrics
{
    // Second-scale buckets around the one- and five-second alarms in docs/internal/operations/realtime.md;
    // the exporter defaults start at 0 and 5, which would put every sub-second value in one bucket.
    internal static readonly IReadOnlyList<double> LatencyBucketBoundaries =
        [0.005, 0.01, 0.025, 0.05, 0.1, 0.25, 0.5, 1, 2.5, 5, 10, 30, 60];

    internal static readonly UpDownCounter<long> Subscribers = HonuaTelemetry.Meter.CreateUpDownCounter<long>(
        "honua.realtime.subscribers", unit: null, description: "Open transport sessions, tagged by family and transport.");

    internal static readonly Gauge<long> LogHead = HonuaTelemetry.Meter.CreateGauge<long>(
        "honua.realtime.log.head", unit: null, description: "Last observed committed sequence, tagged by family.");

    internal static readonly Gauge<long> DispatchPosition = HonuaTelemetry.Meter.CreateGauge<long>(
        "honua.realtime.dispatch.position", unit: null, description: "Last dispatched sequence, tagged by family.");

    internal static readonly Gauge<long> DispatchLagEvents = HonuaTelemetry.Meter.CreateGauge<long>(
        "honua.realtime.dispatch.lag.events", unit: null, description: "Events waiting when the dispatcher last woke (head minus dispatch position), tagged by family.");

    // A histogram, not a gauge: a dispatch takes milliseconds, so a last-value gauge would read the
    // caught-up state at nearly every export and hide the wait that preceded it.
    internal static readonly Histogram<double> DispatchLagSeconds = HonuaTelemetry.Meter.CreateHistogram<double>(
        "honua.realtime.dispatch.lag.seconds", unit: null,
        description: "Seconds the oldest event of each dispatched batch waited in the log, tagged by family.",
        tags: null, advice: new InstrumentAdvice<double> { HistogramBucketBoundaries = LatencyBucketBoundaries });


    internal static readonly Histogram<double> DeliveryLatency = HonuaTelemetry.Meter.CreateHistogram<double>(
        "honua.realtime.delivery.latency", unit: null,
        description: "Seconds from commit to a frame flushed to the transport, tagged by family and transport.",
        tags: null, advice: new InstrumentAdvice<double> { HistogramBucketBoundaries = LatencyBucketBoundaries });

    internal static readonly Gauge<long> ListenerConnected = HonuaTelemetry.Meter.CreateGauge<long>(
        "honua.realtime.listener.connected", unit: null, description: "One while the dedicated listener is connected, otherwise zero.");

    internal static readonly Counter<long> ListenerReconnects = HonuaTelemetry.Meter.CreateCounter<long>(
        "honua.realtime.listener.reconnects", unit: null, description: "Successful listener reconnects after a failure.");

    internal static readonly Counter<long> Wakeups = HonuaTelemetry.Meter.CreateCounter<long>(
        "honua.realtime.wakeups", unit: null, description: "Dispatcher wake-ups, tagged by source: notify, safety-poll, degraded-poll or local-poll.");

    internal static readonly Counter<long> Resets = HonuaTelemetry.Meter.CreateCounter<long>(
        "honua.realtime.resets", unit: null, description: "Reset frames written, tagged by family, transport and reason.");

    internal static readonly Counter<long> SlowConsumerClosed = HonuaTelemetry.Meter.CreateCounter<long>(
        "honua.realtime.slow_consumer.closed", unit: null, description: "Sessions closed because their bounded channel filled, tagged by family.");

    internal static readonly Counter<long> FramesDropped = HonuaTelemetry.Meter.CreateCounter<long>(
        "honua.realtime.frames.dropped", unit: null, description: "Frames a session never received: rejected by its full bounded channel or held for a replay when it closed, tagged by family.");

    internal static readonly Counter<long> FramesFiltered = HonuaTelemetry.Meter.CreateCounter<long>(
        "honua.realtime.authz.frames_filtered", unit: null, description: "Frames filtered by authorization, tagged by family and transport.");

    internal static readonly Counter<long> StreamsRevoked = HonuaTelemetry.Meter.CreateCounter<long>(
        "honua.realtime.authz.streams_revoked", unit: null, description: "Sessions closed after access was revoked, tagged by family and transport.");

    internal static readonly Gauge<double> NotifyQueueUsage = HonuaTelemetry.Meter.CreateGauge<double>(
        "honua.realtime.notify_queue.usage", unit: null, description: "PostgreSQL notification queue fraction sampled by the maintenance lease holder.");
}
