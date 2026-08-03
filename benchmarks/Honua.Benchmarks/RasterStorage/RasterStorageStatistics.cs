// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Benchmarks.RasterStorage;

internal static class RasterStorageStatistics
{
    public static double Percentile(IReadOnlyList<double> samples, double percentile)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (samples.Count == 0)
        {
            throw new ArgumentException("At least one sample is required.", nameof(samples));
        }

        if (percentile is <= 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(percentile), percentile, "Percentile must be in (0, 1].");
        }

        var ordered = samples.Order().ToArray();
        var rank = Math.Max(0, (int)Math.Ceiling(percentile * ordered.Length) - 1);
        return ordered[rank];
    }

    public static RasterStorageWorkloadResult CreateCompletedResult(
        RasterStorageLayout layout,
        string fixtureId,
        RasterStorageWorkload workload,
        int warmupCount,
        IReadOnlyList<double> samples,
        IReadOnlyList<MetricObservation> metrics)
        => new(
            layout,
            fixtureId,
            workload,
            BenchmarkResultStatus.Completed,
            warmupCount,
            samples,
            Percentile(samples, 0.50),
            Percentile(samples, 0.95),
            metrics);
}
