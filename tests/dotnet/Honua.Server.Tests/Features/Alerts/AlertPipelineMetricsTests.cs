// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics.Metrics;
using FluentAssertions;
using Honua.Alerts;
using Honua.Core.Features.Alerts.Domain;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Alerts;

public sealed class AlertPipelineMetricsTests
{
    [UnitTest]
    public void RecordEventEmitted_IncrementsCounter_TaggedByTrigger()
    {
        var measurements = CaptureLongMeasurements(
            "honua.alerts.events_emitted_total",
            () => AlertPipelineMetrics.RecordEventEmitted(AlertTriggerType.Dwell));

        measurements.Should().Contain(m =>
            m.Value == 1 &&
            m.Tags.Any(t => t.Key == "trigger" && (string?)t.Value == "Dwell"));
    }

    [UnitTest]
    public void RecordDispatchEnqueued_IncrementsCounter_TaggedByChannel()
    {
        var measurements = CaptureLongMeasurements(
            "honua.alerts.dispatches_enqueued_total",
            () => AlertPipelineMetrics.RecordDispatchEnqueued(AlertChannelType.Slack));

        measurements.Should().Contain(m =>
            m.Value == 1 &&
            m.Tags.Any(t => t.Key == "channel" && (string?)t.Value == "slack"));
    }

    [UnitTest]
    public void RecordDeliverySucceeded_IncrementsCounter_TaggedByChannel()
    {
        var measurements = CaptureLongMeasurements(
            "honua.alerts.deliveries_succeeded_total",
            () => AlertPipelineMetrics.RecordDeliverySucceeded(AlertChannelType.Webhook, latencyMs: 12.5));

        measurements.Should().Contain(m =>
            m.Value == 1 &&
            m.Tags.Any(t => t.Key == "channel" && (string?)t.Value == "webhook"));
    }

    [UnitTest]
    public void RecordDeliveryRateCapped_IncrementsCounter()
    {
        var measurements = CaptureLongMeasurements(
            "honua.alerts.deliveries_rate_capped_total",
            () => AlertPipelineMetrics.RecordDeliveryRateCapped(AlertChannelType.Email));

        measurements.Should().Contain(m =>
            m.Value == 1 &&
            m.Tags.Any(t => t.Key == "channel" && (string?)t.Value == "email"));
    }

    [UnitTest]
    public void RecordOpsNotification_IncrementsCounter_TaggedBySourceSeverityOutcome()
    {
        var measurements = CaptureLongMeasurements(
            "honua.alerts.ops_notifications_total",
            () => AlertPipelineMetrics.RecordOpsNotification("deploy-workflow", AlertSeverity.Critical, "enqueued"));

        measurements.Should().Contain(m =>
            m.Value == 1 &&
            m.Tags.Any(t => t.Key == "source" && (string?)t.Value == "deploy-workflow") &&
            m.Tags.Any(t => t.Key == "outcome" && (string?)t.Value == "enqueued"));
    }

    private static List<(long Value, IReadOnlyList<KeyValuePair<string, object?>> Tags)> CaptureLongMeasurements(
        string instrumentName,
        Action emit)
    {
        var captured = new List<(long, IReadOnlyList<KeyValuePair<string, object?>>)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == "Honua" && instrument.Name == instrumentName)
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
