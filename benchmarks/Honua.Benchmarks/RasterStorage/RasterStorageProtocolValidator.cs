// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Benchmarks.RasterStorage;

internal static class RasterStorageProtocolValidator
{
    public static void ValidateDefinition(RasterStorageProtocolDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (!string.Equals(definition.ProtocolVersion, RasterStorageProtocol.Version, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Unsupported raster storage protocol version '{definition.ProtocolVersion}'.");
        }

        var duplicateFixtures = definition.Fixtures
            .GroupBy(fixture => fixture.Id, StringComparer.Ordinal)
            .Where(group => group.Count() != 1)
            .Select(group => group.Key)
            .ToArray();
        if (duplicateFixtures.Length > 0)
        {
            throw new InvalidDataException($"Duplicate fixture identifiers: {string.Join(", ", duplicateFixtures)}.");
        }

        foreach (var fixture in definition.Fixtures)
        {
            if (fixture.Scenes.Count == 0)
            {
                throw new InvalidDataException($"Fixture '{fixture.Id}' contains no scenes.");
            }

            var alignment = RasterGridAlignment.Analyze(fixture.Scenes);
            var actual = alignment.IsAligned ? GridExpectation.Aligned : GridExpectation.Misaligned;
            if (actual != fixture.AlignmentExpectation)
            {
                throw new InvalidDataException(
                    $"Fixture '{fixture.Id}' expected {fixture.AlignmentExpectation} but analyzed as {actual}.");
            }
        }

        var cells = definition.Cells
            .GroupBy(cell => (cell.Layout, cell.Workload))
            .ToDictionary(group => group.Key, group => group.ToArray());
        foreach (var layout in Enum.GetValues<RasterStorageLayout>())
        {
            foreach (var workload in Enum.GetValues<RasterStorageWorkload>())
            {
                if (!cells.TryGetValue((layout, workload), out var matches) || matches.Length != 1)
                {
                    throw new InvalidDataException(
                        $"Protocol must contain exactly one support cell for {layout}/{workload}.");
                }
            }
        }

        if (cells.Count != Enum.GetValues<RasterStorageLayout>().Length * Enum.GetValues<RasterStorageWorkload>().Length)
        {
            throw new InvalidDataException("Protocol contains an unknown or duplicate storage/workload cell.");
        }

        var missingMetrics = RasterStorageMetricNames.Required
            .Except(definition.RequiredMetrics, StringComparer.Ordinal)
            .ToArray();
        if (missingMetrics.Length > 0)
        {
            throw new InvalidDataException($"Protocol omits required metrics: {string.Join(", ", missingMetrics)}.");
        }
    }

    public static void ValidateRun(RasterStorageProtocolDefinition definition, RasterStorageBenchmarkRun run)
    {
        ValidateDefinition(definition);
        ArgumentNullException.ThrowIfNull(run);
        if (!string.Equals(run.ProtocolVersion, definition.ProtocolVersion, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Run protocol '{run.ProtocolVersion}' does not match '{definition.ProtocolVersion}'.");
        }

        if (run.CompletedAt < run.StartedAt)
        {
            throw new InvalidDataException("Run completion time precedes its start time.");
        }

        var fixtures = definition.Fixtures.Select(fixture => fixture.Id).ToHashSet(StringComparer.Ordinal);
        var cells = definition.Cells.ToDictionary(cell => (cell.Layout, cell.Workload));
        foreach (var result in run.Results)
        {
            if (!fixtures.Contains(result.FixtureId))
            {
                throw new InvalidDataException($"Result references unknown fixture '{result.FixtureId}'.");
            }

            if (!cells.TryGetValue((result.Layout, result.Workload), out var cell))
            {
                throw new InvalidDataException($"Result references unknown cell {result.Layout}/{result.Workload}.");
            }

            if (cell.Support == BenchmarkSupport.Unsupported && result.Status != BenchmarkResultStatus.Unsupported)
            {
                throw new InvalidDataException(
                    $"Unsupported cell {result.Layout}/{result.Workload} must remain explicitly unsupported.");
            }

            if (result.Status == BenchmarkResultStatus.Completed)
            {
                ValidateCompletedResult(definition, result);
            }
            else if (string.IsNullOrWhiteSpace(result.Reason))
            {
                throw new InvalidDataException(
                    $"Non-completed result {result.Layout}/{result.Workload}/{result.FixtureId} requires a reason.");
            }
        }
    }

    private static void ValidateCompletedResult(
        RasterStorageProtocolDefinition definition,
        RasterStorageWorkloadResult result)
    {
        if (result.LatencySamplesMilliseconds.Count == 0 ||
            result.LatencyP50Milliseconds is null ||
            result.LatencyP95Milliseconds is null)
        {
            throw new InvalidDataException(
                $"Completed result {result.Layout}/{result.Workload}/{result.FixtureId} has no latency distribution.");
        }

        var expectedP50 = RasterStorageStatistics.Percentile(result.LatencySamplesMilliseconds, 0.50);
        var expectedP95 = RasterStorageStatistics.Percentile(result.LatencySamplesMilliseconds, 0.95);
        if (!NearlyEqual(result.LatencyP50Milliseconds.Value, expectedP50) ||
            !NearlyEqual(result.LatencyP95Milliseconds.Value, expectedP95))
        {
            throw new InvalidDataException(
                $"Completed result {result.Layout}/{result.Workload}/{result.FixtureId} contains stale percentile values.");
        }

        var metrics = result.Metrics.ToDictionary(metric => metric.Name, StringComparer.Ordinal);
        foreach (var required in definition.RequiredMetrics)
        {
            if (!metrics.TryGetValue(required, out var metric))
            {
                throw new InvalidDataException(
                    $"Completed result {result.Layout}/{result.Workload}/{result.FixtureId} omits '{required}'.");
            }

            if (metric.Availability == MetricAvailability.Measured && metric.Value is null)
            {
                throw new InvalidDataException($"Measured metric '{required}' has no value.");
            }

            if (metric.Availability != MetricAvailability.Measured && string.IsNullOrWhiteSpace(metric.Reason))
            {
                throw new InvalidDataException($"Non-measured metric '{required}' requires a reason.");
            }
        }
    }

    private static bool NearlyEqual(double left, double right)
        => Math.Abs(left - right) <= Math.Max(0.000_001, Math.Abs(right) * 0.000_001);
}
