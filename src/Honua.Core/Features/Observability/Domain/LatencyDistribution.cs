// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Observability.Domain;

/// <summary>
/// A compact, mergeable fixed-bucket latency histogram used so cluster-wide serving-latency percentiles
/// can be recomputed from merged per-replica DISTRIBUTIONS rather than from a request-weighted mean of
/// per-replica percentiles (#2809). Averaging pre-aggregated percentiles hides a sick replica: a small
/// replica serving 100% slow traffic is diluted away by the healthy majority. Summing histograms keeps the
/// slow replica's samples in the high buckets, so the merged p95/p99 surface the tail.
/// </summary>
/// <remarks>
/// Buckets are fixed log-scale upper bounds in milliseconds (plus a terminal overflow bucket), so two
/// distributions are mergeable by element-wise addition and the sketch is bounded (a fixed-length count
/// vector, AOT-safe with no reflection). Quantiles use the nearest-rank rule over the cumulative counts and
/// report the containing bucket's upper bound — an approximation on the order of one bucket width, which is
/// the documented trade for cross-replica mergeability. The true peak is carried separately as
/// <see cref="OpsHealthLatencyPoint.MaxMs"/>.
/// </remarks>
public sealed record LatencyDistribution
{
    /// <summary>
    /// Fixed bucket upper bounds in milliseconds. A sample with duration <c>d</c> falls in the first bucket
    /// whose bound is <c>&gt;= d</c>; samples above the last bound fall in the terminal overflow bucket.
    /// </summary>
    public static IReadOnlyList<double> BucketUpperBoundsMs { get; } =
    [
        1, 2, 3, 5, 8, 13, 21, 34, 55, 89, 144, 233, 377, 610, 987, 1597, 2584, 4181, 6765, 10000, 20000, 30000, 60000,
    ];

    /// <summary>Number of count slots (one per finite bucket plus the terminal overflow bucket).</summary>
    public static int BucketCount => BucketUpperBoundsMs.Count + 1;

    /// <summary>
    /// Per-bucket sample counts. Length is always <see cref="BucketCount"/>; the final slot is the overflow
    /// bucket (durations above the last finite bound).
    /// </summary>
    public required IReadOnlyList<long> BucketCounts { get; init; }

    /// <summary>Total number of samples across all buckets.</summary>
    public long TotalCount
    {
        get
        {
            long total = 0;
            foreach (var count in BucketCounts)
            {
                total += count;
            }

            return total;
        }
    }

    /// <summary>Returns an empty distribution (all bucket counts zero).</summary>
    public static LatencyDistribution Empty { get; } = new() { BucketCounts = new long[BucketCount] };

    /// <summary>
    /// Builds a distribution from a set of raw sample durations (milliseconds). Negative or NaN samples are
    /// ignored. Order does not matter.
    /// </summary>
    /// <param name="durationsMs">The raw per-request durations in milliseconds.</param>
    /// <returns>The bucketed distribution.</returns>
    public static LatencyDistribution FromDurations(IReadOnlyList<double> durationsMs)
    {
        ArgumentNullException.ThrowIfNull(durationsMs);
        var counts = new long[BucketCount];
        foreach (var duration in durationsMs)
        {
            if (double.IsNaN(duration) || duration < 0)
            {
                continue;
            }

            counts[BucketIndex(duration)]++;
        }

        return new LatencyDistribution { BucketCounts = counts };
    }

    /// <summary>
    /// Merges this distribution with <paramref name="other"/> by element-wise addition. Both distributions
    /// share the fixed bucket layout, so the sum is an exact merge of the two underlying sample sets.
    /// </summary>
    /// <param name="other">The distribution to add.</param>
    /// <returns>The merged distribution.</returns>
    public LatencyDistribution Merge(LatencyDistribution other)
    {
        ArgumentNullException.ThrowIfNull(other);
        var counts = new long[BucketCount];
        for (var i = 0; i < BucketCount; i++)
        {
            counts[i] = BucketCounts[i] + other.BucketCounts[i];
        }

        return new LatencyDistribution { BucketCounts = counts };
    }

    /// <summary>
    /// Computes a nearest-rank quantile from the cumulative bucket counts, returning the containing bucket's
    /// upper bound in milliseconds. Returns 0 for an empty distribution.
    /// </summary>
    /// <param name="quantile">The quantile in the inclusive range [0, 1] (e.g. 0.95 for p95).</param>
    /// <returns>The bucket-upper-bound estimate for the quantile, in milliseconds.</returns>
    public double Quantile(double quantile)
    {
        var total = TotalCount;
        if (total == 0)
        {
            return 0;
        }

        var clamped = Math.Clamp(quantile, 0d, 1d);
        // Nearest-rank: the smallest rank r (1-based) such that cumulative count >= ceil(q * n).
        var targetRank = (long)Math.Ceiling(clamped * total);
        targetRank = Math.Clamp(targetRank, 1, total);

        long cumulative = 0;
        for (var i = 0; i < BucketCount; i++)
        {
            cumulative += BucketCounts[i];
            if (cumulative >= targetRank)
            {
                return i < BucketUpperBoundsMs.Count
                    ? BucketUpperBoundsMs[i]
                    : BucketUpperBoundsMs[^1];
            }
        }

        return BucketUpperBoundsMs[^1];
    }

    private static int BucketIndex(double durationMs)
    {
        for (var i = 0; i < BucketUpperBoundsMs.Count; i++)
        {
            if (durationMs <= BucketUpperBoundsMs[i])
            {
                return i;
            }
        }

        return BucketUpperBoundsMs.Count;
    }
}
