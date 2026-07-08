// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using FluentAssertions;
using Honua.Core.Features.Observability.Domain;
using Honua.Infrastructure.Monitoring;
using Honua.ServiceDefaults;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.Infrastructure.Monitoring;

/// <summary>
/// Unit tests for the ops-health rollup sampler surface (#2553): history-query parsing, and a perf
/// assertion bounding the sampler's per-flush in-process capture overhead so it stays off serving paths.
/// </summary>
[Protocol(TestProtocols.TestQuality)]
public sealed class OpsHealthRollupSamplerTests
{
    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public void HistoryQuery_DefaultsToOneMinuteWindow()
    {
        OpsHealthHistoryQuery.TryParse(window: null, resolution: null, perReplica: false, out var error, out var query)
            .Should().BeTrue();
        error.Should().BeNull();
        query.Tier.Should().Be(OpsHealthRollupTier.OneMinute);
        query.Window.Should().Be(TimeSpan.FromHours(1));
        query.PerReplica.Should().BeFalse();
    }

    [Theory]
    [InlineData("1m", OpsHealthRollupTier.OneMinute)]
    [InlineData("5m", OpsHealthRollupTier.FiveMinute)]
    [InlineData("1h", OpsHealthRollupTier.Hourly)]
    [Operation(Operations.TestInfrastructure)]
    public void HistoryQuery_ParsesResolution(string resolution, OpsHealthRollupTier expected)
    {
        OpsHealthHistoryQuery.TryParse("2h", resolution, perReplica: true, out _, out var query).Should().BeTrue();
        query.Tier.Should().Be(expected);
        query.PerReplica.Should().BeTrue();
    }

    [Fact]
    [Operation(Operations.TestInfrastructure)]
    public void HistoryQuery_ClampsWindowToTierRetentionCap()
    {
        // 1-minute tier is retained 24h, so a 90d request is clamped down.
        OpsHealthHistoryQuery.TryParse("90d", "1m", perReplica: false, out _, out var query).Should().BeTrue();
        query.Window.Should().Be(TimeSpan.FromHours(24));
    }

    [Theory]
    [InlineData("13m")]
    [InlineData("nonsense")]
    [Operation(Operations.TestInfrastructure)]
    public void HistoryQuery_RejectsUnsupportedResolution(string resolution)
    {
        OpsHealthHistoryQuery.TryParse("1h", resolution, perReplica: false, out var error, out _).Should().BeFalse();
        error.Should().NotBeNullOrWhiteSpace();
    }

    [Theory]
    [InlineData("0h")]
    [InlineData("abc")]
    [InlineData("10")]
    [Operation(Operations.TestInfrastructure)]
    public void HistoryQuery_RejectsInvalidWindow(string window)
    {
        OpsHealthHistoryQuery.TryParse(window, "1m", perReplica: false, out var error, out _).Should().BeFalse();
        error.Should().NotBeNullOrWhiteSpace();
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public void SamplerCapture_FromTelemetry_HasBoundedOverhead()
    {
        // Warm the shared in-process aggregator with representative traffic.
        for (var i = 0; i < 5000; i++)
        {
            HonuaTelemetry.RecordServingRequest(
                HonuaTelemetry.Protocols.FeatureServer,
                operation: "query",
                statusCode: i % 50 == 0 ? 500 : 200,
                durationMs: 5 + (i % 40));
        }

        // The sampler's per-flush capture cost is dominated by snapshotting the aggregator and shaping the
        // rollup sample. It must be cheap and never touch serving code — assert a generous upper bound.
        const int iterations = 200;
        var stopwatch = Stopwatch.StartNew();
        for (var i = 0; i < iterations; i++)
        {
            var snapshot = HonuaTelemetry.GetServingLatencySnapshot();
            var latency = snapshot.Protocols
                .Select(p => new OpsHealthLatencyPoint
                {
                    Protocol = p.Protocol,
                    RequestCount = p.RequestCount,
                    ErrorCount = p.ErrorCount,
                    P50Ms = p.P50Ms,
                    P95Ms = p.P95Ms,
                    P99Ms = p.P99Ms,
                    MaxMs = p.MaxMs,
                })
                .ToList();
            latency.Should().NotBeNull();
        }

        stopwatch.Stop();
        var perCaptureMs = stopwatch.Elapsed.TotalMilliseconds / iterations;
        perCaptureMs.Should().BeLessThan(25);
    }
}
