// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Observability.Domain;

// Percentiles are recomputed from merged histograms because averaging per-replica percentiles hides a small slow replica.
public sealed record LatencyDistribution
{
    public static IReadOnlyList<double> BucketUpperBoundsMs { get; } =
    [
        1, 2, 3, 5, 8, 13, 21, 34, 55, 89, 144, 233, 377, 610, 987, 1597, 2584, 4181, 6765, 10000, 20000, 30000, 60000,
    ];

    public static int BucketCount => BucketUpperBoundsMs.Count + 1;

    public required IReadOnlyList<long> BucketCounts { get; init; }

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

    public static LatencyDistribution Empty { get; } = new() { BucketCounts = new long[BucketCount] };

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
