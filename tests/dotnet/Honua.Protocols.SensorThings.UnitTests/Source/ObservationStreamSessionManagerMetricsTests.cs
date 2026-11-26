// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Diagnostics.Metrics;
using Honua.Core.Features.Realtime;
using Honua.Protocols.SensorThings.Streaming;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.SensorThings;

public sealed class ObservationStreamSessionManagerMetricsTests
{
    [UnitTest]
    public async Task FullBuffer_DisconnectsAndExportsOneDropUntilDisposed()
    {
        var samples = new List<(long Value, Dictionary<string, object?> Tags)>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, current) =>
            {
                if (instrument.Meter.Name == ObservationStreamSessionManager.MeterName
                    && instrument.Name == "honua_sensorthings_observation_stream_drops_total")
                    current.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, measurement, tags, _) =>
        {
            var map = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var tag in tags)
            {
                map[tag.Key] = tag.Value;
            }

            samples.Add((measurement, map));
        });
        listener.Start();
        var provider = Substitute.For<IRealtimeFanoutProvider>();
        RealtimeSubscriptionRequest? request = null;
        provider.Subscribe(Arg.Do<RealtimeSubscriptionRequest>(value => request = value))
            .Returns(Substitute.For<IRealtimeSubscription>());
        await using var manager = new ObservationStreamSessionManager(
            NullLogger<ObservationStreamSessionManager>.Instance, provider,
            new ObservationStreamOptions { MaxBufferPerConnection = 1 });
        var session = manager.TryCreateSession(new("SSE", 7, new("a", null)) { PrincipalId = "alice" }, out _)!;
        Assert.True(request!.Writer.TryWrite(RealtimeDelivery.Heartbeat.Instance));
        Assert.False(request.Writer.TryWrite(RealtimeDelivery.Heartbeat.Instance));
        Assert.False(request.Writer.TryWrite(RealtimeDelivery.Heartbeat.Instance));
        Assert.Equal(1, manager.SlowConsumerDrops);
        var drop = Assert.Single(samples, s => s.Value == 1);
        Assert.False(drop.Tags.ContainsKey("datastream.id"));
        Assert.True(session.DisconnectToken.IsCancellationRequested);
        Assert.Equal(ObservationStreamDisconnectReason.SlowConsumer, session.DisconnectReason);
        await session.DisposeAsync();
        Assert.Equal(0, manager.SessionCount);
        Assert.Equal(ObservationStreamDisconnectReason.SlowConsumer, session.DisconnectReason);
    }
}
