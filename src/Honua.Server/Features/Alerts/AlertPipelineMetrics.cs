// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using System.Diagnostics.Metrics;
using Honua.Core.Features.Alerts.Domain;
using Honua.ServiceDefaults;

namespace Honua.Alerts;

/// <summary>
/// OpenTelemetry instruments for the alert pipeline: event emission, dispatch
/// enqueue, per-channel delivery outcomes and latency, the rate-cap deferral counter,
/// and observable backlog gauges.
///
/// <para>
/// Instruments are created from the injected <see cref="IMeterFactory"/> on a meter named
/// <see cref="HonuaTelemetry.ServiceName"/> ("Honua"), which is already on the exporter
/// allow-list, so no meter-registration change is required. The type is a DI singleton that
/// owns its instruments and backlog-gauge state on the instance (not process-global statics),
/// so tests can construct an isolated instance with its own <see cref="IMeterFactory"/> and
/// observe it without cross-test interference (#2802). Backlog gauges read a cached snapshot
/// the dispatcher refreshes each pass rather than re-querying the database on every scrape.
/// </para>
/// </summary>
internal sealed class AlertPipelineMetrics
{
    private const string TriggerTag = "trigger";
    private const string ChannelTag = "channel";
    private const string OutcomeTag = "outcome";
    private const string SourceTag = "source";
    private const string SeverityTag = "severity";

    private readonly Meter _meter;
    private readonly Counter<long> _eventsEmittedCounter;
    private readonly Counter<long> _dispatchesEnqueuedCounter;
    private readonly Counter<long> _deliveriesSucceededCounter;
    private readonly Counter<long> _deliveriesFailedCounter;
    private readonly Counter<long> _deliveriesDeadLetteredCounter;
    private readonly Counter<long> _deliveriesRateCappedCounter;
    private readonly Counter<long> _opsNotificationsCounter;
    private readonly Counter<long> _deliveriesSuppressedCounter;
    private readonly Counter<long> _deliveriesCircuitDeferredCounter;
    private readonly Histogram<double> _deliveryLatencyHistogram;

    private long _backlogPending;
    private long _backlogDeadLettered;

    /// <summary>
    /// Initializes a new instance of the <see cref="AlertPipelineMetrics"/> class, creating its
    /// instruments on a "Honua" meter obtained from <paramref name="meterFactory"/>.
    /// </summary>
    /// <param name="meterFactory">The DI meter factory used to create the shared "Honua" meter.</param>
    public AlertPipelineMetrics(IMeterFactory meterFactory)
    {
        ArgumentNullException.ThrowIfNull(meterFactory);
        _meter = meterFactory.Create(HonuaTelemetry.ServiceName, HonuaTelemetry.ServiceVersion);

        _eventsEmittedCounter = _meter.CreateCounter<long>(
            "honua.alerts.events_emitted_total",
            "events",
            "Alert events appended to the durable event store, tagged by trigger type.");

        _dispatchesEnqueuedCounter = _meter.CreateCounter<long>(
            "honua.alerts.dispatches_enqueued_total",
            "dispatches",
            "Alert delivery dispatch rows enqueued to the outbox, tagged by channel.");

        _deliveriesSucceededCounter = _meter.CreateCounter<long>(
            "honua.alerts.deliveries_succeeded_total",
            "deliveries",
            "Alert deliveries that succeeded, tagged by channel.");

        _deliveriesFailedCounter = _meter.CreateCounter<long>(
            "honua.alerts.deliveries_failed_total",
            "deliveries",
            "Alert deliveries that failed but remain retriable, tagged by channel.");

        _deliveriesDeadLetteredCounter = _meter.CreateCounter<long>(
            "honua.alerts.deliveries_dead_lettered_total",
            "deliveries",
            "Alert deliveries that exhausted retries and were dead-lettered, tagged by channel.");

        _deliveriesRateCappedCounter = _meter.CreateCounter<long>(
            "honua.alerts.deliveries_rate_capped_total",
            "deliveries",
            "Alert deliveries deferred by the per-channel notification rate cap, tagged by channel.");

        _opsNotificationsCounter = _meter.CreateCounter<long>(
            "honua.alerts.ops_notifications_total",
            "notifications",
            "Operations notifications composed for delivery, tagged by source, severity, and outcome.");

        _deliveriesSuppressedCounter = _meter.CreateCounter<long>(
            "honua.alerts.deliveries_suppressed_total",
            "deliveries",
            "Alert deliveries deferred because the event is operator-suppressed, tagged by channel.");

        _deliveriesCircuitDeferredCounter = _meter.CreateCounter<long>(
            "honua.alerts.deliveries_circuit_deferred_total",
            "deliveries",
            "Alert deliveries deferred because the per-channel delivery circuit breaker is open, tagged by channel.");

        _deliveryLatencyHistogram = _meter.CreateHistogram<double>(
            "honua.alerts.delivery_latency",
            "milliseconds",
            "Latency of an alert delivery sink call, tagged by channel and outcome.");

        _meter.CreateObservableGauge(
            "honua.alerts.dispatch_backlog_count",
            () => Interlocked.Read(ref _backlogPending),
            unit: "dispatches",
            description: "Alert dispatch rows awaiting delivery (pending, claimed, or retriable).");

        _meter.CreateObservableGauge(
            "honua.alerts.dispatch_dead_lettered_count",
            () => Interlocked.Read(ref _backlogDeadLettered),
            unit: "dispatches",
            description: "Alert dispatch rows currently in the dead-letter state.");
    }

    /// <summary>
    /// The "Honua" meter that owns this instance's instruments. Exposed so unit tests can filter a
    /// <see cref="MeterListener"/> to this exact meter instance for full parallel isolation.
    /// </summary>
    public Meter Meter => _meter;

    /// <summary>Records one emitted alert event for the given trigger type.</summary>
    public void RecordEventEmitted(AlertTriggerType triggerType)
        => _eventsEmittedCounter.Add(1, new KeyValuePair<string, object?>(TriggerTag, triggerType.ToString()));

    /// <summary>Records one enqueued dispatch row for the given channel.</summary>
    public void RecordDispatchEnqueued(AlertChannelType channelType)
        => _dispatchesEnqueuedCounter.Add(1, new KeyValuePair<string, object?>(ChannelTag, channelType.ToExternalName()));

    /// <summary>Records a successful delivery and its latency for the given channel.</summary>
    public void RecordDeliverySucceeded(AlertChannelType channelType, double latencyMs)
    {
        var channelName = channelType.ToExternalName();
        _deliveriesSucceededCounter.Add(1, new KeyValuePair<string, object?>(ChannelTag, channelName));
        _deliveryLatencyHistogram.Record(
            latencyMs,
            new KeyValuePair<string, object?>(ChannelTag, channelName),
            new KeyValuePair<string, object?>(OutcomeTag, "succeeded"));
    }

    /// <summary>Records a failed delivery (dead-lettered or retriable) and its latency.</summary>
    public void RecordDeliveryFailed(AlertChannelType channelType, bool deadLettered, double latencyMs)
    {
        var channelName = channelType.ToExternalName();
        if (deadLettered)
        {
            _deliveriesDeadLetteredCounter.Add(1, new KeyValuePair<string, object?>(ChannelTag, channelName));
        }
        else
        {
            _deliveriesFailedCounter.Add(1, new KeyValuePair<string, object?>(ChannelTag, channelName));
        }

        _deliveryLatencyHistogram.Record(
            latencyMs,
            new KeyValuePair<string, object?>(ChannelTag, channelName),
            new KeyValuePair<string, object?>(OutcomeTag, deadLettered ? "dead_lettered" : "failed"));
    }

    /// <summary>Records a delivery deferred by the per-channel notification rate cap.</summary>
    public void RecordDeliveryRateCapped(AlertChannelType channelType)
        => _deliveriesRateCappedCounter.Add(1, new KeyValuePair<string, object?>(ChannelTag, channelType.ToExternalName()));

    /// <summary>Records a delivery deferred because its event is operator-suppressed.</summary>
    public void RecordDeliverySuppressed(AlertChannelType channelType)
        => _deliveriesSuppressedCounter.Add(1, new KeyValuePair<string, object?>(ChannelTag, channelType.ToExternalName()));

    /// <summary>Records a delivery deferred because the per-channel delivery circuit breaker is open.</summary>
    public void RecordDeliveryCircuitDeferred(AlertChannelType channelType)
        => _deliveriesCircuitDeferredCounter.Add(1, new KeyValuePair<string, object?>(ChannelTag, channelType.ToExternalName()));

    /// <summary>Records an operations-notification outcome.</summary>
    public void RecordOpsNotification(string source, AlertSeverity severity, string outcome)
        => _opsNotificationsCounter.Add(
            1,
            new KeyValuePair<string, object?>(SourceTag, source),
            new KeyValuePair<string, object?>(SeverityTag, severity.ToString()),
            new KeyValuePair<string, object?>(OutcomeTag, outcome));

    /// <summary>Refreshes the cached backlog snapshot read by the observable gauges.</summary>
    public void RecordBacklog(long pending, long deadLettered)
    {
        Interlocked.Exchange(ref _backlogPending, pending);
        Interlocked.Exchange(ref _backlogDeadLettered, deadLettered);
    }

    /// <summary>Returns a stopwatch timestamp for measuring delivery latency.</summary>
    public static long StartTimestamp() => Stopwatch.GetTimestamp();

    /// <summary>Returns elapsed milliseconds since a <see cref="StartTimestamp"/> reading.</summary>
    public static double ElapsedMilliseconds(long startTimestamp)
        => Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
}
