// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Observability.Domain;

namespace Honua.Core.Tests.Features.Observability;

/// <summary>
/// Unit tests for <see cref="LatencyDistribution"/>: the mergeable fixed-bucket latency histogram that lets
/// cluster percentiles be recomputed from merged distributions rather than a mean of percentiles (#2809).
/// </summary>
public class LatencyDistributionTests
{
    [Fact]
    public void FromDurations_BucketsCountsAndReportsTotal()
    {
        var distribution = LatencyDistribution.FromDurations([4.0, 4.5, 100.0]);

        distribution.TotalCount.Should().Be(3);
        distribution.BucketCounts.Should().HaveCount(LatencyDistribution.BucketCount);
    }

    [Fact]
    public void FromDurations_IgnoresNegativeAndNaN()
    {
        var distribution = LatencyDistribution.FromDurations([-1.0, double.NaN, 5.0]);

        distribution.TotalCount.Should().Be(1);
    }

    [Fact]
    public void Quantile_OnEmptyDistribution_ReturnsZero()
    {
        LatencyDistribution.Empty.Quantile(0.99).Should().Be(0);
    }

    [Fact]
    public void Quantile_NearestRank_ReturnsContainingBucketUpperBound()
    {
        // 99 samples at 5ms, 1 sample at 9000ms. p99 (99th of 100 == the fast bucket), p100 hits the slow one.
        var durations = Enumerable.Repeat(5.0, 99).Append(9000.0).ToArray();
        var distribution = LatencyDistribution.FromDurations(durations);

        distribution.TotalCount.Should().Be(100);
        distribution.Quantile(0.50).Should().BeLessThan(50);
        // The single slow sample is the 100th; the max quantile must land in the multi-second bucket.
        distribution.Quantile(1.0).Should().BeGreaterThanOrEqualTo(9000);
    }

    [Fact]
    public void Merge_IsElementwiseAdditionOfCounts()
    {
        var a = LatencyDistribution.FromDurations(Enumerable.Repeat(5.0, 10).ToArray());
        var b = LatencyDistribution.FromDurations(Enumerable.Repeat(5000.0, 4).ToArray());

        var merged = a.Merge(b);

        merged.TotalCount.Should().Be(14);
        // The merged distribution reflects both sample sets: p50 stays fast, high quantile reflects the slow set.
        merged.Quantile(0.50).Should().BeLessThan(50);
        merged.Quantile(0.99).Should().BeGreaterThanOrEqualTo(5000);
    }
}
