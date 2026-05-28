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
/// Builds release-gate migration acceptance evidence from source-specific artifacts.
/// </summary>
public static class MigrationAcceptanceEvidenceBuilder
{
    /// <summary>
    /// Builds a deterministic acceptance-suite artifact from source inventory, manifest, and parity evidence inputs.
    /// </summary>
    /// <param name="runId">Stable workflow or release run identifier.</param>
    /// <param name="inputs">Per-source evidence inputs.</param>
    /// <param name="options">Optional suite gate options.</param>
    /// <returns>Migration acceptance evidence suite artifact.</returns>
    public static MigrationAcceptanceEvidenceArtifact Build(
        string runId,
        IEnumerable<MigrationAcceptanceEvidenceInput> inputs,
        MigrationAcceptanceEvidenceOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentNullException.ThrowIfNull(inputs);

        options ??= new MigrationAcceptanceEvidenceOptions();

        var entries = inputs
            .Select(input => BuildEntry(input, options))
            .OrderBy(static entry => entry.SourceKind, StringComparer.Ordinal)
            .ThenBy(static entry => entry.Id, StringComparer.Ordinal)
            .ToArray();
        var gaps = BuildBlockingGaps(entries, options).ToArray();
        var summary = BuildSummary(entries, gaps, options);
        var costEvidence = BuildCostEvidenceSummary(entries, options);

        return new MigrationAcceptanceEvidenceArtifact
        {
            RunId = runId.Trim(),
            Summary = summary,
            CostEvidence = costEvidence,
            Entries = entries,
            BlockingGaps = gaps
        };
    }

    private static MigrationAcceptanceEvidenceEntry BuildEntry(
        MigrationAcceptanceEvidenceInput input,
        MigrationAcceptanceEvidenceOptions options)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(input.Inventory);

        var manifest = input.Manifest ?? MigrationManifestTranslator.Translate(input.Inventory);
        var parityEvidence = input.ParityEvidence ?? MigrationParityEvidenceGenerator.Generate(
            input.Inventory,
            manifest,
            input.ReadinessAttestation,
            input.PerformanceCost);
        ValidateEvidenceIdentity(input.Inventory, manifest, parityEvidence);

        var stages = BuildStages(
            input,
            manifest,
            parityEvidence,
            suppliedManifest: input.Manifest != null,
            suppliedParityEvidence: input.ParityEvidence != null);
        var manualReviewCount = manifest.ManualReviewItems.Length + manifest.StyleActions.Count(static action =>
            string.Equals(action.Action, "manual-review", StringComparison.OrdinalIgnoreCase));
        var unsupportedCount = manifest.UnsupportedItems.Length;
        var costEvidence = BuildCostEvidenceEntry(
            parityEvidence.PerformanceCost,
            manifest,
            manualReviewCount,
            options);

        return new MigrationAcceptanceEvidenceEntry
        {
            Id = string.IsNullOrWhiteSpace(input.Id) ? input.Inventory.SourceKind : input.Id.Trim(),
            SourceKind = input.Inventory.SourceKind,
            Source = input.Inventory.Source,
            State = AggregateState(stages.Select(static stage => stage.State)),
            AutomationLevel = ClassifyAutomation(manifest, parityEvidence, manualReviewCount, unsupportedCount),
            Stages = stages,
            ManualReviewCount = manualReviewCount,
            UnsupportedCount = unsupportedCount,
            ManifestAvailable = true,
            InventoryArtifactKind = input.Inventory.ArtifactKind,
            ManifestArtifactKind = manifest.ArtifactKind,
            ParityEvidenceArtifactKind = parityEvidence.ArtifactKind,
            EvidenceReferences = SanitizeEvidenceReferences(input.EvidenceReferences),
            CostEvidence = costEvidence,
            Notes = BuildEntryNotes(manifest, parityEvidence, options)
        };
    }

    private static void ValidateEvidenceIdentity(
        MigrationSourceInventoryArtifact inventory,
        MigrationManifestArtifact manifest,
        MigrationParityEvidenceArtifact parityEvidence)
    {
        if (!StringEquals(inventory.SourceKind, manifest.SourceKind))
        {
            throw new ArgumentException("Migration manifest source kind must match inventory source kind.");
        }

        if (!SourceIdentityMatches(inventory.Source, manifest.Source))
        {
            throw new ArgumentException("Migration manifest source identity must match inventory source identity.");
        }

        if (!StringEquals(inventory.SourceKind, parityEvidence.SourceKind))
        {
            throw new ArgumentException("Migration parity evidence source kind must match inventory source kind.");
        }

        if (!SourceIdentityMatches(inventory.Source, parityEvidence.Source))
        {
            throw new ArgumentException("Migration parity evidence source identity must match inventory source identity.");
        }
    }

    private static bool SourceIdentityMatches(MigrationSourceIdentity left, MigrationSourceIdentity right)
        => StringEquals(left.DisplayName, right.DisplayName) &&
           StringEquals(left.BaseUrl, right.BaseUrl);

    private static bool StringEquals(string? left, string? right)
        => string.Equals(left?.Trim(), right?.Trim(), StringComparison.Ordinal);

    private static IEnumerable<MigrationAcceptanceEvidenceGap> BuildBlockingGaps(
        IReadOnlyCollection<MigrationAcceptanceEvidenceEntry> entries,
        MigrationAcceptanceEvidenceOptions options)
    {
        var coveredSourceKinds = entries
            .Select(static entry => entry.SourceKind)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var sourceKind in Order(options.RequiredSourceKinds))
        {
            if (!coveredSourceKinds.Contains(sourceKind))
            {
                yield return new MigrationAcceptanceEvidenceGap
                {
                    Id = $"missing-source-kind:{sourceKind}",
                    SourceKind = sourceKind,
                    State = MigrationEvidenceStates.Fail,
                    Summary = $"Required migration source kind '{sourceKind}' has no evidence entry.",
                    Remediation = ["Add a deterministic fixture or scheduled evidence lane for this source kind."]
                };
            }
        }

        if (options.RequireAutomatedEntries)
        {
            foreach (var entry in entries.Where(static entry =>
                         entry.AutomationLevel != MigrationAutomationLevels.Automated))
            {
                yield return new MigrationAcceptanceEvidenceGap
                {
                    Id = $"automation-level:{entry.Id}",
                    SourceKind = entry.SourceKind,
                    State = entry.AutomationLevel == MigrationAutomationLevels.Unsupported
                        ? MigrationEvidenceStates.Fail
                        : MigrationEvidenceStates.Unknown,
                    Summary = $"{entry.Id} is classified as {entry.AutomationLevel}.",
                    Remediation = ["Close unsupported items or record an explicit assisted-migration claim instead of automated migration."]
                };
            }
        }

        foreach (var entry in entries)
        {
            if (options.RequireCostEvidence)
            {
                var costState = entry.CostEvidence?.State ?? MigrationCostEvidenceStates.Fail;
                if (costState != MigrationCostEvidenceStates.Pass)
                {
                    yield return new MigrationAcceptanceEvidenceGap
                    {
                        Id = $"cost-evidence:{entry.Id}",
                        SourceKind = entry.SourceKind,
                        State = costState == MigrationCostEvidenceStates.Fail
                            ? MigrationEvidenceStates.Fail
                            : MigrationEvidenceStates.Unknown,
                        Summary = costState == MigrationCostEvidenceStates.Fail
                            ? $"{entry.Id} cost evidence failed the configured release contract."
                            : $"{entry.Id} cost evidence is {costState}.",
                        Remediation = entry.CostEvidence?.Findings
                            .SelectMany(static finding => finding.Remediation)
                            .DefaultIfEmpty("Attach passing performance and cost evidence before using migration cost claims.")
                            .ToArray() ??
                            ["Attach passing performance and cost evidence before using migration cost claims."]
                    };
                }
            }

            foreach (var stage in entry.Stages.Where(static stage =>
                         stage.State is MigrationEvidenceStates.Fail or MigrationEvidenceStates.Unknown))
            {
                yield return new MigrationAcceptanceEvidenceGap
                {
                    Id = $"stage:{entry.Id}:{stage.Id}",
                    SourceKind = entry.SourceKind,
                    State = stage.State,
                    Summary = $"{entry.Id} {stage.Id} stage is {stage.State}.",
                    Remediation = stage.Id switch
                    {
                        MigrationAcceptanceStageIds.ApplyOrDryRun =>
                            ["Attach apply or dry-run evidence before claiming end-to-end automated migration."],
                        MigrationAcceptanceStageIds.Publish =>
                            ["Attach publish evidence before claiming published target parity."],
                        MigrationAcceptanceStageIds.Readiness =>
                            ["Complete or waive cutover-readiness attestations before release evidence is accepted."],
                        _ => ["Attach passing evidence for this stage or classify the migration as assisted/manual."]
                    }
                };
            }
        }
    }

    private static MigrationAcceptanceEvidenceSummary BuildSummary(
        MigrationAcceptanceEvidenceEntry[] entries,
        MigrationAcceptanceEvidenceGap[] gaps,
        MigrationAcceptanceEvidenceOptions options)
    {
        var states = entries.Select(static entry => entry.State).Concat(gaps.Select(static gap => gap.State));
        var overallState = AggregateState(states);

        return new MigrationAcceptanceEvidenceSummary
        {
            OverallState = overallState,
            SourceCount = entries.Length,
            PassingSourceCount = entries.Count(static entry => entry.State == MigrationEvidenceStates.Pass),
            FailingSourceCount = entries.Count(static entry => entry.State == MigrationEvidenceStates.Fail),
            UnknownSourceCount = entries.Count(static entry => entry.State == MigrationEvidenceStates.Unknown),
            AutomatedSourceCount = entries.Count(static entry => entry.AutomationLevel == MigrationAutomationLevels.Automated),
            ManualReviewSourceCount = entries.Count(static entry =>
                entry.AutomationLevel is MigrationAutomationLevels.Assisted or MigrationAutomationLevels.ManualReview),
            UnsupportedSourceCount = entries.Count(static entry => entry.AutomationLevel == MigrationAutomationLevels.Unsupported),
            RequiredSourceKinds = Order(options.RequiredSourceKinds),
            CoveredSourceKinds = entries
                .Select(static entry => entry.SourceKind)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(static sourceKind => sourceKind, StringComparer.Ordinal)
                .ToArray()
        };
    }

    private static MigrationAcceptanceCostEvidenceSummary BuildCostEvidenceSummary(
        MigrationAcceptanceEvidenceEntry[] entries,
        MigrationAcceptanceEvidenceOptions options)
    {
        var measured = entries
            .Select(static entry => entry.CostEvidence)
            .OfType<MigrationAcceptanceCostEvidenceEntry>()
            .ToArray();
        var missingSourceCount = entries.Length - measured.Length;
        var states = measured.Select(static entry => entry.State);
        if (missingSourceCount > 0)
        {
            states = states.Append(options.RequireCostEvidence
                ? MigrationCostEvidenceStates.Fail
                : MigrationCostEvidenceStates.Unknown);
        }

        return new MigrationAcceptanceCostEvidenceSummary
        {
            State = AggregateCostState(states),
            MeasuredSourceCount = measured.Length,
            MissingSourceCount = missingSourceCount,
            DurationMilliseconds = SumNullable(measured.Select(static entry => entry.DurationMilliseconds)),
            ScanDurationMilliseconds = SumNullable(measured.Select(static entry => entry.ScanDurationMilliseconds)),
            ApplyDurationMilliseconds = SumNullable(measured.Select(static entry => entry.ApplyDurationMilliseconds)),
            FeatureThroughputPerSecond = AverageNullable(measured.Select(static entry => entry.FeatureThroughputPerSecond)),
            SourceRequestCount = SumNullable(measured.Select(static entry => entry.SourceRequestCount)),
            RetryCount = SumNullable(measured.Select(static entry => entry.RetryCount)),
            BytesRead = SumNullable(measured.Select(static entry => entry.BytesRead)),
            BytesWritten = SumNullable(measured.Select(static entry => entry.BytesWritten)),
            ArtifactSizeBytes = SumNullable(measured.Select(static entry => entry.ArtifactSizeBytes)),
            ManualReviewRatio = AverageNullable(measured.Select(static entry => entry.ManualReviewRatio)),
            Findings = measured
                .SelectMany(static entry => entry.Findings)
                .OrderBy(static finding => finding.Id, StringComparer.Ordinal)
                .ToArray()
        };
    }

    private static MigrationAcceptanceCostEvidenceEntry? BuildCostEvidenceEntry(
        MigrationPerformanceCostEvidence? performanceCost,
        MigrationManifestArtifact manifest,
        int manifestManualReviewCount,
        MigrationAcceptanceEvidenceOptions options)
    {
        var normalized = MigrationParityEvidenceGenerator.NormalizePerformanceCost(performanceCost);
        if (normalized == null)
        {
            return null;
        }

        var operations = normalized.Operations
            .Select(static operation => new MigrationAcceptanceCostOperationEvidence
            {
                Id = operation.Id,
                Stage = operation.Stage,
                State = operation.State,
                DurationMilliseconds = operation.DurationMilliseconds,
                FeatureThroughputPerSecond = CalculateThroughput(
                    operation.FeatureCount,
                    operation.DurationMilliseconds),
                SourceRequestCount = operation.SourceRequestCount,
                RetryCount = operation.RetryCount,
                BytesRead = operation.BytesRead,
                BytesWritten = operation.BytesWritten,
                ArtifactSizeBytes = operation.ArtifactSizeBytes,
                EvidenceReferences = SanitizeEvidenceReferences(operation.EvidenceReferences)
            })
            .OrderBy(static operation => operation.Id, StringComparer.Ordinal)
            .ThenBy(static operation => operation.Stage, StringComparer.Ordinal)
            .ToArray();
        var totals = normalized.Totals;
        var durationMilliseconds = totals.DurationMilliseconds ??
            SumNullable(operations.Select(static operation => operation.DurationMilliseconds));
        var scanDurationMilliseconds = SumNullable(operations
            .Where(static operation => IsScanStage(operation.Stage))
            .Select(static operation => operation.DurationMilliseconds));
        var applyDurationMilliseconds = SumNullable(operations
            .Where(static operation => IsApplyStage(operation.Stage))
            .Select(static operation => operation.DurationMilliseconds));
        var sourceRequestCount = totals.SourceRequestCount ??
            SumNullable(operations.Select(static operation => operation.SourceRequestCount));
        var retryCount = totals.RetryCount ??
            SumNullable(operations.Select(static operation => operation.RetryCount));
        var bytesRead = totals.BytesRead ??
            SumNullable(operations.Select(static operation => operation.BytesRead));
        var bytesWritten = totals.BytesWritten ??
            SumNullable(operations.Select(static operation => operation.BytesWritten));
        var artifactSizeBytes = totals.ArtifactSizeBytes ??
            SumNullable(operations.Select(static operation => operation.ArtifactSizeBytes));
        var manualReviewCount = totals.ManualReviewCount ?? manifestManualReviewCount;
        var resourceCount = totals.ResourceCount ??
            manifest.TargetResources.Length + manifest.ManualReviewItems.Length + manifest.UnsupportedItems.Length;
        var manualReviewRatio = CalculateRatio(manualReviewCount, resourceCount);
        var featureThroughput = CalculateThroughput(totals.FeatureCount, durationMilliseconds) ??
            AverageNullable(operations.Select(static operation => operation.FeatureThroughputPerSecond));
        var resourceThroughput = CalculateThroughput(resourceCount, durationMilliseconds);

        var findings = BuildCostEvidenceFindings(
                normalized,
                options.CostEvidenceThresholds,
                durationMilliseconds,
                scanDurationMilliseconds,
                applyDurationMilliseconds,
                featureThroughput,
                sourceRequestCount,
                retryCount,
                bytesRead,
                bytesWritten,
                artifactSizeBytes,
                manualReviewRatio,
                operations)
            .OrderBy(static finding => finding.Id, StringComparer.Ordinal)
            .ToArray();
        var state = AggregateCostState(
            [NormalizeCostState(normalized.State), .. findings.Select(static finding => finding.State)]);

        return new MigrationAcceptanceCostEvidenceEntry
        {
            State = state,
            Summary = normalized.Summary,
            MeasurementScope = normalized.MeasurementScope,
            DurationMilliseconds = durationMilliseconds,
            ScanDurationMilliseconds = scanDurationMilliseconds,
            ApplyDurationMilliseconds = applyDurationMilliseconds,
            FeatureThroughputPerSecond = featureThroughput,
            ResourceThroughputPerSecond = resourceThroughput,
            SourceRequestCount = sourceRequestCount,
            RetryCount = retryCount,
            BytesRead = bytesRead,
            BytesWritten = bytesWritten,
            ArtifactSizeBytes = artifactSizeBytes,
            ManualReviewRatio = manualReviewRatio,
            Operations = operations,
            EvidenceReferences = SanitizeEvidenceReferences(normalized.EvidenceReferences),
            Findings = findings
        };
    }

    private static IEnumerable<MigrationAcceptanceCostEvidenceFinding> BuildCostEvidenceFindings(
        MigrationPerformanceCostEvidence performanceCost,
        MigrationAcceptanceCostEvidenceThresholds thresholds,
        long? durationMilliseconds,
        long? scanDurationMilliseconds,
        long? applyDurationMilliseconds,
        double? featureThroughput,
        int? sourceRequestCount,
        int? retryCount,
        long? bytesRead,
        long? bytesWritten,
        long? artifactSizeBytes,
        double? manualReviewRatio,
        IReadOnlyCollection<MigrationAcceptanceCostOperationEvidence> operations)
    {
        foreach (var operation in performanceCost.Operations.Where(static operation =>
                     NormalizeCostState(operation.State) == MigrationCostEvidenceStates.Fail))
        {
            yield return new MigrationAcceptanceCostEvidenceFinding
            {
                Id = $"operation-failed:{operation.Id}",
                State = MigrationCostEvidenceStates.Fail,
                Summary = $"{operation.Id} cost operation reported failed evidence.",
                Remediation = ["Inspect the source operation evidence and rerun the measured migration lane."]
            };
        }

        if (thresholds.RequireMetricContract)
        {
            if (durationMilliseconds == null)
            {
                yield return MissingMetric("duration-milliseconds", "Total measured duration is required.");
            }

            if (scanDurationMilliseconds == null)
            {
                yield return MissingMetric("scan-duration-milliseconds", "Measured scan duration is required.");
            }

            if (applyDurationMilliseconds == null)
            {
                yield return MissingMetric("apply-duration-milliseconds", "Measured apply or dry-run duration is required.");
            }

            if (featureThroughput == null)
            {
                yield return MissingMetric("feature-throughput-per-second", "Feature throughput is required.");
            }

            if (sourceRequestCount == null)
            {
                yield return MissingMetric("source-request-count", "Source request count is required.");
            }

            if (retryCount == null)
            {
                yield return MissingMetric("retry-count", "Retry count is required.");
            }

            if (bytesRead == null || bytesWritten == null)
            {
                yield return MissingMetric("bytes", "Bytes read and written are required.");
            }

            if (artifactSizeBytes == null)
            {
                yield return MissingMetric("artifact-size-bytes", "Cost evidence artifact size is required.");
            }

            if (manualReviewRatio == null)
            {
                yield return MissingMetric("manual-review-ratio", "Manual-review ratio is required.");
            }
        }

        foreach (var requiredStage in Order(thresholds.RequiredDurationStages))
        {
            if (!operations.Any(operation =>
                    StageMatches(operation.Stage, requiredStage) &&
                    operation.DurationMilliseconds is > 0))
            {
                yield return MissingMetric(
                    $"stage-duration:{requiredStage}",
                    $"Measured {requiredStage} duration is required.");
            }
        }

        foreach (var finding in EvaluateUpperLimit(
                     "duration-milliseconds",
                     durationMilliseconds,
                     thresholds.MaxDurationMillisecondsWarn,
                     thresholds.MaxDurationMillisecondsFail,
                     "Total measured duration exceeded the configured threshold."))
        {
            yield return finding;
        }

        foreach (var finding in EvaluateLowerLimit(
                     "feature-throughput-per-second",
                     featureThroughput,
                     thresholds.MinFeatureThroughputPerSecondWarn,
                     thresholds.MinFeatureThroughputPerSecondFail,
                     "Feature throughput fell below the configured threshold."))
        {
            yield return finding;
        }

        foreach (var finding in EvaluateUpperLimit(
                     "source-request-count",
                     sourceRequestCount,
                     thresholds.MaxSourceRequestCountWarn,
                     thresholds.MaxSourceRequestCountFail,
                     "Source request count exceeded the configured threshold."))
        {
            yield return finding;
        }

        foreach (var finding in EvaluateUpperLimit(
                     "retry-count",
                     retryCount,
                     thresholds.MaxRetryCountWarn,
                     thresholds.MaxRetryCountFail,
                     "Retry count exceeded the configured threshold."))
        {
            yield return finding;
        }

        foreach (var finding in EvaluateUpperLimit(
                     "artifact-size-bytes",
                     artifactSizeBytes,
                     thresholds.MaxArtifactSizeBytesWarn,
                     thresholds.MaxArtifactSizeBytesFail,
                     "Evidence artifact size exceeded the configured threshold."))
        {
            yield return finding;
        }

        foreach (var finding in EvaluateUpperLimit(
                     "manual-review-ratio",
                     manualReviewRatio,
                     thresholds.MaxManualReviewRatioWarn,
                     thresholds.MaxManualReviewRatioFail,
                     "Manual-review ratio exceeded the configured threshold."))
        {
            yield return finding;
        }
    }

    private static MigrationAcceptanceCostEvidenceFinding MissingMetric(string id, string summary)
        => new()
        {
            Id = $"missing-metric:{id}",
            State = MigrationCostEvidenceStates.Fail,
            Summary = summary,
            Remediation = ["Update the migration evidence collector to emit this metric before accepting cost claims."]
        };

    private static IEnumerable<MigrationAcceptanceCostEvidenceFinding> EvaluateUpperLimit(
        string id,
        long? value,
        long? warnThreshold,
        long? failThreshold,
        string summary)
    {
        if (value == null)
        {
            yield break;
        }

        foreach (var finding in EvaluateUpperLimit(id, (double)value.Value, warnThreshold, failThreshold, summary))
        {
            yield return finding;
        }
    }

    private static IEnumerable<MigrationAcceptanceCostEvidenceFinding> EvaluateUpperLimit(
        string id,
        int? value,
        int? warnThreshold,
        int? failThreshold,
        string summary)
    {
        if (value == null)
        {
            yield break;
        }

        foreach (var finding in EvaluateUpperLimit(id, (double)value.Value, warnThreshold, failThreshold, summary))
        {
            yield return finding;
        }
    }

    private static IEnumerable<MigrationAcceptanceCostEvidenceFinding> EvaluateUpperLimit(
        string id,
        double? value,
        double? warnThreshold,
        double? failThreshold,
        string summary)
    {
        if (value == null)
        {
            yield break;
        }

        if (failThreshold != null && value.Value > failThreshold.Value)
        {
            yield return ThresholdFinding(id, MigrationCostEvidenceStates.Fail, summary, value.Value, failThreshold.Value);
            yield break;
        }

        if (warnThreshold != null && value.Value > warnThreshold.Value)
        {
            yield return ThresholdFinding(id, MigrationCostEvidenceStates.Warn, summary, value.Value, warnThreshold.Value);
        }
    }

    private static IEnumerable<MigrationAcceptanceCostEvidenceFinding> EvaluateLowerLimit(
        string id,
        double? value,
        double? warnThreshold,
        double? failThreshold,
        string summary)
    {
        if (value == null)
        {
            yield break;
        }

        if (failThreshold != null && value.Value < failThreshold.Value)
        {
            yield return ThresholdFinding(id, MigrationCostEvidenceStates.Fail, summary, value.Value, failThreshold.Value);
            yield break;
        }

        if (warnThreshold != null && value.Value < warnThreshold.Value)
        {
            yield return ThresholdFinding(id, MigrationCostEvidenceStates.Warn, summary, value.Value, warnThreshold.Value);
        }
    }

    private static MigrationAcceptanceCostEvidenceFinding ThresholdFinding(
        string id,
        string state,
        string summary,
        double value,
        double threshold)
        => new()
        {
            Id = $"threshold:{id}",
            State = state,
            Summary = $"{summary} value={value:0.###}, threshold={threshold:0.###}.",
            Remediation = ["Review the fixture, collector, and threshold before publishing migration cost evidence."]
        };

    private static string AggregateCostState(IEnumerable<string> states)
    {
        var materialized = states.Select(NormalizeCostState).ToArray();
        if (materialized.Any(static state => state == MigrationCostEvidenceStates.Fail))
        {
            return MigrationCostEvidenceStates.Fail;
        }

        if (materialized.Any(static state => state == MigrationCostEvidenceStates.Warn))
        {
            return MigrationCostEvidenceStates.Warn;
        }

        if (materialized.Length == 0 || materialized.Any(static state => state == MigrationCostEvidenceStates.Unknown))
        {
            return MigrationCostEvidenceStates.Unknown;
        }

        return MigrationCostEvidenceStates.Pass;
    }

    private static string NormalizeCostState(string state)
        => state switch
        {
            MigrationCostEvidenceStates.Pass => MigrationCostEvidenceStates.Pass,
            MigrationCostEvidenceStates.Warn => MigrationCostEvidenceStates.Warn,
            MigrationCostEvidenceStates.Fail => MigrationCostEvidenceStates.Fail,
            MigrationCostEvidenceStates.Unknown => MigrationCostEvidenceStates.Unknown,
            _ => MigrationCostEvidenceStates.Unknown
        };

    private static double? CalculateThroughput(long? count, long? durationMilliseconds)
    {
        if (count is null || durationMilliseconds is null or <= 0)
        {
            return null;
        }

        return count.Value / (durationMilliseconds.Value / 1000d);
    }

    private static double? CalculateRatio(long? numerator, long? denominator)
    {
        if (numerator is null || denominator is null or <= 0)
        {
            return null;
        }

        return numerator.Value / (double)denominator.Value;
    }

    private static bool StageMatches(string actualStage, string requiredStage)
        => string.Equals(actualStage, requiredStage, StringComparison.Ordinal) ||
            (string.Equals(requiredStage, MigrationAcceptanceStageIds.ApplyOrDryRun, StringComparison.Ordinal) &&
                IsApplyStage(actualStage)) ||
            (string.Equals(requiredStage, MigrationAcceptanceStageIds.Scan, StringComparison.Ordinal) &&
                IsScanStage(actualStage));

    private static bool IsScanStage(string stage)
        => string.Equals(stage, MigrationAcceptanceStageIds.Scan, StringComparison.OrdinalIgnoreCase);

    private static bool IsApplyStage(string stage)
        => string.Equals(stage, MigrationAcceptanceStageIds.ApplyOrDryRun, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(stage, "apply", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(stage, "import", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(stage, "dry-run", StringComparison.OrdinalIgnoreCase);

    private static long? SumNullable(IEnumerable<long?> values)
    {
        var materialized = values.Where(static value => value.HasValue).Select(static value => value!.Value).ToArray();
        return materialized.Length == 0 ? null : materialized.Sum();
    }

    private static int? SumNullable(IEnumerable<int?> values)
    {
        var materialized = values.Where(static value => value.HasValue).Select(static value => value!.Value).ToArray();
        return materialized.Length == 0 ? null : materialized.Sum();
    }

    private static double? AverageNullable(IEnumerable<double?> values)
    {
        var materialized = values.Where(static value => value.HasValue).Select(static value => value!.Value).ToArray();
        return materialized.Length == 0 ? null : materialized.Average();
    }

    private static string ClassifyAutomation(
        MigrationManifestArtifact manifest,
        MigrationParityEvidenceArtifact parityEvidence,
        int manualReviewCount,
        int unsupportedCount)
    {
        if (unsupportedCount > 0 || parityEvidence.Sections.SelectMany(static section => section.Items)
                .Any(static item => item.State == MigrationEvidenceStates.Fail))
        {
            return MigrationAutomationLevels.Unsupported;
        }

        if (manualReviewCount == 0)
        {
            return MigrationAutomationLevels.Automated;
        }

        return manualReviewCount >= manifest.TargetResources.Length
            ? MigrationAutomationLevels.ManualReview
            : MigrationAutomationLevels.Assisted;
    }

    private static MigrationAcceptanceEvidenceStage[] BuildStages(
        MigrationAcceptanceEvidenceInput input,
        MigrationManifestArtifact manifest,
        MigrationParityEvidenceArtifact parityEvidence,
        bool suppliedManifest,
        bool suppliedParityEvidence)
    {
        var overrides = input.StageEvidence
            .Where(static stage => !string.IsNullOrWhiteSpace(stage.Id))
            .GroupBy(static stage => stage.Id.Trim(), StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.First(), StringComparer.Ordinal);

        return
        [
            BuildStage(
                MigrationAcceptanceStageIds.Scan,
                overrides,
                MigrationEvidenceStates.Pass,
                "Source inventory artifact is available.",
                [input.Inventory.ArtifactKind],
                []),
            BuildStage(
                MigrationAcceptanceStageIds.Manifest,
                overrides,
                MigrationEvidenceStates.Pass,
                suppliedManifest
                    ? "Migration manifest artifact was supplied."
                    : "Migration manifest artifact was generated from the inventory.",
                [manifest.ArtifactKind],
                suppliedManifest ? [] : ["Persist the generated manifest with release evidence for reviewer inspection."]),
            BuildStage(
                MigrationAcceptanceStageIds.ApplyOrDryRun,
                overrides,
                MigrationEvidenceStates.Unknown,
                "Apply or dry-run evidence was not supplied.",
                [],
                ["Run the bounded source-specific apply or dry-run lane and attach its evidence artifact."]),
            BuildStage(
                MigrationAcceptanceStageIds.Publish,
                overrides,
                MigrationEvidenceStates.Unknown,
                "Publish evidence was not supplied.",
                [],
                ["Attach target publish evidence before claiming published-service parity."]),
            BuildStage(
                MigrationAcceptanceStageIds.Parity,
                overrides,
                parityEvidence.OverallState,
                parityEvidence.Summary,
                [parityEvidence.ArtifactKind],
                suppliedParityEvidence ? [] : ["Persist the generated parity evidence pack with release evidence."]),
            BuildStage(
                MigrationAcceptanceStageIds.Readiness,
                overrides,
                parityEvidence.CutoverReadiness.State,
                $"Cutover readiness state is {parityEvidence.CutoverReadiness.State}.",
                [parityEvidence.ArtifactKind],
                [])
        ];
    }

    private static MigrationAcceptanceEvidenceStage BuildStage(
        string id,
        Dictionary<string, MigrationAcceptanceStageEvidenceInput> overrides,
        string defaultState,
        string defaultSummary,
        IEnumerable<string> defaultArtifactKinds,
        IEnumerable<string> defaultNotes)
    {
        if (overrides.TryGetValue(id, out var evidence))
        {
            return new MigrationAcceptanceEvidenceStage
            {
                Id = id,
                State = NormalizeState(evidence.State),
                Summary = string.IsNullOrWhiteSpace(evidence.Summary) ? defaultSummary : evidence.Summary.Trim(),
                ArtifactKinds = Order(evidence.ArtifactKinds),
                EvidenceReferences = SanitizeEvidenceReferences(evidence.EvidenceReferences),
                Notes = Order(evidence.Notes)
            };
        }

        return new MigrationAcceptanceEvidenceStage
        {
            Id = id,
            State = NormalizeState(defaultState),
            Summary = defaultSummary,
            ArtifactKinds = Order(defaultArtifactKinds),
            EvidenceReferences = [],
            Notes = Order(defaultNotes)
        };
    }

    private static string[] BuildEntryNotes(
        MigrationManifestArtifact manifest,
        MigrationParityEvidenceArtifact parityEvidence,
        MigrationAcceptanceEvidenceOptions options)
    {
        var notes = new List<string>();
        if (manifest.ManualReviewItems.Length > 0)
        {
            notes.Add($"{manifest.ManualReviewItems.Length} manifest item(s) require manual review.");
        }

        if (manifest.UnsupportedItems.Length > 0)
        {
            notes.Add($"{manifest.UnsupportedItems.Length} manifest item(s) are unsupported.");
        }

        if (parityEvidence.OverallState != MigrationEvidenceStates.Pass)
        {
            notes.Add($"Parity evidence state is {parityEvidence.OverallState}.");
        }

        if (options.RequireReadinessPass && parityEvidence.CutoverReadiness.State != MigrationEvidenceStates.Pass)
        {
            notes.Add($"Cutover readiness state is {parityEvidence.CutoverReadiness.State}.");
        }

        return Order(notes);
    }

    private static string AggregateState(IEnumerable<string> states)
    {
        var materialized = states.Select(NormalizeState).ToArray();
        if (materialized.Any(static state => state == MigrationEvidenceStates.Fail))
        {
            return MigrationEvidenceStates.Fail;
        }

        if (materialized.Length == 0 || materialized.Any(static state => state == MigrationEvidenceStates.Unknown))
        {
            return MigrationEvidenceStates.Unknown;
        }

        return MigrationEvidenceStates.Pass;
    }

    private static string NormalizeState(string state)
        => state switch
        {
            MigrationEvidenceStates.Pass => MigrationEvidenceStates.Pass,
            MigrationEvidenceStates.Fail => MigrationEvidenceStates.Fail,
            MigrationEvidenceStates.Unknown => MigrationEvidenceStates.Unknown,
            MigrationEvidenceStates.NotApplicable => MigrationEvidenceStates.NotApplicable,
            _ => MigrationEvidenceStates.Unknown
        };

    private static string[] SanitizeEvidenceReferences(IEnumerable<string> values)
        => values
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => SanitizeEvidenceReference(value.Trim()))
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static value => value, StringComparer.Ordinal)
            .ToArray();

    private static string SanitizeEvidenceReference(string value)
    {
        var sensitiveTailIndex = value.IndexOfAny(['?', '#']);
        var withoutQueryOrFragment = sensitiveTailIndex >= 0 ? value[..sensitiveTailIndex] : value;

        if (!Uri.TryCreate(withoutQueryOrFragment, UriKind.Absolute, out var uri) ||
            string.IsNullOrEmpty(uri.UserInfo))
        {
            return withoutQueryOrFragment;
        }

        var builder = new UriBuilder(uri)
        {
            UserName = string.Empty,
            Password = string.Empty
        };

        return builder.Uri.AbsoluteUri;
    }

    private static string[] Order(IEnumerable<string> values)
        => values
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => value.Trim())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static value => value, StringComparer.Ordinal)
            .ToArray();
}

/// <summary>
/// Per-source inputs used to build an acceptance-suite evidence entry.
/// </summary>
public sealed record MigrationAcceptanceEvidenceInput
{
    /// <summary>
    /// Stable source evidence entry identifier.
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// Source inventory artifact.
    /// </summary>
    public required MigrationSourceInventoryArtifact Inventory { get; init; }

    /// <summary>
    /// Optional migration manifest. When omitted, the builder translates the inventory.
    /// </summary>
    public MigrationManifestArtifact? Manifest { get; init; }

    /// <summary>
    /// Optional parity evidence. When omitted, the builder generates parity from the inventory and manifest.
    /// </summary>
    public MigrationParityEvidenceArtifact? ParityEvidence { get; init; }

    /// <summary>
    /// Optional readiness attestation used when parity evidence must be generated.
    /// </summary>
    public MigrationReadinessAttestation? ReadinessAttestation { get; init; }

    /// <summary>
    /// Optional performance and migration-cost evidence used when parity evidence must be generated.
    /// </summary>
    public MigrationPerformanceCostEvidence? PerformanceCost { get; init; }

    /// <summary>
    /// Optional externally collected evidence for canonical acceptance stages such as apply/dry-run and publish.
    /// </summary>
    public MigrationAcceptanceStageEvidenceInput[] StageEvidence { get; init; } = [];

    /// <summary>
    /// Secret-safe artifact references supporting this evidence entry.
    /// </summary>
    public string[] EvidenceReferences { get; init; } = [];
}

/// <summary>
/// Externally collected evidence for one canonical migration acceptance stage.
/// </summary>
public sealed record MigrationAcceptanceStageEvidenceInput
{
    /// <summary>
    /// Stable stage identifier from <see cref="MigrationAcceptanceStageIds"/>.
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// Stage state: <c>pass</c>, <c>fail</c>, <c>unknown</c>, or <c>not-applicable</c>.
    /// </summary>
    public required string State { get; init; }

    /// <summary>
    /// Human-readable summary for this stage.
    /// </summary>
    public required string Summary { get; init; }

    /// <summary>
    /// Artifact kinds supporting this stage.
    /// </summary>
    public string[] ArtifactKinds { get; init; } = [];

    /// <summary>
    /// Secret-safe links or paths to supporting evidence artifacts for this stage.
    /// </summary>
    public string[] EvidenceReferences { get; init; } = [];

    /// <summary>
    /// Deterministic notes explaining stage limitations or follow-up work.
    /// </summary>
    public string[] Notes { get; init; } = [];
}

/// <summary>
/// Options for building a migration acceptance evidence suite.
/// </summary>
public sealed record MigrationAcceptanceEvidenceOptions
{
    /// <summary>
    /// Source kinds that must be represented for the suite to pass.
    /// </summary>
    public string[] RequiredSourceKinds { get; init; } = [];

    /// <summary>
    /// Whether non-automated entries should produce blocking gaps.
    /// </summary>
    public bool RequireAutomatedEntries { get; init; } = true;

    /// <summary>
    /// Whether non-passing readiness should be surfaced in entry notes.
    /// </summary>
    public bool RequireReadinessPass { get; init; } = true;

    /// <summary>
    /// Whether each entry must include passing cost evidence for the suite to pass.
    /// </summary>
    public bool RequireCostEvidence { get; init; }

    /// <summary>
    /// Cost evidence metric coverage and warning/failure thresholds.
    /// </summary>
    public MigrationAcceptanceCostEvidenceThresholds CostEvidenceThresholds { get; init; } = new();
}

/// <summary>
/// Thresholds and required metric coverage for migration cost evidence.
/// </summary>
public sealed record MigrationAcceptanceCostEvidenceThresholds
{
    /// <summary>
    /// Whether core cost metrics must be present: duration, scan/apply durations, throughput, source requests, retries, bytes, artifact size, and manual-review ratio.
    /// </summary>
    public bool RequireMetricContract { get; init; }

    /// <summary>
    /// Operation stages that must include measured durations.
    /// </summary>
    public string[] RequiredDurationStages { get; init; } = [];

    /// <summary>
    /// Total duration warning threshold in milliseconds.
    /// </summary>
    public long? MaxDurationMillisecondsWarn { get; init; }

    /// <summary>
    /// Total duration failure threshold in milliseconds.
    /// </summary>
    public long? MaxDurationMillisecondsFail { get; init; }

    /// <summary>
    /// Feature throughput warning threshold in features per second.
    /// </summary>
    public double? MinFeatureThroughputPerSecondWarn { get; init; }

    /// <summary>
    /// Feature throughput failure threshold in features per second.
    /// </summary>
    public double? MinFeatureThroughputPerSecondFail { get; init; }

    /// <summary>
    /// Source request warning threshold.
    /// </summary>
    public int? MaxSourceRequestCountWarn { get; init; }

    /// <summary>
    /// Source request failure threshold.
    /// </summary>
    public int? MaxSourceRequestCountFail { get; init; }

    /// <summary>
    /// Retry warning threshold.
    /// </summary>
    public int? MaxRetryCountWarn { get; init; }

    /// <summary>
    /// Retry failure threshold.
    /// </summary>
    public int? MaxRetryCountFail { get; init; }

    /// <summary>
    /// Cost evidence artifact-size warning threshold in bytes.
    /// </summary>
    public long? MaxArtifactSizeBytesWarn { get; init; }

    /// <summary>
    /// Cost evidence artifact-size failure threshold in bytes.
    /// </summary>
    public long? MaxArtifactSizeBytesFail { get; init; }

    /// <summary>
    /// Manual-review ratio warning threshold.
    /// </summary>
    public double? MaxManualReviewRatioWarn { get; init; }

    /// <summary>
    /// Manual-review ratio failure threshold.
    /// </summary>
    public double? MaxManualReviewRatioFail { get; init; }
}
