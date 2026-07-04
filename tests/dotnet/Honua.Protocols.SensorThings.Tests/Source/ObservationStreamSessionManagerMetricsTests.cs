// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics.Metrics;
using System.Threading.Channels;
using Honua.Protocols.SensorThings.Streaming;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Honua.Server.Tests.Features.Protocols.SensorThings;

/// <summary>
/// Unit tests for the SensorThings observation-stream slow-consumer drop counter (PA-112): the
/// in-memory <c>SlowConsumerDrops</c> counter must also be published as an OTel instrument on a
/// named <see cref="Meter"/> so it can be registered with the shared MeterProvider. A
/// <see cref="MeterListener"/> observes instrument publication directly, so the test needs
/// neither a live host nor a way to actually force a slow-consumer drop.
/// </summary>
/// <remarks>
/// Forcing an end-to-end "drop happens, counter increments" test is not meaningfully possible
/// here: <c>ObservationStreamSessionManager</c> uses <see cref="BoundedChannelFullMode.DropWrite"/>,
/// and <c>ChannelWriter&lt;T&gt;.TryWrite</c> returns <see langword="true"/> for that mode even
/// when the item is silently discarded (it only returns <see langword="false"/> once the writer
/// has been completed). The manager's drop-counting branch (<c>if (!TryWrite(...))</c>) is
/// therefore effectively unreachable via the public API today — a separate, pre-existing bug
/// this pass does not fix (see the PA-112 write-up). This test instead verifies the metric is
/// wired up correctly so it is ready to report real drops once that separate bug is fixed.
/// </remarks>
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
}
