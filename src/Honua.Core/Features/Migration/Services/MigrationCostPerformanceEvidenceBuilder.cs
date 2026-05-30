// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.Migration.Services;
using Honua.Core.Features.FileImport.Abstractions;
using Honua.Core.Features.FileImport.Domain;
using Honua.Core.Features.FileImport.Services;

namespace Honua.Core.Features.Migration.Services;

/// <summary>
/// Builds deterministic, release-safe migration cost and performance evidence artifacts.
/// </summary>
public static class MigrationCostPerformanceEvidenceBuilder
{
    private static readonly string[] RequiredPhaseOrder =
    [
        MigrationCostPerformancePhases.Scan,
        MigrationCostPerformancePhases.Manifest,
        MigrationCostPerformancePhases.Apply,
        MigrationCostPerformancePhases.Import
    ];

    private static readonly string[] RequiredRecoveryScenarios =
    [
        MigrationCostPerformanceRecoveryScenarios.SourceFailure,
        MigrationCostPerformanceRecoveryScenarios.JobCancellation,
        MigrationCostPerformanceRecoveryScenarios.TransientNetworkError,
        MigrationCostPerformanceRecoveryScenarios.RepeatedApplyAttempt
    ];

    /// <summary>
    /// Builds a cost and performance evidence artifact from source inventory and phase measurements.
    /// </summary>
    /// <param name="inventory">Source inventory artifact used to provide safe source context.</param>
    /// <param name="input">Measurement input collected by a scanner, importer, or test harness.</param>
    /// <param name="thresholds">Optional threshold profile. The release evidence baseline is used when omitted.</param>
    /// <returns>A deterministic artifact with private URLs, credentials, and source data omitted.</returns>
    public static MigrationCostPerformanceEvidenceArtifact Build(
        MigrationSourceInventoryArtifact inventory,
        MigrationCostPerformanceEvidenceInput input,
        MigrationCostPerformanceThresholds? thresholds = null)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        ArgumentNullException.ThrowIfNull(input);

        var activeThresholds = thresholds ?? MigrationCostPerformanceThresholds.ReleaseEvidenceBaseline;
        var phases = BuildPhases(input.PhaseMeasurements, activeThresholds);
        var recovery = BuildRecovery(input.RecoveryMeasurements);
        var totals = AggregateMetrics(phases.Select(static phase => phase.Metrics));
        var overallClassification = AggregateClassification(
            phases.Select(static phase => phase.Classification)
                .Concat(recovery.Select(static scenario => scenario.Classification)));

        return new MigrationCostPerformanceEvidenceArtifact
        {
            SourceKind = SafeIdentifier(inventory.SourceKind, "unknown-source"),
            Source = BuildSourceSummary(inventory.Source),
            MeasurementScope = SafeText(input.MeasurementScope, "migration evidence run"),
            RunId = string.IsNullOrWhiteSpace(input.RunId) ? null : SafeIdentifier(input.RunId, "run"),
            FixtureProfile = BuildFixtureProfile(input.FixtureProfile, inventory),
            OverallClassification = overallClassification,
            Summary = BuildSummary(overallClassification, phases, recovery),
            Thresholds = activeThresholds with
            {
                ProfileName = SafeIdentifier(activeThresholds.ProfileName, "custom")
            },
            Totals = totals,
            Phases = phases,
            Recovery = recovery,
            Privacy = new MigrationCostPerformancePrivacySummary
            {
                SourceUrlsIncluded = false,
                CredentialValuesIncluded = false,
                SourceDataIncluded = false,
                OmittedFields =
                [
                    "source.baseUrl",
                    "source query string",
                    "source fragment",
                    "credential values",
                    "source data samples"
                ]
            }
        };
    }

    private static MigrationCostPerformanceSourceSummary BuildSourceSummary(MigrationSourceIdentity source)
        => new()
        {
            DisplayName = SafeText(source.DisplayName, "migration source"),
            Product = SafeNullableText(source.Product),
            Version = SafeNullableText(source.Version),
            ServiceType = SafeNullableText(source.ServiceType)
        };

    private static MigrationCostPerformanceFixtureProfile BuildFixtureProfile(
        MigrationCostPerformanceFixtureProfile? fixture,
        MigrationSourceInventoryArtifact inventory)
        => new()
        {
            SourceFamily = SafeIdentifier(fixture?.SourceFamily ?? inventory.SourceKind, "unknown-source"),
            Size = SafeIdentifier(fixture?.Size, "unspecified"),
            ExpectedResourceCount = fixture?.ExpectedResourceCount,
            ExpectedFeatureCount = fixture?.ExpectedFeatureCount,
            ExpectedCoverageCount = fixture?.ExpectedCoverageCount,
            Description = SafeNullableText(fixture?.Description)
        };

    private static MigrationCostPerformancePhaseEvidence[] BuildPhases(
        IReadOnlyList<MigrationCostPerformancePhaseMeasurement> measurements,
        MigrationCostPerformanceThresholds thresholds)
    {
        var grouped = measurements
            .GroupBy(static measurement => NormalizePhase(measurement.Phase), StringComparer.Ordinal)
            .ToDictionary(
                static group => group.Key,
                static group => group.Select(static measurement => measurement.Metrics).ToArray(),
                StringComparer.Ordinal);
        var orderedPhases = RequiredPhaseOrder
            .Concat(grouped.Keys.Except(RequiredPhaseOrder, StringComparer.Ordinal).Order(StringComparer.Ordinal))
            .ToArray();

        return orderedPhases
            .Select(phase =>
            {
                var measured = grouped.TryGetValue(phase, out var phaseMetrics);
                var metrics = measured ? AggregateMetrics(phaseMetrics!) : new MigrationCostPerformanceMetrics();
                var signals = BuildSignals(phase, metrics, thresholds, measured);

                return new MigrationCostPerformancePhaseEvidence
                {
                    Id = phase,
                    Phase = phase,
                    Classification = AggregateClassification(signals.Select(static signal => signal.Classification)),
                    Metrics = metrics,
                    Signals = signals
                };
            })
            .ToArray();
    }

    private static MigrationCostPerformanceMetrics AggregateMetrics(IEnumerable<MigrationCostPerformanceMetrics> metrics)
    {
        var materialized = metrics.Select(NormalizeMetrics).ToArray();
        var durationMilliseconds = SumLong(materialized, static metric => metric.DurationMilliseconds);
        var resourceCount = SumLong(materialized, static metric => metric.ResourceCount);
        var featureCount = SumLong(materialized, static metric => metric.FeatureCount);
        var coverageCount = SumLong(materialized, static metric => metric.CoverageCount);
        var manualReviewCount = SumInt(materialized, static metric => metric.ManualReviewCount);
        var candidateItemCount = SumInt(materialized, static metric => metric.CandidateItemCount);
        var manualReviewRatio = ComputeManualReviewRatio(manualReviewCount, candidateItemCount) ??
            MaxDouble(materialized, static metric => metric.ManualReviewRatio);

        return new MigrationCostPerformanceMetrics
        {
            DurationMilliseconds = durationMilliseconds,
            SourceRequestCount = SumLong(materialized, static metric => metric.SourceRequestCount),
            BytesRead = SumLong(materialized, static metric => metric.BytesRead),
            BytesWritten = SumLong(materialized, static metric => metric.BytesWritten),
            RetryCount = SumInt(materialized, static metric => metric.RetryCount),
            ResumeCount = SumInt(materialized, static metric => metric.ResumeCount),
            CpuMilliseconds = SumLong(materialized, static metric => metric.CpuMilliseconds),
            PeakMemoryBytes = MaxLong(materialized, static metric => metric.PeakMemoryBytes),
            DatabaseGrowthBytes = SumLong(materialized, static metric => metric.DatabaseGrowthBytes),
            ArtifactBytes = SumLong(materialized, static metric => metric.ArtifactBytes),
            ResourceCount = resourceCount,
            FeatureCount = featureCount,
            CoverageCount = coverageCount,
            ResourceThroughputPerSecond = ComputeThroughput(resourceCount, durationMilliseconds) ??
                SumDouble(materialized, static metric => metric.ResourceThroughputPerSecond),
            FeatureThroughputPerSecond = ComputeThroughput(featureCount, durationMilliseconds) ??
                SumDouble(materialized, static metric => metric.FeatureThroughputPerSecond),
            CoverageThroughputPerSecond = ComputeThroughput(coverageCount, durationMilliseconds) ??
                SumDouble(materialized, static metric => metric.CoverageThroughputPerSecond),
            ManualReviewCount = manualReviewCount,
            CandidateItemCount = candidateItemCount,
            ManualReviewRatio = manualReviewRatio
        };
    }

    private static MigrationCostPerformanceMetrics NormalizeMetrics(MigrationCostPerformanceMetrics metrics)
    {
        var manualReviewRatio = metrics.ManualReviewRatio ??
            ComputeManualReviewRatio(metrics.ManualReviewCount, metrics.CandidateItemCount);

        return metrics with
        {
            ResourceThroughputPerSecond = metrics.ResourceThroughputPerSecond ??
                ComputeThroughput(metrics.ResourceCount, metrics.DurationMilliseconds),
            FeatureThroughputPerSecond = metrics.FeatureThroughputPerSecond ??
                ComputeThroughput(metrics.FeatureCount, metrics.DurationMilliseconds),
            CoverageThroughputPerSecond = metrics.CoverageThroughputPerSecond ??
                ComputeThroughput(metrics.CoverageCount, metrics.DurationMilliseconds),
            ManualReviewRatio = manualReviewRatio
        };
    }

    private static MigrationCostPerformanceSignal[] BuildSignals(
        string phase,
        MigrationCostPerformanceMetrics metrics,
        MigrationCostPerformanceThresholds thresholds,
        bool measured)
    {
        if (!measured)
        {
            return
            [
                new MigrationCostPerformanceSignal
                {
                    Metric = "phaseMeasured",
                    Classification = MigrationCostPerformanceClassifications.Warn,
                    Unit = "boolean",
                    Summary = $"{phase} measurements were not submitted."
                }
            ];
        }

        var signals = new List<MigrationCostPerformanceSignal>();

        AddMetricSignals(signals, "durationMilliseconds", metrics.DurationMilliseconds, "milliseconds", thresholds.DurationWarnMilliseconds, thresholds.DurationFailMilliseconds);
        AddMetricSignals(signals, "sourceRequestCount", metrics.SourceRequestCount, "count", thresholds.SourceRequestWarnCount, thresholds.SourceRequestFailCount);
        AddMetricSignals(signals, "bytesRead", metrics.BytesRead, "bytes", thresholds.BytesReadWarn, thresholds.BytesReadFail);
        AddMetricSignals(signals, "bytesWritten", metrics.BytesWritten, "bytes", thresholds.BytesWrittenWarn, thresholds.BytesWrittenFail);
        AddMetricSignals(signals, "retryCount", metrics.RetryCount, "count", thresholds.RetryWarnCount, thresholds.RetryFailCount);
        AddMetricSignals(signals, "resumeCount", metrics.ResumeCount, "count", thresholds.ResumeWarnCount, thresholds.ResumeFailCount);
        AddMetricSignals(signals, "cpuMilliseconds", metrics.CpuMilliseconds, "milliseconds", thresholds.CpuWarnMilliseconds, thresholds.CpuFailMilliseconds);
        AddMetricSignals(signals, "peakMemoryBytes", metrics.PeakMemoryBytes, "bytes", thresholds.PeakMemoryWarnBytes, thresholds.PeakMemoryFailBytes);
        AddMetricSignals(signals, "databaseGrowthBytes", metrics.DatabaseGrowthBytes, "bytes", thresholds.DatabaseGrowthWarnBytes, thresholds.DatabaseGrowthFailBytes);
        AddMetricSignals(signals, "artifactBytes", metrics.ArtifactBytes, "bytes", thresholds.ArtifactBytesWarn, thresholds.ArtifactBytesFail);
        AddLowerBoundMetricSignals(signals, "resourceThroughputPerSecond", metrics.ResourceThroughputPerSecond, "resources/second", thresholds.ResourceThroughputWarnPerSecond, thresholds.ResourceThroughputFailPerSecond);
        AddLowerBoundMetricSignals(signals, "featureThroughputPerSecond", metrics.FeatureThroughputPerSecond, "features/second", thresholds.FeatureThroughputWarnPerSecond, thresholds.FeatureThroughputFailPerSecond);
        AddLowerBoundMetricSignals(signals, "coverageThroughputPerSecond", metrics.CoverageThroughputPerSecond, "coverages/second", thresholds.CoverageThroughputWarnPerSecond, thresholds.CoverageThroughputFailPerSecond);
        AddMetricSignals(signals, "manualReviewRatio", metrics.ManualReviewRatio, "ratio", thresholds.ManualReviewRatioWarn, thresholds.ManualReviewRatioFail);

        if (signals.Count == 0)
        {
            signals.Add(new MigrationCostPerformanceSignal
            {
                Metric = "phaseMeasured",
                Classification = MigrationCostPerformanceClassifications.Warn,
                Unit = "boolean",
                Summary = $"{phase} was submitted without numeric measurements."
            });
        }

        return signals
            .OrderBy(static signal => signal.Metric, StringComparer.Ordinal)
            .ToArray();
    }

    private static void AddMetricSignals(
        List<MigrationCostPerformanceSignal> signals,
        string metric,
        double? observed,
        string unit,
        double? warnThreshold,
        double? failThreshold)
    {
        if (observed == null)
        {
            return;
        }

        if (observed < 0)
        {
            signals.Add(new MigrationCostPerformanceSignal
            {
                Metric = metric,
                Classification = MigrationCostPerformanceClassifications.Fail,
                Observed = observed,
                Unit = unit,
                WarnThreshold = warnThreshold,
                FailThreshold = failThreshold,
                Summary = $"{metric} must not be negative."
            });
            return;
        }

        var classification = ClassifyUpperBound(observed.Value, warnThreshold, failThreshold);
        signals.Add(new MigrationCostPerformanceSignal
        {
            Metric = metric,
            Classification = classification,
            Observed = observed,
            Unit = unit,
            WarnThreshold = warnThreshold,
            FailThreshold = failThreshold,
            Summary = BuildSignalSummary(metric, classification)
        });
    }

    private static void AddLowerBoundMetricSignals(
        List<MigrationCostPerformanceSignal> signals,
        string metric,
        double? observed,
        string unit,
        double? warnThreshold,
        double? failThreshold)
    {
        if (observed == null)
        {
            return;
        }

        if (observed < 0)
        {
            signals.Add(new MigrationCostPerformanceSignal
            {
                Metric = metric,
                Classification = MigrationCostPerformanceClassifications.Fail,
                Observed = observed,
                Unit = unit,
                WarnThreshold = warnThreshold,
                FailThreshold = failThreshold,
                Summary = $"{metric} must not be negative."
            });
            return;
        }

        var classification = ClassifyLowerBound(observed.Value, warnThreshold, failThreshold);
        signals.Add(new MigrationCostPerformanceSignal
        {
            Metric = metric,
            Classification = classification,
            Observed = observed,
            Unit = unit,
            WarnThreshold = warnThreshold,
            FailThreshold = failThreshold,
            Summary = BuildLowerBoundSignalSummary(metric, classification)
        });
    }

    private static string ClassifyUpperBound(double observed, double? warnThreshold, double? failThreshold)
    {
        if (failThreshold != null && observed >= failThreshold.Value)
        {
            return MigrationCostPerformanceClassifications.Fail;
        }

        if (warnThreshold != null && observed >= warnThreshold.Value)
        {
            return MigrationCostPerformanceClassifications.Warn;
        }

        return MigrationCostPerformanceClassifications.Pass;
    }

    private static string ClassifyLowerBound(double observed, double? warnThreshold, double? failThreshold)
    {
        if (failThreshold != null && observed <= failThreshold.Value)
        {
            return MigrationCostPerformanceClassifications.Fail;
        }

        if (warnThreshold != null && observed <= warnThreshold.Value)
        {
            return MigrationCostPerformanceClassifications.Warn;
        }

        return MigrationCostPerformanceClassifications.Pass;
    }

    private static string BuildSignalSummary(string metric, string classification)
        => classification switch
        {
            MigrationCostPerformanceClassifications.Fail => $"{metric} reached the failure threshold.",
            MigrationCostPerformanceClassifications.Warn => $"{metric} reached the warning threshold.",
            _ => $"{metric} is within baseline."
        };

    private static string BuildLowerBoundSignalSummary(string metric, string classification)
        => classification switch
        {
            MigrationCostPerformanceClassifications.Fail => $"{metric} is below the failure threshold.",
            MigrationCostPerformanceClassifications.Warn => $"{metric} is below the warning threshold.",
            _ => $"{metric} is within baseline."
        };

    private static string AggregateClassification(IEnumerable<string> classifications)
    {
        var materialized = classifications.ToArray();
        if (materialized.Any(static classification => classification == MigrationCostPerformanceClassifications.Fail))
        {
            return MigrationCostPerformanceClassifications.Fail;
        }

        if (materialized.Any(static classification => classification == MigrationCostPerformanceClassifications.Warn))
        {
            return MigrationCostPerformanceClassifications.Warn;
        }

        return MigrationCostPerformanceClassifications.Pass;
    }

    private static string BuildSummary(
        string overallClassification,
        IReadOnlyCollection<MigrationCostPerformancePhaseEvidence> phases,
        IReadOnlyCollection<MigrationCostPerformanceRecoveryEvidence> recovery)
    {
        var failCount = phases.Count(static phase => phase.Classification == MigrationCostPerformanceClassifications.Fail);
        var warnCount = phases.Count(static phase => phase.Classification == MigrationCostPerformanceClassifications.Warn);
        var failedRecoveryCount = recovery.Count(static scenario => scenario.Classification == MigrationCostPerformanceClassifications.Fail);
        var warningRecoveryCount = recovery.Count(static scenario => scenario.Classification == MigrationCostPerformanceClassifications.Warn);

        return overallClassification switch
        {
            MigrationCostPerformanceClassifications.Fail => $"Migration cost/performance evidence has {failCount} failed phase(s), {warnCount} warning phase(s), {failedRecoveryCount} failed recovery scenario(s), and {warningRecoveryCount} recovery warning(s).",
            MigrationCostPerformanceClassifications.Warn => $"Migration cost/performance evidence has {warnCount} phase(s) and {warningRecoveryCount} recovery scenario(s) requiring review.",
            _ => "Migration cost/performance evidence is within the configured baseline."
        };
    }

    private static MigrationCostPerformanceRecoveryEvidence[] BuildRecovery(
        IReadOnlyList<MigrationCostPerformanceRecoveryMeasurement> measurements)
    {
        var grouped = measurements
            .GroupBy(static measurement => SafeIdentifier(measurement.Scenario, "unknown"), StringComparer.Ordinal)
            .ToDictionary(
                static group => group.Key,
                static group => group.ToArray(),
                StringComparer.Ordinal);
        var orderedScenarios = RequiredRecoveryScenarios
            .Concat(grouped.Keys.Except(RequiredRecoveryScenarios, StringComparer.Ordinal).Order(StringComparer.Ordinal))
            .ToArray();

        return orderedScenarios
            .Select(scenario => grouped.TryGetValue(scenario, out var scenarioMeasurements)
                ? BuildRecoveryEvidence(scenario, scenarioMeasurements)
                : new MigrationCostPerformanceRecoveryEvidence
                {
                    Scenario = scenario,
                    Classification = MigrationCostPerformanceClassifications.Warn,
                    Summary = $"{scenario} recovery evidence was not submitted."
                })
            .ToArray();
    }

    private static MigrationCostPerformanceRecoveryEvidence BuildRecoveryEvidence(
        string scenario,
        MigrationCostPerformanceRecoveryMeasurement[] measurements)
    {
        var classification = AggregateClassification(measurements.Select(measurement => ClassifyRecovery(scenario, measurement)));
        return new MigrationCostPerformanceRecoveryEvidence
        {
            Scenario = scenario,
            Classification = classification,
            Recovered = AggregateBool(measurements.Select(static measurement => measurement.Recovered)),
            ResumeObserved = AggregateBool(measurements.Select(static measurement => measurement.ResumeObserved)),
            IdempotentReplay = AggregateBool(measurements.Select(static measurement => measurement.IdempotentReplay)),
            AttemptCount = AggregateAttemptCount(measurements.Select(static measurement => measurement.AttemptCount)),
            Summary = BuildRecoverySummary(scenario, classification, measurements.Length)
        };
    }

    private static string ClassifyRecovery(
        string scenario,
        MigrationCostPerformanceRecoveryMeasurement measurement)
    {
        if (scenario == MigrationCostPerformanceRecoveryScenarios.RepeatedApplyAttempt)
        {
            if (measurement.AttemptCount < 0 || measurement.IdempotentReplay == false)
            {
                return MigrationCostPerformanceClassifications.Fail;
            }

            return measurement.IdempotentReplay == true
                ? MigrationCostPerformanceClassifications.Pass
                : MigrationCostPerformanceClassifications.Warn;
        }

        if (measurement.AttemptCount < 0 ||
            measurement.Recovered == false ||
            measurement.ResumeObserved == false ||
            measurement.IdempotentReplay == false)
        {
            return MigrationCostPerformanceClassifications.Fail;
        }

        if (measurement.Recovered != true)
        {
            return MigrationCostPerformanceClassifications.Warn;
        }

        return measurement.ResumeObserved == true
            ? MigrationCostPerformanceClassifications.Pass
            : MigrationCostPerformanceClassifications.Warn;
    }

    private static bool? AggregateBool(IEnumerable<bool?> values)
    {
        var materialized = values.ToArray();
        if (materialized.Any(static value => value == false))
        {
            return false;
        }

        if (materialized.Any(static value => value == true))
        {
            return true;
        }

        return null;
    }

    private static int? AggregateAttemptCount(IEnumerable<int?> values)
    {
        var materialized = values.Where(static value => value.HasValue).Select(static value => value.GetValueOrDefault()).ToArray();
        if (materialized.Length == 0)
        {
            return null;
        }

        var negativeAttemptCount = materialized.Where(static value => value < 0).Order().FirstOrDefault();
        return negativeAttemptCount < 0
            ? negativeAttemptCount
            : materialized.Sum();
    }

    private static string BuildRecoverySummary(string scenario, string classification, int measurementCount)
    {
        var summary = classification switch
        {
            MigrationCostPerformanceClassifications.Fail => $"{scenario} recovery evidence failed.",
            MigrationCostPerformanceClassifications.Warn => $"{scenario} recovery evidence is incomplete.",
            _ => $"{scenario} recovery evidence passed."
        };

        return measurementCount > 1
            ? $"{summary} {measurementCount} recovery measurements were aggregated."
            : summary;
    }

    private static string NormalizePhase(string phase)
    {
        var normalized = SafeIdentifier(phase, "unknown");

        return normalized switch
        {
            MigrationCostPerformancePhases.Scan => MigrationCostPerformancePhases.Scan,
            MigrationCostPerformancePhases.Manifest => MigrationCostPerformancePhases.Manifest,
            MigrationCostPerformancePhases.Apply => MigrationCostPerformancePhases.Apply,
            MigrationCostPerformancePhases.Import => MigrationCostPerformancePhases.Import,
            _ => normalized
        };
    }

    private static string SafeText(string? value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        var trimmed = value.Trim();
        if (ContainsUrl(trimmed))
        {
            return "[redacted-url]";
        }

        if (ContainsSensitiveMarker(trimmed))
        {
            return "[redacted]";
        }

        return trimmed.Length <= 160 ? trimmed : trimmed[..160];
    }

    private static string? SafeNullableText(string? value)
    {
        var safe = SafeText(value, string.Empty);
        return safe.Length == 0 ? null : safe;
    }

    private static string SafeIdentifier(string? value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value) || ContainsUrl(value) || ContainsSensitiveMarker(value))
        {
            return fallback;
        }

        var builder = new StringBuilder(value.Length);
        var previousWasSeparator = false;
        foreach (var character in value.Trim())
        {
            if (char.IsLetterOrDigit(character) || character is '-' or '_' or '.' or ':')
            {
                builder.Append(char.ToLowerInvariant(character));
                previousWasSeparator = false;
                continue;
            }

            if (!previousWasSeparator)
            {
                builder.Append('-');
                previousWasSeparator = true;
            }
        }

        var normalized = builder.ToString().Trim('-');
        return normalized.Length == 0 ? fallback : normalized;
    }

    private static bool ContainsUrl(string value)
        => value.Contains("://", StringComparison.Ordinal) ||
            value.Contains('@') ||
            value.Contains("?token=", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("?password=", StringComparison.OrdinalIgnoreCase);

    private static bool ContainsSensitiveMarker(string value)
        => value.Contains("password", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("token=", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("api_key", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("apikey", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("authorization", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("x-amz-signature", StringComparison.OrdinalIgnoreCase);

    private static long? SumLong(IEnumerable<MigrationCostPerformanceMetrics> metrics, Func<MigrationCostPerformanceMetrics, long?> selector)
    {
        long sum = 0;
        var hasValue = false;
        foreach (var metric in metrics)
        {
            var value = selector(metric);
            if (value == null)
            {
                continue;
            }

            sum += value.Value;
            hasValue = true;
        }

        return hasValue ? sum : null;
    }

    private static int? SumInt(IEnumerable<MigrationCostPerformanceMetrics> metrics, Func<MigrationCostPerformanceMetrics, int?> selector)
    {
        var sum = 0;
        var hasValue = false;
        foreach (var metric in metrics)
        {
            var value = selector(metric);
            if (value == null)
            {
                continue;
            }

            sum += value.Value;
            hasValue = true;
        }

        return hasValue ? sum : null;
    }

    private static long? MaxLong(IEnumerable<MigrationCostPerformanceMetrics> metrics, Func<MigrationCostPerformanceMetrics, long?> selector)
    {
        long? max = null;
        foreach (var metric in metrics)
        {
            var value = selector(metric);
            if (value != null && (max == null || value > max))
            {
                max = value;
            }
        }

        return max;
    }

    private static double? MaxDouble(IEnumerable<MigrationCostPerformanceMetrics> metrics, Func<MigrationCostPerformanceMetrics, double?> selector)
    {
        double? max = null;
        foreach (var metric in metrics)
        {
            var value = selector(metric);
            if (value != null && (max == null || value > max))
            {
                max = value;
            }
        }

        return max;
    }

    private static double? SumDouble(IEnumerable<MigrationCostPerformanceMetrics> metrics, Func<MigrationCostPerformanceMetrics, double?> selector)
    {
        double sum = 0;
        var hasValue = false;
        foreach (var metric in metrics)
        {
            var value = selector(metric);
            if (value == null)
            {
                continue;
            }

            sum += value.Value;
            hasValue = true;
        }

        return hasValue ? sum : null;
    }

    private static double? ComputeManualReviewRatio(int? manualReviewCount, int? candidateItemCount)
    {
        if (manualReviewCount == null || candidateItemCount == null)
        {
            return null;
        }

        if (candidateItemCount <= 0)
        {
            return manualReviewCount > 0 ? 1 : 0;
        }

        return (double)manualReviewCount.Value / candidateItemCount.Value;
    }

    private static double? ComputeThroughput(long? count, long? durationMilliseconds)
    {
        if (count == null || durationMilliseconds == null || durationMilliseconds <= 0)
        {
            return null;
        }

        return count.Value / (durationMilliseconds.Value / 1000d);
    }
}
