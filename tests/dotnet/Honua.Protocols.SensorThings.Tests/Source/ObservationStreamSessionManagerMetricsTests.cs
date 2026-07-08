// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics.Metrics;
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
            maxBufferPerConnection: 1);
        using var session = manager.TryCreateSession("sse", datastreamId: 7)!;

        manager.PublishObservations(
            [
                CreateObservation(id: 1, datastreamId: 7),
                CreateObservation(id: 2, datastreamId: 7)
            ]);

        Assert.Equal(1, manager.SlowConsumerDrops);
        Assert.Contains(1, samples);
    }

    private static SensorThingsObservation CreateObservation(long id, long datastreamId) => new()
    {
        Id = id,
        DatastreamId = datastreamId,
        PhenomenonTime = DateTimeOffset.UtcNow,
        Result = id
    };
}
