// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.Migration.Services;
using Honua.Core.Features.FileImport.Abstractions;
using Honua.Core.Features.FileImport.Domain;
using Honua.Core.Features.FileImport.Services;
using Honua.Core.Features.FileImport.Services.FileGdb;

namespace Honua.Core.Features.Migration.Services;

/// <summary>
/// Evaluates a <see cref="MigrationRunMetricsArtifact"/> against a
/// <see cref="MigrationMetricBaseline"/> and emits a
/// <see cref="MigrationRunMetricsBaselineArtifact"/> with a Pass/Warn/Fail status.
/// </summary>
public static class MigrationRunMetricsBaselineEvaluator
{
    /// <summary>
    /// Classify a slice-1 run-metrics artifact against the supplied baseline.
    /// </summary>
    /// <param name="run">Run-metrics artifact emitted by <see cref="MigrationRunMetricsBuilder"/>.</param>
    /// <param name="baseline">Baseline to evaluate against.</param>
    public static MigrationRunMetricsBaselineArtifact Evaluate(
        MigrationRunMetricsArtifact run,
        MigrationMetricBaseline baseline)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(baseline);

        var signals = new List<MigrationMetricBaselineSignal>(baseline.Bands.Length);
        foreach (var band in baseline.Bands)
        {
            signals.Add(EvaluateMetric(band, run.Totals));
        }

        var status = AggregateStatus(signals);
        var summary = BuildSummary(status, signals);

        return new MigrationRunMetricsBaselineArtifact
        {
            SourceFamily = baseline.SourceFamily,
            Size = baseline.Size,
            BaselineProfile = baseline.ProfileName,
            Status = status,
            Summary = summary,
            RunId = run.RunId,
            MeasurementScope = run.MeasurementScope,
            Signals = signals.ToArray(),
            FixtureProfile = baseline.FixtureProfile
        };
    }

    /// <summary>
    /// Convenience overload that looks up the baseline from
    /// <see cref="MigrationFixtureBaselineCatalog"/> using the run artifact's source family.
    /// </summary>
    /// <param name="run">Run-metrics artifact.</param>
    /// <param name="size">Fixture size to evaluate against.</param>
    public static MigrationRunMetricsBaselineArtifact? TryEvaluate(
        MigrationRunMetricsArtifact run,
        string size)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentException.ThrowIfNullOrWhiteSpace(size);

        return MigrationFixtureBaselineCatalog.TryGet(run.SourceFamily, size, out var baseline)
            ? Evaluate(run, baseline)
            : null;
    }

    private static MigrationMetricBaselineSignal EvaluateMetric(
        MigrationMetricBaselineBand band,
        MigrationRunMetricsValues totals)
    {
        var observed = ReadMetric(band.Metric, totals);
        if (observed == null)
        {
            return new MigrationMetricBaselineSignal
            {
                Metric = band.Metric,
                Status = MigrationMetricBaselineStatuses.Warn,
                Unit = band.Unit,
                Observed = null,
                WarnThreshold = band.LowerBound ? band.WarnAtOrBelow : band.WarnAtOrAbove,
                FailThreshold = band.LowerBound ? band.FailAtOrBelow : band.FailAtOrAbove,
                Summary = $"{band.Metric} was not measured."
            };
        }

        if (observed < 0)
        {
            return new MigrationMetricBaselineSignal
            {
                Metric = band.Metric,
                Status = MigrationMetricBaselineStatuses.Fail,
                Unit = band.Unit,
                Observed = observed,
                WarnThreshold = band.LowerBound ? band.WarnAtOrBelow : band.WarnAtOrAbove,
                FailThreshold = band.LowerBound ? band.FailAtOrBelow : band.FailAtOrAbove,
                Summary = $"{band.Metric} must not be negative."
            };
        }

        var status = band.LowerBound
            ? ClassifyLowerBound(observed.Value, band.WarnAtOrBelow, band.FailAtOrBelow)
            : ClassifyUpperBound(observed.Value, band.WarnAtOrAbove, band.FailAtOrAbove);

        return new MigrationMetricBaselineSignal
        {
            Metric = band.Metric,
            Status = status,
            Unit = band.Unit,
            Observed = observed,
            WarnThreshold = band.LowerBound ? band.WarnAtOrBelow : band.WarnAtOrAbove,
            FailThreshold = band.LowerBound ? band.FailAtOrBelow : band.FailAtOrAbove,
            Summary = BuildSignalSummary(band.Metric, status, band.LowerBound)
        };
    }

    private static double? ReadMetric(string metric, MigrationRunMetricsValues totals) => metric switch
    {
        "durationMilliseconds" => totals.DurationMilliseconds,
        "sourceRequestCount" => totals.SourceRequestCount,
        "bytesRead" => totals.BytesRead,
        "bytesWritten" => totals.BytesWritten,
        "retryCount" => totals.RetryCount,
        "resumeCount" => totals.ResumeCount,
        "cpuMilliseconds" => totals.CpuMilliseconds,
        "peakMemoryBytes" => totals.PeakMemoryBytes,
        "databaseGrowthBytes" => totals.DatabaseGrowthBytes,
        "databaseGrowthRows" => totals.DatabaseGrowthRows,
        "artifactBytes" => totals.ArtifactBytes,
        "resourceCount" => totals.ResourceCount,
        "featureCount" => totals.FeatureCount,
        "coverageCount" => totals.CoverageCount,
        "resourceThroughputPerSecond" => totals.ResourceThroughputPerSecond,
        "featureThroughputPerSecond" => totals.FeatureThroughputPerSecond,
        "coverageThroughputPerSecond" => totals.CoverageThroughputPerSecond,
        "manualReviewRatio" => totals.ManualReviewRatio,
        _ => null
    };

    private static string ClassifyUpperBound(double observed, double? warn, double? fail)
    {
        if (fail != null && observed >= fail.Value)
        {
            return MigrationMetricBaselineStatuses.Fail;
        }

        if (warn != null && observed >= warn.Value)
        {
            return MigrationMetricBaselineStatuses.Warn;
        }

        return MigrationMetricBaselineStatuses.Pass;
    }

    private static string ClassifyLowerBound(double observed, double? warn, double? fail)
    {
        if (fail != null && observed <= fail.Value)
        {
            return MigrationMetricBaselineStatuses.Fail;
        }

        if (warn != null && observed <= warn.Value)
        {
            return MigrationMetricBaselineStatuses.Warn;
        }

        return MigrationMetricBaselineStatuses.Pass;
    }

    private static string AggregateStatus(IReadOnlyCollection<MigrationMetricBaselineSignal> signals)
    {
        if (signals.Any(static signal => signal.Status == MigrationMetricBaselineStatuses.Fail))
        {
            return MigrationMetricBaselineStatuses.Fail;
        }

        if (signals.Any(static signal => signal.Status == MigrationMetricBaselineStatuses.Warn))
        {
            return MigrationMetricBaselineStatuses.Warn;
        }

        return MigrationMetricBaselineStatuses.Pass;
    }

    private static string BuildSignalSummary(string metric, string status, bool lowerBound) => status switch
    {
        MigrationMetricBaselineStatuses.Fail => lowerBound
            ? $"{metric} is below the failure floor."
            : $"{metric} reached the failure threshold.",
        MigrationMetricBaselineStatuses.Warn => lowerBound
            ? $"{metric} is below the warning floor."
            : $"{metric} reached the warning threshold.",
        _ => $"{metric} is within baseline."
    };

    private static string BuildSummary(string status, IReadOnlyCollection<MigrationMetricBaselineSignal> signals)
    {
        var failCount = signals.Count(static s => s.Status == MigrationMetricBaselineStatuses.Fail);
        var warnCount = signals.Count(static s => s.Status == MigrationMetricBaselineStatuses.Warn);

        return status switch
        {
            MigrationMetricBaselineStatuses.Fail => $"Migration run failed baseline with {failCount} metric(s) at fail and {warnCount} metric(s) at warn.",
            MigrationMetricBaselineStatuses.Warn => $"Migration run is within fail bounds but has {warnCount} metric(s) at warn.",
            _ => "Migration run is within baseline for all evaluated metrics."
        };
    }
}
