// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using Honua.ServiceDefaults;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.Infrastructure.Monitoring;

/// <summary>
/// Unit tests for the in-process serving-latency rolling-window aggregation
/// (<see cref="ServingLatencyAggregator"/>).
/// </summary>
[Protocol(TestProtocols.TestQuality)]
public sealed class ServingLatencyAggregatorTests
{
    private static readonly long TicksPerSecond = Stopwatch.Frequency;

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public void GetSnapshot_EmptyWindow_ReturnsNoProtocols()
    {
        var aggregator = new ServingLatencyAggregator(TimeSpan.FromMinutes(5), samplesPerProtocol: 16, timestampProvider: () => 0);

        var snapshot = aggregator.GetSnapshot();

        Assert.Empty(snapshot.Protocols);
        Assert.Equal(300d, snapshot.WindowSeconds);
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public void GetSnapshot_ComputesNearestRankPercentiles()
    {
        long now = 1_000 * TicksPerSecond;
        var aggregator = new ServingLatencyAggregator(TimeSpan.FromMinutes(5), samplesPerProtocol: 256, timestampProvider: () => now);

        // 1..100 ms across 100 requests; all 2xx.
        for (var ms = 1; ms <= 100; ms++)
        {
            aggregator.Record("FeatureServer", ms, statusCode: 200);
        }

        var entry = Assert.Single(aggregator.GetSnapshot().Protocols);
        Assert.Equal("FeatureServer", entry.Protocol);
        Assert.Equal(100, entry.RequestCount);
        Assert.Equal(0, entry.ErrorCount);
        Assert.Equal(0d, entry.ErrorRate);
        // Nearest-rank: p50 => ceil(0.50*100)=50th sample = 50ms; p95 => 95ms; p99 => 99ms.
        Assert.Equal(50d, entry.P50Ms);
        Assert.Equal(95d, entry.P95Ms);
        Assert.Equal(99d, entry.P99Ms);
        Assert.Equal(100d, entry.MaxMs);
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public void GetSnapshot_CountsServerErrorsForErrorRate()
    {
        long now = 500 * TicksPerSecond;
        var aggregator = new ServingLatencyAggregator(TimeSpan.FromMinutes(5), samplesPerProtocol: 64, timestampProvider: () => now);

        aggregator.Record("OGC-Features", 10, statusCode: 200);
        aggregator.Record("OGC-Features", 20, statusCode: 404); // client error: request, not error
        aggregator.Record("OGC-Features", 30, statusCode: 500); // server error
        aggregator.Record("OGC-Features", 40, statusCode: 503); // server error

        var entry = Assert.Single(aggregator.GetSnapshot().Protocols);
        Assert.Equal(4, entry.RequestCount);
        Assert.Equal(2, entry.ErrorCount);
        Assert.Equal(0.5d, entry.ErrorRate);
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public void GetSnapshot_ExcludesSamplesOutsideWindow()
    {
        long now = 0;
        var aggregator = new ServingLatencyAggregator(TimeSpan.FromSeconds(60), samplesPerProtocol: 128, timestampProvider: () => now);

        // Old sample at t=0.
        aggregator.Record("WFS-2.0", 999, statusCode: 200);

        // Advance 120s (> 60s window) and record fresh samples.
        now = 120 * TicksPerSecond;
        aggregator.Record("WFS-2.0", 10, statusCode: 200);
        aggregator.Record("WFS-2.0", 20, statusCode: 200);

        var entry = Assert.Single(aggregator.GetSnapshot().Protocols);
        Assert.Equal(2, entry.RequestCount); // the t=0 sample expired out of the window
        Assert.Equal(20d, entry.MaxMs);

        // Advance far beyond the window: everything expires and the protocol drops out.
        now = 1_000 * TicksPerSecond;
        Assert.Empty(aggregator.GetSnapshot().Protocols);
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public void Record_RingReservoir_BoundsRetainedSamples()
    {
        long now = 10 * TicksPerSecond;
        var aggregator = new ServingLatencyAggregator(TimeSpan.FromMinutes(5), samplesPerProtocol: 8, timestampProvider: () => now);

        // Write 20 samples into an 8-slot ring; only the most recent 8 survive.
        for (var i = 1; i <= 20; i++)
        {
            aggregator.Record("MapServer", i, statusCode: 200);
        }

        var entry = Assert.Single(aggregator.GetSnapshot().Protocols);
        Assert.Equal(8, entry.RequestCount); // only the most recent 8 of 20 survive
        Assert.Equal(20d, entry.MaxMs); // last written sample retained
        // Retained set sorted ascending is [13,14,15,16,17,18,19,20];
        // nearest-rank p50 => ceil(0.50*8)=4th sample => 16ms.
        Assert.Equal(16d, entry.P50Ms);
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public void GetSnapshot_ErrorRate_IsWindowed_ErrorsAgeOut()
    {
        // #2809: the error rate is a WINDOWED signal (the source ProductionMetricsCollector now reads),
        // not a lifetime ratio. A burst of errors must decay out of the window as it slides past them.
        long now = 0;
        var aggregator = new ServingLatencyAggregator(TimeSpan.FromSeconds(60), samplesPerProtocol: 128, timestampProvider: () => now);

        // A burst of server errors at t=0: 100% error rate in the window.
        for (var i = 0; i < 10; i++)
        {
            aggregator.Record("WMS", 50, statusCode: 500);
        }

        Assert.Equal(1.0d, Assert.Single(aggregator.GetSnapshot().Protocols).ErrorRate);

        // Slide the window past the burst and record only healthy traffic: the error rate falls to 0 because
        // the old errors have aged out (a lifetime ratio would still report a nonzero rate here).
        now = 120 * TicksPerSecond;
        for (var i = 0; i < 10; i++)
        {
            aggregator.Record("WMS", 50, statusCode: 200);
        }

        var entry = Assert.Single(aggregator.GetSnapshot().Protocols);
        Assert.Equal(10, entry.RequestCount);
        Assert.Equal(0d, entry.ErrorRate);
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public void GetSnapshot_PopulatesMergeableDistribution()
    {
        // #2809: each snapshot carries a mergeable distribution so cluster percentiles can be recomputed
        // from summed per-replica distributions instead of a mean of percentiles.
        long now = 1_000 * TicksPerSecond;
        var aggregator = new ServingLatencyAggregator(TimeSpan.FromMinutes(5), samplesPerProtocol: 256, timestampProvider: () => now);

        for (var ms = 1; ms <= 100; ms++)
        {
            aggregator.Record("FeatureServer", ms, statusCode: 200);
        }

        var entry = Assert.Single(aggregator.GetSnapshot().Protocols);
        Assert.NotNull(entry.Distribution);
        Assert.Equal(100, entry.Distribution!.TotalCount);
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public void Record_InvalidInputs_AreIgnored()
    {
        long now = 5 * TicksPerSecond;
        var aggregator = new ServingLatencyAggregator(TimeSpan.FromMinutes(5), samplesPerProtocol: 16, timestampProvider: () => now);

        aggregator.Record("", 10, statusCode: 200);
        aggregator.Record("  ", 10, statusCode: 200);
        aggregator.Record("FeatureServer", -1, statusCode: 200);
        aggregator.Record("FeatureServer", double.NaN, statusCode: 200);

        Assert.Empty(aggregator.GetSnapshot().Protocols);
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task Record_ConcurrentWriters_DoNotThrowAndRetainConsistentCounts()
    {
        long now = 42 * TicksPerSecond;
        var aggregator = new ServingLatencyAggregator(TimeSpan.FromMinutes(5), samplesPerProtocol: 100_000, timestampProvider: () => now);

        const int writers = 8;
        const int perWriter = 5_000;
        var tasks = new Task[writers];
        for (var w = 0; w < writers; w++)
        {
            tasks[w] = Task.Run(() =>
            {
                for (var i = 0; i < perWriter; i++)
                {
                    aggregator.Record("FeatureServer", 1 + (i % 50), statusCode: 200);
                }
            });
        }

        await Task.WhenAll(tasks);

        var entry = Assert.Single(aggregator.GetSnapshot().Protocols);
        Assert.Equal(writers * perWriter, entry.RequestCount);
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public void Constructor_RejectsInvalidArguments()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ServingLatencyAggregator(TimeSpan.Zero, 16));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ServingLatencyAggregator(TimeSpan.FromMinutes(1), 0));
    }
}
