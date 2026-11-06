// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Diagnostics.Metrics;
using System.Text.Json;
using Honua.Core.Features.SensorThings.Domain;
using Honua.Protocols.SensorThings.Streaming;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Honua.Server.Tests.Features.Protocols.SensorThings;

/// <summary>
/// Unit tests for the SensorThings observation-stream slow-consumer drop counter (PA-112):
/// the in-memory <c>SlowConsumerDrops</c> counter must also be published as an OTel
/// instrument on a named <see cref="Meter"/> so it can be registered with the shared
/// MeterProvider.
/// </summary>
public sealed class ObservationStreamSessionManagerMetricsTests
{
    [UnitTest]
    public void Constructor_PublishesObservationStreamDropsCounter()
    {
        using var listener = new MeterListener();
        Instrument? published = null;
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == ObservationStreamSessionManager.MeterName
                && instrument.Name == "honua_sensorthings_observation_stream_drops_total")
            {
                published = instrument;
            }
        };
        listener.Start();

        using var manager = new ObservationStreamSessionManager(
            NullLogger<ObservationStreamSessionManager>.Instance,
            redis: null);

        Assert.NotNull(published);
        Assert.Equal(ObservationStreamSessionManager.MeterName, published!.Meter.Name);
        Assert.IsType<Counter<long>>(published);
    }

    [UnitTest]
    public void PublishObservations_WhenSessionBufferFull_IncrementsDropCounters()
    {
        var samples = new List<long>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, currentListener) =>
            {
                if (instrument.Meter.Name == ObservationStreamSessionManager.MeterName
                    && instrument.Name == "honua_sensorthings_observation_stream_drops_total")
                {
                    currentListener.EnableMeasurementEvents(instrument);
                }
            }
        };
        listener.SetMeasurementEventCallback<long>((_, measurement, _, _) => samples.Add(measurement));
        listener.Start();

        using var manager = new ObservationStreamSessionManager(
            NullLogger<ObservationStreamSessionManager>.Instance,
            redis: null,
            new ObservationStreamOptions { MaxBufferPerConnection = 1 });
        var scope = new ObservationStreamScope("tenant-a", "schema_a");
        using var session = manager.TryCreateSession("sse", datastreamId: 7, scope)!;

        manager.PublishObservations(
            [
                CreateObservation(id: 1, datastreamId: 7),
                CreateObservation(id: 2, datastreamId: 7)
            ], scope);

        Assert.Equal(1, manager.SlowConsumerDrops);
        Assert.Contains(1, samples);
    }

    [UnitTest]
    public void PublishObservations_WhenSessionBufferFull_DisconnectsInsteadOfStayingHealthy()
    {
        using var manager = new ObservationStreamSessionManager(
            NullLogger<ObservationStreamSessionManager>.Instance,
            redis: null,
            new ObservationStreamOptions { MaxBufferPerConnection = 1 });
        var scope = new ObservationStreamScope("tenant-a", "schema_a");
        var session = manager.TryCreateSession("sse", datastreamId: 7, scope);
        Assert.NotNull(session);
        try
        {
            manager.PublishObservations(
                [
                    CreateObservation(id: 1, datastreamId: 7),
                    CreateObservation(id: 2, datastreamId: 7)
                ], scope);

            Assert.Equal(1, manager.SlowConsumerDrops);
            Assert.Equal(0, manager.SessionCount);
            Assert.True(session!.DisconnectToken.IsCancellationRequested);
            Assert.Equal(ObservationStreamDisconnectReason.SlowConsumer, session.DisconnectReason);
        }
        finally
        {
            session!.Dispose();
            Assert.Equal(ObservationStreamDisconnectReason.SlowConsumer, session.DisconnectReason);
        }
    }

    [UnitTest]
    public void BroadcastHeartbeat_WhenSessionBufferFull_DisconnectsInsteadOfStayingHealthy()
    {
        using var manager = new ObservationStreamSessionManager(
            NullLogger<ObservationStreamSessionManager>.Instance,
            redis: null,
            new ObservationStreamOptions { MaxBufferPerConnection = 1 });
        var scope = new ObservationStreamScope("tenant-a", "schema_a");
        var session = manager.TryCreateSession("websocket", datastreamId: null, scope);
        Assert.NotNull(session);
        try
        {
            manager.PublishObservations([CreateObservation(id: 1, datastreamId: 7)], scope);
            Assert.Equal(0, manager.SlowConsumerDrops);
            Assert.Equal(1, manager.SessionCount);

            manager.BroadcastHeartbeat();

            Assert.Equal(1, manager.SlowConsumerDrops);
            Assert.Equal(0, manager.SessionCount);
            Assert.True(session!.DisconnectToken.IsCancellationRequested);
            Assert.Equal(ObservationStreamDisconnectReason.SlowConsumer, session.DisconnectReason);
        }
        finally
        {
            session!.Dispose();
            Assert.Equal(ObservationStreamDisconnectReason.SlowConsumer, session.DisconnectReason);
        }
    }

    [UnitTest]
    public void StatusFrames_AdvertiseEphemeralSemanticsAndSlowConsumerGap()
    {
        var connected = JsonSerializer.Serialize(
            ObservationStreamStatus.Connected(7),
            ObservationStreamJsonContext.Default.ObservationStreamStatus);
        Assert.Contains("\"status\":\"connected\"", connected, StringComparison.Ordinal);
        Assert.Contains("\"semantics\":\"ephemeral\"", connected, StringComparison.Ordinal);
        Assert.DoesNotContain("reason", connected, StringComparison.Ordinal);

        var gap = JsonSerializer.Serialize(
            ObservationStreamStatus.SlowConsumerGap(7),
            ObservationStreamJsonContext.Default.ObservationStreamStatus);
        Assert.Contains("\"status\":\"gap\"", gap, StringComparison.Ordinal);
        Assert.Contains("\"reason\":\"slow-consumer\"", gap, StringComparison.Ordinal);
        Assert.Contains("\"semantics\":\"ephemeral\"", gap, StringComparison.Ordinal);
    }

    private static SensorThingsObservation CreateObservation(long id, long datastreamId) => new()
    {
        Id = id,
        DatastreamId = datastreamId,
        PhenomenonTime = DateTimeOffset.UtcNow,
        Result = id
    };
}
