// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Observability.Domain;

namespace Honua.Core.Tests.Features.Observability;

/// <summary>
/// Unit tests for <see cref="OpsHealthRollupAggregation"/>: the cross-replica merge helpers that back the
/// cluster-wide serving-latency aggregation and the history read API (#2553).
/// </summary>
public class OpsHealthRollupAggregationTests
{
    [Fact]
    public void MergeAcrossReplicas_Singlepoint_ReturnsSamePointWithProtocol()
    {
        var point = new OpsHealthLatencyPoint
        {
            Protocol = "FeatureServer",
            RequestCount = 10,
            ErrorCount = 1,
            P50Ms = 5,
            P95Ms = 20,
            P99Ms = 40,
            MaxMs = 80,
        };

        var merged = OpsHealthRollupAggregation.MergeAcrossReplicas("FeatureServer", [point]);

        merged.Should().Be(point);
    }

    [Fact]
    public void MergeAcrossReplicas_SumsCountsAndWeightsPercentilesByRequestCount()
    {
        var a = new OpsHealthLatencyPoint
        {
            Protocol = "OGC-Features",
            RequestCount = 100,
            ErrorCount = 5,
            P50Ms = 10,
            P95Ms = 100,
            P99Ms = 200,
            MaxMs = 300,
        };
        var b = new OpsHealthLatencyPoint
        {
            Protocol = "OGC-Features",
            RequestCount = 300,
            ErrorCount = 15,
            P50Ms = 20,
            P95Ms = 200,
            P99Ms = 400,
            MaxMs = 250,
        };

        var merged = OpsHealthRollupAggregation.MergeAcrossReplicas("OGC-Features", [a, b]);

        merged.RequestCount.Should().Be(400);
        merged.ErrorCount.Should().Be(20);
        // Request-count-weighted mean: (10*100 + 20*300) / 400 = 17.5
        merged.P50Ms.Should().BeApproximately(17.5, 1e-9);
        // (100*100 + 200*300) / 400 = 175
        merged.P95Ms.Should().BeApproximately(175, 1e-9);
        // (200*100 + 400*300) / 400 = 350
        merged.P99Ms.Should().BeApproximately(350, 1e-9);
        // Max feeds the max.
        merged.MaxMs.Should().Be(300);
        merged.ErrorRate.Should().BeApproximately(20d / 400d, 1e-9);
    }

    [Fact]
    public void MergeAcrossReplicas_WithZeroRequests_FallsBackToMaxPercentiles()
    {
        var a = new OpsHealthLatencyPoint { Protocol = "WFS-2.0", RequestCount = 0, ErrorCount = 0, P50Ms = 5, P95Ms = 9, P99Ms = 12, MaxMs = 20 };
        var b = new OpsHealthLatencyPoint { Protocol = "WFS-2.0", RequestCount = 0, ErrorCount = 0, P50Ms = 7, P95Ms = 11, P99Ms = 15, MaxMs = 18 };

        var merged = OpsHealthRollupAggregation.MergeAcrossReplicas("WFS-2.0", [a, b]);

        merged.RequestCount.Should().Be(0);
        merged.P50Ms.Should().Be(7);
        merged.P95Ms.Should().Be(11);
        merged.P99Ms.Should().Be(15);
        merged.MaxMs.Should().Be(20);
        merged.ErrorRate.Should().Be(0);
    }

    [Fact]
    public void MergeVitalsAcrossReplicas_TakesWorstStatusPeakBacklogAndAveragedRatios()
    {
        var a = new OpsHealthVitalsPoint
        {
            OverallStatus = "Healthy",
            GpQueueTotal = 3,
            AlertPending = 10,
            AlertDeadLettered = 0,
            DbPoolUtilization = 0.4,
            DbActiveConnections = 5,
            CacheHitRatio = 0.9,
            ErrorRate = 0.01,
        };
        var b = new OpsHealthVitalsPoint
        {
            OverallStatus = "Degraded",
            GpQueueTotal = 8,
            AlertPending = 25,
            AlertDeadLettered = 2,
            DbPoolUtilization = 0.6,
            DbActiveConnections = 7,
            CacheHitRatio = 0.7,
            ErrorRate = 0.03,
        };

        var merged = OpsHealthRollupAggregation.MergeVitalsAcrossReplicas([a, b]);

        merged.OverallStatus.Should().Be("Degraded");
        merged.GpQueueTotal.Should().Be(8);
        merged.AlertPending.Should().Be(25);
        merged.AlertDeadLettered.Should().Be(2);
        merged.DbActiveConnections.Should().Be(12);
        merged.DbPoolUtilization.Should().BeApproximately(0.5, 1e-9);
        merged.CacheHitRatio.Should().BeApproximately(0.8, 1e-9);
        merged.ErrorRate.Should().BeApproximately(0.02, 1e-9);
    }

    [Fact]
    public void MergeVitalsAcrossReplicas_DedupsGpQueueBreakdownWithPerKeyMax()
    {
        // The GP queue is a GLOBAL view: every replica queries the same shared job store, so two replicas
        // reporting the identical queue must merge to that queue (dedup by max), never to double the depth.
        var a = new OpsHealthVitalsPoint
        {
            OverallStatus = "Healthy",
            GpQueueTotal = 5,
            GpQueueBreakdown = new Dictionary<string, int>
            {
                ["Queued|local"] = 3,
                ["Running|local"] = 2,
            },
            DbActiveConnections = 1,
            CacheHitRatio = 1,
            ErrorRate = 0,
        };
        // Same global queue observed slightly later by a second replica: one queued job started running on
        // aws-batch, so its per-key values differ but describe the same queue.
        var b = new OpsHealthVitalsPoint
        {
            OverallStatus = "Healthy",
            GpQueueTotal = 5,
            GpQueueBreakdown = new Dictionary<string, int>
            {
                ["Queued|local"] = 3,
                ["Running|local"] = 1,
                ["Running|aws-batch"] = 1,
            },
            DbActiveConnections = 1,
            CacheHitRatio = 1,
            ErrorRate = 0,
        };

        var merged = OpsHealthRollupAggregation.MergeVitalsAcrossReplicas([a, b]);

        merged.GpQueueBreakdown.Should().HaveCount(3);
        // Identical global observation is deduped, not summed (3, never 6).
        merged.GpQueueBreakdown["Queued|local"].Should().Be(3);
        // Differing observations of the same key keep the peak.
        merged.GpQueueBreakdown["Running|local"].Should().Be(2);
        merged.GpQueueBreakdown["Running|aws-batch"].Should().Be(1);
    }

    [Theory]
    [InlineData("Unhealthy", 3)]
    [InlineData("Degraded", 2)]
    [InlineData("Healthy", 0)]
    [InlineData("Weird", 1)]
    [InlineData(null, 1)]
    public void StatusRank_OrdersBySeverity(string? status, int expected)
    {
        OpsHealthRollupAggregation.StatusRank(status).Should().Be(expected);
    }
}
