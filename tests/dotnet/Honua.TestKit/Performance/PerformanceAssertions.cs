// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Diagnostics;
using FluentAssertions;

namespace Honua.TestKit.Performance;

public static class PerformanceAssertions
{
    public static async Task<T> ShouldCompleteWithin<T>(
        this Task<T> operation,
        TimeSpan timeLimit,
        string? because = null)
    {
        var stopwatch = Stopwatch.StartNew();
        var result = await operation;
        stopwatch.Stop();

        stopwatch.Elapsed.Should().BeLessOrEqualTo(timeLimit, because);
        return result;
    }

    public static async Task<(T Result, TimeSpan Duration)> MeasureAsync<T>(Func<Task<T>> operation)
    {
        var stopwatch = Stopwatch.StartNew();
        var result = await operation();
        stopwatch.Stop();
        return (result, stopwatch.Elapsed);
    }

    public static class Thresholds
    {
        public static readonly TimeSpan MetadataQuery = TimeSpan.FromMilliseconds(100);

        public static readonly TimeSpan SmallFeatureQuery = TimeSpan.FromMilliseconds(500);

        public static readonly TimeSpan MediumFeatureQuery = TimeSpan.FromSeconds(2);

        public static readonly TimeSpan LargeSpatialQuery = TimeSpan.FromSeconds(5);

        public static readonly TimeSpan ComplexFilter = TimeSpan.FromSeconds(1);

        public static readonly TimeSpan DatabaseConnection = TimeSpan.FromSeconds(1);

        public static readonly TimeSpan TileGeneration = TimeSpan.FromSeconds(3);
    }

    public static async Task<PerformanceResult> BenchmarkAsync<T>(
        Func<Task<T>> operation,
        int iterations = 10,
        string? operationName = null,
        int warmupIterations = 3)
    {
        // Warm up before measuring so first-request costs (JIT, the first
        // database connection, route/handler compilation) do not skew the
        // steady-state average. These benchmarks assert steady-state latency
        // targets, not cold-start cost; on a loaded shared CI runner a single
        // cold first request over a handful of iterations is enough to push the
        // average past a tight threshold and flake the run. Warmup failures are
        // intentionally swallowed here — they resurface in the measured loop and
        // are reported through SuccessRate.
        for (int i = 0; i < warmupIterations; i++)
        {
            try
            {
                await operation();
            }
            catch (Exception caughtException) when (caughtException is not OutOfMemoryException)
            {
                // Broad catch is intentional (see comment above): warmup failures are
                // ignored here and surfaced by the measured iterations below instead.
            }
        }

        var durations = new List<TimeSpan>();
        var errors = new List<Exception>();

        for (int i = 0; i < iterations; i++)
        {
            try
            {
                var (_, duration) = await MeasureAsync(operation);
                durations.Add(duration);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Broad catch is intentional: any failure counts against SuccessRate/ErrorCount
                // rather than aborting the whole benchmark run.
                errors.Add(ex);
            }

            // Small delay between iterations
            await Task.Delay(10);
        }

        return new PerformanceResult
        {
            OperationName = operationName ?? "Unknown",
            Iterations = iterations,
            SuccessCount = durations.Count,
            ErrorCount = errors.Count,
            Durations = durations,
            Errors = errors
        };
    }

    public static void ShouldMeetCriteria(
        this PerformanceResult result,
        TimeSpan maxAverageTime,
        TimeSpan? maxP95Time = null,
        double minSuccessRate = 0.95)
    {
        result.SuccessRate.Should().BeGreaterOrEqualTo(minSuccessRate,
            $"operation '{result.OperationName}' should have at least {minSuccessRate:P0} success rate");

        if (result.Durations.Count > 0)
        {
            result.AverageTime.Should().BeLessOrEqualTo(maxAverageTime,
                $"operation '{result.OperationName}' average time should be under {maxAverageTime.TotalMilliseconds}ms");

            if (maxP95Time.HasValue && result.Durations.Count >= 20)
            {
                result.P95Time.Should().BeLessOrEqualTo(maxP95Time.Value,
                    $"operation '{result.OperationName}' P95 time should be under {maxP95Time.Value.TotalMilliseconds}ms");
            }
        }
    }
}

public class PerformanceResult
{
    public string OperationName { get; init; } = "";
    public int Iterations { get; init; }
    public int SuccessCount { get; init; }
    public int ErrorCount { get; init; }
    public List<TimeSpan> Durations { get; init; } = new();
    public List<Exception> Errors { get; init; } = new();

    public double SuccessRate => Iterations > 0 ? (double)SuccessCount / Iterations : 0;

    public TimeSpan AverageTime => Durations.Count > 0
        ? TimeSpan.FromMilliseconds(Durations.Average(d => d.TotalMilliseconds))
        : TimeSpan.Zero;

    public TimeSpan MinTime => Durations.Count > 0 ? Durations.Min() : TimeSpan.Zero;
    public TimeSpan MaxTime => Durations.Count > 0 ? Durations.Max() : TimeSpan.Zero;

    public TimeSpan P95Time
    {
        get
        {
            if (Durations.Count == 0)
            {
                return TimeSpan.Zero;
            }

            var sorted = Durations.OrderBy(d => d).ToList();
            var index = (int)Math.Ceiling(sorted.Count * 0.95) - 1;
            return sorted[Math.Max(0, Math.Min(index, sorted.Count - 1))];
        }
    }

    public TimeSpan P99Time
    {
        get
        {
            if (Durations.Count == 0)
            {
                return TimeSpan.Zero;
            }

            var sorted = Durations.OrderBy(d => d).ToList();
            var index = (int)Math.Ceiling(sorted.Count * 0.99) - 1;
            return sorted[Math.Max(0, Math.Min(index, sorted.Count - 1))];
        }
    }

    public override string ToString()
    {
        return $"Performance Result for '{OperationName}': " +
               $"Success Rate: {SuccessRate:P1}, " +
               $"Average: {AverageTime.TotalMilliseconds:F1}ms, " +
               $"P95: {P95Time.TotalMilliseconds:F1}ms, " +
               $"Min: {MinTime.TotalMilliseconds:F1}ms, " +
               $"Max: {MaxTime.TotalMilliseconds:F1}ms";
    }
}
