// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics.Metrics;
using FluentAssertions;
using Honua.Server.Features.Streaming;
using Honua.Server.Tests.Infrastructure.Telemetry;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Features.Streaming;

/// <summary>
/// Unit coverage for the GA-promotion (#2428) streaming telemetry. Verifies the OTel
/// instruments land on the shared "Honua" meter with the expected tags so backpressure,
/// reconnect/replay, and cross-node broadcast loss are observable. Each test constructs an
/// isolated <see cref="FeatureStreamMetrics"/> over its own <see cref="TestMeterFactory"/> and
/// filters the listener to that exact meter instance, so parallel tests never pollute one
/// another's observations and the active-session gauge is asserted with a single deterministic
/// read (#2802). Mirrors <c>AlertPipelineMetricsTests</c>.
/// </summary>
public sealed class FeatureStreamMetricsTests
{
    [UnitTest]
    public void RecordSessionOpened_IncrementsCounter_TaggedByTransport()
    {
        using var factory = new TestMeterFactory();
        var metrics = new FeatureStreamMetrics(factory);

        var measurements = CaptureLongMeasurements(
            metrics.Meter,
            "honua.streaming.sessions_opened_total",
            () => metrics.RecordSessionOpened("WebSocket"));

        measurements.Should().Contain(m =>
            m.Value == 1 &&
            m.Tags.Any(t => t.Key == "transport" && (string?)t.Value == "WebSocket"));
    }

    [UnitTest]
    public void RecordSessionClosed_IncrementsCounter_TaggedByTransportAndReason()
    {
        using var factory = new TestMeterFactory();
        var metrics = new FeatureStreamMetrics(factory);

        var measurements = CaptureLongMeasurements(
            metrics.Meter,
            "honua.streaming.sessions_closed_total",
            () => metrics.RecordSessionClosed("SSE", FeatureStreamDisconnectReason.ClientClosed));

        measurements.Should().Contain(m =>
            m.Value == 1 &&
            m.Tags.Any(t => t.Key == "transport" && (string?)t.Value == "SSE") &&
            m.Tags.Any(t => t.Key == "reason" && (string?)t.Value == "ClientClosed"));
    }

    [UnitTest]
    public void RecordSlowConsumerDrop_IncrementsBackpressureCounter_TaggedByTransport()
    {
        using var factory = new TestMeterFactory();
        var metrics = new FeatureStreamMetrics(factory);

        var measurements = CaptureLongMeasurements(
            metrics.Meter,
            "honua.streaming.slow_consumer_drops_total",
            () => metrics.RecordSlowConsumerDrop("WebSocket"));

        measurements.Should().Contain(m =>
            m.Value == 1 &&
            m.Tags.Any(t => t.Key == "transport" && (string?)t.Value == "WebSocket"));
    }

    [UnitTest]
    public void RecordSessionRejected_IncrementsCapCounter()
    {
        using var factory = new TestMeterFactory();
        var metrics = new FeatureStreamMetrics(factory);

        var measurements = CaptureLongMeasurements(
            metrics.Meter,
            "honua.streaming.sessions_rejected_total",
            metrics.RecordSessionRejected);

        measurements.Should().Contain(m => m.Value == 1);
    }

    [UnitTest]
    public void RecordReplayEventsDelivered_AddsCount_TaggedByTransport()
    {
        using var factory = new TestMeterFactory();
        var metrics = new FeatureStreamMetrics(factory);

        var measurements = CaptureLongMeasurements(
            metrics.Meter,
            "honua.streaming.replay_events_delivered_total",
            () => metrics.RecordReplayEventsDelivered("SSE", 7));

        measurements.Should().Contain(m =>
            m.Value == 7 &&
            m.Tags.Any(t => t.Key == "transport" && (string?)t.Value == "SSE"));
    }

    [UnitTest]
    public void RecordReplayEventsDelivered_WithZeroCount_EmitsNothing()
    {
        using var factory = new TestMeterFactory();
        var metrics = new FeatureStreamMetrics(factory);

        var measurements = CaptureLongMeasurements(
            metrics.Meter,
            "honua.streaming.replay_events_delivered_total",
            () => metrics.RecordReplayEventsDelivered("SSE", 0));

        measurements.Should().BeEmpty();
    }

    [UnitTest]
    public void RecordClusterBroadcastDropped_IncrementsLossCounter()
    {
        using var factory = new TestMeterFactory();
        var metrics = new FeatureStreamMetrics(factory);

        var measurements = CaptureLongMeasurements(
            metrics.Meter,
            "honua.streaming.cluster_broadcast_dropped_total",
            metrics.RecordClusterBroadcastDropped);

        measurements.Should().Contain(m => m.Value == 1);
    }

    [UnitTest]
    public void DisconnectSession_RecordsAdminCloseAndRefreshesActiveGauge()
    {
        using var factory = new TestMeterFactory();
        var metrics = new FeatureStreamMetrics(factory);

        var closed = new List<(long Value, IReadOnlyList<KeyValuePair<string, object?>> Tags)>();
        var active = new List<long>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            // Filter to this test's meter instance for full parallel isolation: another test's
            // FeatureStreamMetrics uses a different TestMeterFactory and meter, so its
            // active-session gauge is never observed here.
            if (!ReferenceEquals(instrument.Meter, metrics.Meter))
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
            NullLogger<FeatureStreamSessionManager>.Instance,
            metrics);
        using var session = manager.CreateSession("WebSocket", null);

        manager.DisconnectSession(session.SessionId).Should().BeTrue();

        closed.Should().Contain(m =>
            m.Value == 1 &&
            m.Tags.Any(t => t.Key == "transport" && (string?)t.Value == "WebSocket") &&
            m.Tags.Any(t => t.Key == "reason" && (string?)t.Value == "AdminDisconnect"));

        // The active-session gauge reads this manager's own metrics instance, which no other
        // test shares, so a single observable read after the disconnect deterministically yields
        // the post-disconnect count (0). No deadline polling is required.
        listener.RecordObservableInstruments();

        active.Should().NotBeEmpty();
        active.Should().OnlyContain(v => v == 0,
            "DisconnectSession must refresh the active-session gauge to this manager's count (0)");
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
