// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Diagnostics;
using System.Diagnostics.Metrics;
using Honua.Core.Features.Alerts.Domain;
using Honua.ServiceDefaults;

namespace Honua.Alerts;

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
    private long _evaluationLeaderAcquisitionFailing;

    public AlertPipelineMetrics(IMeterFactory meterFactory)
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

        _eventsEmittedCounter = _meter.CreateCounter<long>(
            "honua.alerts.events_emitted_total",
            unit: null,
            "Alert events appended to the durable event store, tagged by trigger type.");

        _dispatchesEnqueuedCounter = _meter.CreateCounter<long>(
            "honua.alerts.dispatches_enqueued_total",
            unit: null,
            "Alert delivery dispatch rows enqueued to the outbox, tagged by channel.");

        _deliveriesSucceededCounter = _meter.CreateCounter<long>(
            "honua.alerts.deliveries_succeeded_total",
            unit: null,
            "Alert deliveries that succeeded, tagged by channel.");

        _deliveriesFailedCounter = _meter.CreateCounter<long>(
            "honua.alerts.deliveries_failed_total",
            unit: null,
            "Alert deliveries that failed but remain retriable, tagged by channel.");

        _deliveriesDeadLetteredCounter = _meter.CreateCounter<long>(
            "honua.alerts.deliveries_dead_lettered_total",
            unit: null,
            "Alert deliveries that exhausted retries and were dead-lettered, tagged by channel.");

        _deliveriesRateCappedCounter = _meter.CreateCounter<long>(
            "honua.alerts.deliveries_rate_capped_total",
            unit: null,
            "Alert deliveries deferred by the per-channel notification rate cap, tagged by channel.");

        _opsNotificationsCounter = _meter.CreateCounter<long>(
            "honua.alerts.ops_notifications_total",
            unit: null,
            "Operations notifications composed for delivery, tagged by source, severity, and outcome.");

        _deliveriesSuppressedCounter = _meter.CreateCounter<long>(
            "honua.alerts.deliveries_suppressed_total",
            unit: null,
            "Alert deliveries deferred because the event is operator-suppressed, tagged by channel.");

        _deliveriesCircuitDeferredCounter = _meter.CreateCounter<long>(
            "honua.alerts.deliveries_circuit_deferred_total",
            unit: null,
            "Alert deliveries deferred because the per-channel delivery circuit breaker is open, tagged by channel.");

        _deliveryLatencyHistogram = _meter.CreateHistogram<double>(
            "honua.alerts.delivery_latency",
            unit: null,
            "Latency of an alert delivery sink call in milliseconds, tagged by channel and outcome.");

        _meter.CreateObservableGauge(
            "honua.alerts.dispatch_backlog_count",
            () => Interlocked.Read(ref _backlogPending),
            unit: null,
            description: "Alert dispatch rows awaiting delivery (pending, claimed, or retriable).");

        _meter.CreateObservableGauge(
            "honua.alerts.dispatch_dead_lettered_count",
            () => Interlocked.Read(ref _backlogDeadLettered),
            unit: null,
            description: "Alert dispatch rows currently in the dead-letter state.");

        _meter.CreateObservableGauge(
            "honua.alerts.evaluation_no_leader",
            () => Interlocked.Read(ref _evaluationLeaderAcquisitionFailing),
            unit: "{state}",
            description: "1 when the evaluator's leadership-acquisition attempts are faulting (no node can acquire the evaluation lease); else 0.");
    }

    public Meter Meter => _meter;

    public void RecordEventEmitted(AlertTriggerType triggerType)
        => _eventsEmittedCounter.Add(1, new KeyValuePair<string, object?>(TriggerTag, triggerType.ToString()));

    public void RecordDispatchEnqueued(AlertChannelType channelType)
        => _dispatchesEnqueuedCounter.Add(1, new KeyValuePair<string, object?>(ChannelTag, channelType.ToExternalName()));

    public void RecordDeliverySucceeded(AlertChannelType channelType, double latencyMs)
    {
        var channelName = channelType.ToExternalName();
        _deliveriesSucceededCounter.Add(1, new KeyValuePair<string, object?>(ChannelTag, channelName));
        _deliveryLatencyHistogram.Record(
            latencyMs,
            new KeyValuePair<string, object?>(ChannelTag, channelName),
            new KeyValuePair<string, object?>(OutcomeTag, "succeeded"));
    }

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

    public void RecordDeliveryRateCapped(AlertChannelType channelType)
        => _deliveriesRateCappedCounter.Add(1, new KeyValuePair<string, object?>(ChannelTag, channelType.ToExternalName()));

    public void RecordDeliverySuppressed(AlertChannelType channelType)
        => _deliveriesSuppressedCounter.Add(1, new KeyValuePair<string, object?>(ChannelTag, channelType.ToExternalName()));

    public void RecordDeliveryCircuitDeferred(AlertChannelType channelType)
        => _deliveriesCircuitDeferredCounter.Add(1, new KeyValuePair<string, object?>(ChannelTag, channelType.ToExternalName()));

    public void RecordOpsNotification(string source, AlertSeverity severity, string outcome)
        => _opsNotificationsCounter.Add(
            1,
            new KeyValuePair<string, object?>(SourceTag, source),
            new KeyValuePair<string, object?>(SeverityTag, severity.ToString()),
            new KeyValuePair<string, object?>(OutcomeTag, outcome));

    public void RecordBacklog(long pending, long deadLettered)
    {
        Interlocked.Exchange(ref _backlogPending, pending);
        Interlocked.Exchange(ref _backlogDeadLettered, deadLettered);
    }

    public void RecordEvaluationLeaderAcquisitionFailing(bool failing)
        => Interlocked.Exchange(ref _evaluationLeaderAcquisitionFailing, failing ? 1 : 0);

    public static long StartTimestamp() => Stopwatch.GetTimestamp();

    public static double ElapsedMilliseconds(long startTimestamp)
        => Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
}
