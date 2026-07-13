// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics.Metrics;
using FluentAssertions;
using Honua.Alerts;
using Honua.Core.Features.Alerts.Domain;
using Honua.Server.Tests.Infrastructure.Telemetry;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Alerts;

/// <summary>
/// Unit coverage for the alert-pipeline OTel instruments. Each test constructs an isolated
/// <see cref="AlertPipelineMetrics"/> over its own <see cref="TestMeterFactory"/> and filters the
/// listener to that exact meter instance, so parallel tests never pollute one another's
/// observations and no shared process-global instrument state is involved (#2802).
/// </summary>
public sealed class AlertPipelineMetricsTests
{
    [UnitTest]
    public void RecordEventEmitted_IncrementsCounter_TaggedByTrigger()
    {
        using var factory = new TestMeterFactory();
        var metrics = new AlertPipelineMetrics(factory);

        var measurements = CaptureLongMeasurements(
            metrics.Meter,
            "honua.alerts.events_emitted_total",
            () => metrics.RecordEventEmitted(AlertTriggerType.Dwell));

        measurements.Should().Contain(m =>
            m.Value == 1 &&
            m.Tags.Any(t => t.Key == "trigger" && (string?)t.Value == "Dwell"));
    }

    [UnitTest]
    public void RecordDispatchEnqueued_IncrementsCounter_TaggedByChannel()
    {
        using var factory = new TestMeterFactory();
        var metrics = new AlertPipelineMetrics(factory);

        var measurements = CaptureLongMeasurements(
            metrics.Meter,
            "honua.alerts.dispatches_enqueued_total",
            () => metrics.RecordDispatchEnqueued(AlertChannelType.Slack));

        measurements.Should().Contain(m =>
            m.Value == 1 &&
            m.Tags.Any(t => t.Key == "channel" && (string?)t.Value == "slack"));
    }

    [UnitTest]
    public void RecordDeliverySucceeded_IncrementsCounter_TaggedByChannel()
    {
        using var factory = new TestMeterFactory();
        var metrics = new AlertPipelineMetrics(factory);

        var measurements = CaptureLongMeasurements(
            metrics.Meter,
            "honua.alerts.deliveries_succeeded_total",
            () => metrics.RecordDeliverySucceeded(AlertChannelType.Webhook, latencyMs: 12.5));

        measurements.Should().Contain(m =>
            m.Value == 1 &&
            m.Tags.Any(t => t.Key == "channel" && (string?)t.Value == "webhook"));
    }

    [UnitTest]
    public void RecordDeliveryRateCapped_IncrementsCounter()
    {
        using var factory = new TestMeterFactory();
        var metrics = new AlertPipelineMetrics(factory);

        var measurements = CaptureLongMeasurements(
            metrics.Meter,
            "honua.alerts.deliveries_rate_capped_total",
            () => metrics.RecordDeliveryRateCapped(AlertChannelType.Email));

        measurements.Should().Contain(m =>
            m.Value == 1 &&
            m.Tags.Any(t => t.Key == "channel" && (string?)t.Value == "email"));
    }

    [UnitTest]
    public void RecordOpsNotification_IncrementsCounter_TaggedBySourceSeverityOutcome()
    {
        using var factory = new TestMeterFactory();
        var metrics = new AlertPipelineMetrics(factory);

        var measurements = CaptureLongMeasurements(
            metrics.Meter,
            "honua.alerts.ops_notifications_total",
            () => metrics.RecordOpsNotification("deploy-workflow", AlertSeverity.Critical, "enqueued"));

        measurements.Should().Contain(m =>
            m.Value == 1 &&
            m.Tags.Any(t => t.Key == "source" && (string?)t.Value == "deploy-workflow") &&
            m.Tags.Any(t => t.Key == "outcome" && (string?)t.Value == "enqueued"));
    }

    private static List<(long Value, IReadOnlyList<KeyValuePair<string, object?>> Tags)> CaptureLongMeasurements(
        Meter meter,
        string instrumentName,
        Action emit)
    {
        var captured = new List<(long, IReadOnlyList<KeyValuePair<string, object?>>)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (ReferenceEquals(instrument.Meter, meter) && instrument.Name == instrumentName)
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            captured.Add((value, tags.ToArray()));
        });
        listener.Start();

        emit();

        listener.Dispose();
        return captured;
    }
}
