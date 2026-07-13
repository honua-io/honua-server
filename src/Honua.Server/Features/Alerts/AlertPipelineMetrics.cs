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
/// and observable backlog gauges. Instruments attach to the shared
/// <see cref="HonuaTelemetry.Meter"/> ("Honua"), which is already on the exporter
/// allow-list, so no meter-registration change is required. Backlog gauges read a
/// cached snapshot the dispatcher refreshes each pass rather than re-querying the
/// database on every scrape.
/// </summary>
internal static class AlertPipelineMetrics
{
    private const string TriggerTag = "trigger";
    private const string ChannelTag = "channel";
    private const string OutcomeTag = "outcome";
    private const string SourceTag = "source";
    private const string SeverityTag = "severity";

    private static readonly Counter<long> EventsEmittedCounter = HonuaTelemetry.Meter.CreateCounter<long>(
        "honua.alerts.events_emitted_total",
        "events",
        "Alert events appended to the durable event store, tagged by trigger type.");

    private static readonly Counter<long> DispatchesEnqueuedCounter = HonuaTelemetry.Meter.CreateCounter<long>(
        "honua.alerts.dispatches_enqueued_total",
        "dispatches",
        "Alert delivery dispatch rows enqueued to the outbox, tagged by channel.");

    private static readonly Counter<long> DeliveriesSucceededCounter = HonuaTelemetry.Meter.CreateCounter<long>(
        "honua.alerts.deliveries_succeeded_total",
        "deliveries",
        "Alert deliveries that succeeded, tagged by channel.");

    private static readonly Counter<long> DeliveriesFailedCounter = HonuaTelemetry.Meter.CreateCounter<long>(
        "honua.alerts.deliveries_failed_total",
        "deliveries",
        "Alert deliveries that failed but remain retriable, tagged by channel.");

    private static readonly Counter<long> DeliveriesDeadLetteredCounter = HonuaTelemetry.Meter.CreateCounter<long>(
        "honua.alerts.deliveries_dead_lettered_total",
        "deliveries",
        "Alert deliveries that exhausted retries and were dead-lettered, tagged by channel.");

    private static readonly Counter<long> DeliveriesRateCappedCounter = HonuaTelemetry.Meter.CreateCounter<long>(
        "honua.alerts.deliveries_rate_capped_total",
        "deliveries",
        "Alert deliveries deferred by the per-channel notification rate cap, tagged by channel.");

    private static readonly Counter<long> OpsNotificationsCounter = HonuaTelemetry.Meter.CreateCounter<long>(
        "honua.alerts.ops_notifications_total",
        "notifications",
        "Operations notifications composed for delivery, tagged by source, severity, and outcome.");

    private static readonly Counter<long> DeliveriesSuppressedCounter = HonuaTelemetry.Meter.CreateCounter<long>(
        "honua.alerts.deliveries_suppressed_total",
        "deliveries",
        "Alert deliveries deferred because the event is operator-suppressed, tagged by channel.");

    private static readonly Counter<long> DeliveriesCircuitDeferredCounter = HonuaTelemetry.Meter.CreateCounter<long>(
        "honua.alerts.deliveries_circuit_deferred_total",
        "deliveries",
        "Alert deliveries deferred because the per-channel delivery circuit breaker is open, tagged by channel.");

    private static readonly Histogram<double> DeliveryLatencyHistogram = HonuaTelemetry.Meter.CreateHistogram<double>(
        "honua.alerts.delivery_latency",
        "milliseconds",
        "Latency of an alert delivery sink call, tagged by channel and outcome.");

    private static long _backlogPending;
    private static long _backlogDeadLettered;
    private static long _evaluationLeaderAcquisitionFailing;

    static AlertPipelineMetrics()
    {
        HonuaTelemetry.Meter.CreateObservableGauge(
            "honua.alerts.dispatch_backlog_count",
            () => Interlocked.Read(ref _backlogPending),
            unit: "dispatches",
            description: "Alert dispatch rows awaiting delivery (pending, claimed, or retriable).");

        HonuaTelemetry.Meter.CreateObservableGauge(
            "honua.alerts.dispatch_dead_lettered_count",
            () => Interlocked.Read(ref _backlogDeadLettered),
            unit: "dispatches",
            description: "Alert dispatch rows currently in the dead-letter state.");

        HonuaTelemetry.Meter.CreateObservableGauge(
            "honua.alerts.evaluation_no_leader",
            () => Interlocked.Read(ref _evaluationLeaderAcquisitionFailing),
            unit: "{state}",
            description: "1 when the evaluator's leadership-acquisition attempts are faulting (no node can acquire the evaluation lease); else 0.");
    }

    /// <summary>Records one emitted alert event for the given trigger type.</summary>
    public static void RecordEventEmitted(AlertTriggerType triggerType)
        => EventsEmittedCounter.Add(1, new KeyValuePair<string, object?>(TriggerTag, triggerType.ToString()));

    /// <summary>Records one enqueued dispatch row for the given channel.</summary>
    public static void RecordDispatchEnqueued(AlertChannelType channelType)
        => DispatchesEnqueuedCounter.Add(1, new KeyValuePair<string, object?>(ChannelTag, channelType.ToExternalName()));

    /// <summary>Records a successful delivery and its latency for the given channel.</summary>
    public static void RecordDeliverySucceeded(AlertChannelType channelType, double latencyMs)
    {
        var channelName = channelType.ToExternalName();
        DeliveriesSucceededCounter.Add(1, new KeyValuePair<string, object?>(ChannelTag, channelName));
        DeliveryLatencyHistogram.Record(
            latencyMs,
            new KeyValuePair<string, object?>(ChannelTag, channelName),
            new KeyValuePair<string, object?>(OutcomeTag, "succeeded"));
    }

    /// <summary>Records a failed delivery (dead-lettered or retriable) and its latency.</summary>
    public static void RecordDeliveryFailed(AlertChannelType channelType, bool deadLettered, double latencyMs)
    {
        var channelName = channelType.ToExternalName();
        if (deadLettered)
        {
            DeliveriesDeadLetteredCounter.Add(1, new KeyValuePair<string, object?>(ChannelTag, channelName));
        }
        else
        {
            DeliveriesFailedCounter.Add(1, new KeyValuePair<string, object?>(ChannelTag, channelName));
        }

        DeliveryLatencyHistogram.Record(
            latencyMs,
            new KeyValuePair<string, object?>(ChannelTag, channelName),
            new KeyValuePair<string, object?>(OutcomeTag, deadLettered ? "dead_lettered" : "failed"));
    }

    /// <summary>Records a delivery deferred by the per-channel notification rate cap.</summary>
    public static void RecordDeliveryRateCapped(AlertChannelType channelType)
        => DeliveriesRateCappedCounter.Add(1, new KeyValuePair<string, object?>(ChannelTag, channelType.ToExternalName()));

    /// <summary>Records a delivery deferred because its event is operator-suppressed.</summary>
    public static void RecordDeliverySuppressed(AlertChannelType channelType)
        => DeliveriesSuppressedCounter.Add(1, new KeyValuePair<string, object?>(ChannelTag, channelType.ToExternalName()));

    /// <summary>Records a delivery deferred because the per-channel delivery circuit breaker is open.</summary>
    public static void RecordDeliveryCircuitDeferred(AlertChannelType channelType)
        => DeliveriesCircuitDeferredCounter.Add(1, new KeyValuePair<string, object?>(ChannelTag, channelType.ToExternalName()));

    /// <summary>Records an operations-notification outcome.</summary>
    public static void RecordOpsNotification(string source, AlertSeverity severity, string outcome)
        => OpsNotificationsCounter.Add(
            1,
            new KeyValuePair<string, object?>(SourceTag, source),
            new KeyValuePair<string, object?>(SeverityTag, severity.ToString()),
            new KeyValuePair<string, object?>(OutcomeTag, outcome));

    /// <summary>Refreshes the cached backlog snapshot read by the observable gauges.</summary>
    public static void RecordBacklog(long pending, long deadLettered)
    {
        Interlocked.Exchange(ref _backlogPending, pending);
        Interlocked.Exchange(ref _backlogDeadLettered, deadLettered);
    }

    /// <summary>
    /// Publishes the evaluator's no-leader state to the <c>honua.alerts.evaluation_no_leader</c>
    /// gauge: <c>true</c> when leadership acquisition is currently faulting fleet-wide.
    /// </summary>
    public static void RecordEvaluationLeaderAcquisitionFailing(bool failing)
        => Interlocked.Exchange(ref _evaluationLeaderAcquisitionFailing, failing ? 1 : 0);

    /// <summary>Returns a stopwatch timestamp for measuring delivery latency.</summary>
    public static long StartTimestamp() => Stopwatch.GetTimestamp();

    /// <summary>Returns elapsed milliseconds since a <see cref="StartTimestamp"/> reading.</summary>
    public static double ElapsedMilliseconds(long startTimestamp)
        => Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
}
