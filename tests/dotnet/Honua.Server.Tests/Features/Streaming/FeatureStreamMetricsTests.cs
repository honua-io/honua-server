// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using FluentAssertions;
using Honua.Server.Features.Streaming;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Features.Streaming;

/// <summary>
/// Unit coverage for the GA-promotion (#2428) streaming telemetry. Verifies the OTel
/// instruments land on the shared "Honua" meter with the expected tags so backpressure,
/// reconnect/replay, and cross-node broadcast loss are observable. Mirrors
/// <c>AlertPipelineMetricsTests</c>.
/// </summary>
public sealed class FeatureStreamMetricsTests
{
    [UnitTest]
    public void RecordSessionOpened_IncrementsCounter_TaggedByTransport()
    {
        var measurements = CaptureLongMeasurements(
            "honua.streaming.sessions_opened_total",
            () => FeatureStreamMetrics.RecordSessionOpened("WebSocket"));

        measurements.Should().Contain(m =>
            m.Value == 1 &&
            m.Tags.Any(t => t.Key == "transport" && (string?)t.Value == "WebSocket"));
    }

    [UnitTest]
    public void RecordSessionClosed_IncrementsCounter_TaggedByTransportAndReason()
    {
        var measurements = CaptureLongMeasurements(
            "honua.streaming.sessions_closed_total",
            () => FeatureStreamMetrics.RecordSessionClosed("SSE", FeatureStreamDisconnectReason.ClientClosed));

        measurements.Should().Contain(m =>
            m.Value == 1 &&
            m.Tags.Any(t => t.Key == "transport" && (string?)t.Value == "SSE") &&
            m.Tags.Any(t => t.Key == "reason" && (string?)t.Value == "ClientClosed"));
    }

    [UnitTest]
    public void RecordSlowConsumerDrop_IncrementsBackpressureCounter_TaggedByTransport()
    {
        var measurements = CaptureLongMeasurements(
            "honua.streaming.slow_consumer_drops_total",
            () => FeatureStreamMetrics.RecordSlowConsumerDrop("WebSocket"));

        measurements.Should().Contain(m =>
            m.Value == 1 &&
            m.Tags.Any(t => t.Key == "transport" && (string?)t.Value == "WebSocket"));
    }

    [UnitTest]
    public void RecordSessionRejected_IncrementsCapCounter()
    {
        var measurements = CaptureLongMeasurements(
            "honua.streaming.sessions_rejected_total",
            FeatureStreamMetrics.RecordSessionRejected);

        measurements.Should().Contain(m => m.Value == 1);
    }

    [UnitTest]
    public void RecordReplayEventsDelivered_AddsCount_TaggedByTransport()
    {
        var measurements = CaptureLongMeasurements(
            "honua.streaming.replay_events_delivered_total",
            () => FeatureStreamMetrics.RecordReplayEventsDelivered("SSE", 7));

        measurements.Should().Contain(m =>
            m.Value == 7 &&
            m.Tags.Any(t => t.Key == "transport" && (string?)t.Value == "SSE"));
    }

    [UnitTest]
    public void RecordReplayEventsDelivered_WithZeroCount_EmitsNothing()
    {
        var measurements = CaptureLongMeasurements(
            "honua.streaming.replay_events_delivered_total",
            () => FeatureStreamMetrics.RecordReplayEventsDelivered("SSE", 0));

        measurements.Should().BeEmpty();
    }

    [UnitTest]
    public void RecordClusterBroadcastDropped_IncrementsLossCounter()
    {
        var measurements = CaptureLongMeasurements(
            "honua.streaming.cluster_broadcast_dropped_total",
            FeatureStreamMetrics.RecordClusterBroadcastDropped);

        measurements.Should().Contain(m => m.Value == 1);
    }

    [UnitTest]
    public void DisconnectSession_RecordsAdminCloseAndRefreshesActiveGauge()
    {
        var closed = new ConcurrentBag<(long Value, IReadOnlyList<KeyValuePair<string, object?>> Tags)>();
        var active = new ConcurrentBag<long>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name != "Honua")
            {
                return;
            }

            if (instrument.Name is "honua.streaming.sessions_closed_total" or "honua.streaming.active_sessions")
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
        {
            if (instrument.Name == "honua.streaming.sessions_closed_total")
            {
                closed.Add((value, tags.ToArray()));
            }
            else if (instrument.Name == "honua.streaming.active_sessions")
            {
                active.Add(value);
            }
        });
        listener.Start();

        using var manager = new FeatureStreamSessionManager(
            Options.Create(new FeatureStreamOptions()),
            NullLogger<FeatureStreamSessionManager>.Instance);
        using var session = manager.CreateSession("WebSocket", null);

        manager.DisconnectSession(session.SessionId).Should().BeTrue();
        listener.RecordObservableInstruments();

        closed.Should().Contain(m =>
            m.Value == 1 &&
            m.Tags.Any(t => t.Key == "transport" && (string?)t.Value == "WebSocket") &&
            m.Tags.Any(t => t.Key == "reason" && (string?)t.Value == "AdminDisconnect"));
        active.Should().Contain(0);
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
