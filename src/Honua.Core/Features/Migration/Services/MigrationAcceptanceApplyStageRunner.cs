// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.FileImport.Domain;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.Migration.Services;
using Honua.Core.Features.FileImport.Abstractions;
using Honua.Core.Features.FileImport.Services;

namespace Honua.Core.Features.Migration.Services;

public static class MigrationAcceptanceApplyStageRunner
{
    public const string DryRunMode = "dry-run";

    public const string ApplyMode = "apply";

    private const string AppliedDisposition = "applied";
    private const string ManualReviewDisposition = "manual-review";
    private const string UnsupportedDisposition = "unsupported";
    private const string ReadyApplyPlanDisposition = "ready";

    public static MigrationApplyStageReport BuildReport(
        string runId,
        string scanRunId,
        IEnumerable<MigrationAcceptanceApplyStageInput> inputs)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentException.ThrowIfNullOrWhiteSpace(scanRunId);
        ArgumentNullException.ThrowIfNull(inputs);

        var entries = inputs
            .Select(input =>
            {
                ArgumentNullException.ThrowIfNull(input);
                if (string.IsNullOrWhiteSpace(input.FixtureId))
                {
                    throw new ArgumentException(
                        "Apply stage inputs must supply a non-empty fixture identifier.",
                        nameof(inputs));
                }

                if (input.Inventory is null)
                {
                    throw new ArgumentException(
                        $"Apply stage input for fixture '{input.FixtureId}' is missing its inventory artifact.",
                        nameof(inputs));
                }

                var manifest = input.Manifest
                    ?? MigrationManifestTranslator.Translate(input.Inventory, input.ManifestOptions);
                var applyPlan = MigrationApplyPlanBuilder.Build(manifest);
                var outcome = BuildOutcome(manifest, applyPlan);
                var applyMode = NormalizeApplyMode(input.ApplyMode);

                return new MigrationApplyStageEntry
                {
                    FixtureId = input.FixtureId,
                    SourceKind = manifest.SourceKind,
                    ApplyMode = applyMode,
                    Outcome = outcome
                };
            })
            .OrderBy(static entry => entry.FixtureId, StringComparer.Ordinal)
            .ToArray();

        return new MigrationApplyStageReport
        {
            RunId = runId,
            ScanRunId = scanRunId,
            Summary = BuildSummary(entries),
            Sources = entries
        };
    }

    public static MigrationApplyStageOutcome BuildOutcome(MigrationManifestArtifact manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return BuildOutcome(manifest, MigrationApplyPlanBuilder.Build(manifest));
    }

    private static MigrationApplyStageOutcome BuildOutcome(
        MigrationManifestArtifact manifest,
        MigrationApplyPlanArtifact applyPlan)
    {
        var classifications = applyPlan.Steps
            .Select(step => new MigrationApplyStageItemClassification
            {
                SourceId = step.SourceId,
                Kind = step.Kind,
                Disposition = MapStepDisposition(step.Disposition),
                Action = step.Action
            })
            .OrderBy(static item => item.SourceId, StringComparer.Ordinal)
            .ThenBy(static item => item.Kind, StringComparer.Ordinal)
            .ToArray();

        var diagnostics = BuildDiagnostics(manifest);

        return new MigrationApplyStageOutcome
        {
            Manifest = manifest,
            ReplayToken = applyPlan.ReplayToken,
            AppliedItemCount = applyPlan.Summary.ReadyStepCount,
            ManualReviewItemCount = applyPlan.Summary.ManualReviewStepCount,
            UnsupportedItemCount = applyPlan.Summary.UnsupportedStepCount,
            Classifications = classifications,
            ManualReviewItems = manifest.ManualReviewItems
                .OrderBy(static item => item.SourceId, StringComparer.Ordinal)
                .ThenBy(static item => item.Code, StringComparer.Ordinal)
                .ToArray(),
            Diagnostics = diagnostics
        };
    }

    private static MigrationApplyStageDiagnostic[] BuildDiagnostics(MigrationManifestArtifact manifest)
    {
        var diagnostics = new List<MigrationApplyStageDiagnostic>();

        foreach (var item in manifest.ManualReviewItems)
        {
            diagnostics.Add(new MigrationApplyStageDiagnostic
            {
                Code = item.Code,
                Severity = "manual-review",
                SourceId = item.SourceId,
                Message = item.Reason
            });
        }

        foreach (var item in manifest.UnsupportedItems)
        {
            diagnostics.Add(new MigrationApplyStageDiagnostic
            {
                Code = item.Code,
                Severity = "unsupported",
                SourceId = item.SourceId,
                Message = item.Reason
            });
        }

        return diagnostics
            .OrderBy(static item => item.SourceId, StringComparer.Ordinal)
            .ThenBy(static item => item.Severity, StringComparer.Ordinal)
            .ThenBy(static item => item.Code, StringComparer.Ordinal)
            .ToArray();
    }

    private static string MapStepDisposition(string disposition)
        => disposition switch
        {
            ReadyApplyPlanDisposition => AppliedDisposition,
            UnsupportedDisposition => UnsupportedDisposition,
            _ => ManualReviewDisposition
        };

    private static string NormalizeApplyMode(string? applyMode)
    {
        if (string.IsNullOrWhiteSpace(applyMode))
        {
            return DryRunMode;
        }

        var trimmed = applyMode.Trim();
        return trimmed switch
        {
            ApplyMode => ApplyMode,
            DryRunMode => DryRunMode,
            _ => throw new ArgumentException(
                $"Apply mode must be either '{ApplyMode}' or '{DryRunMode}' (received '{applyMode}').",
                nameof(applyMode))
        };
    }

    private static MigrationApplyStageSummary BuildSummary(MigrationApplyStageEntry[] entries)
    {
        var appliedSources = 0;
        var dryRunSources = 0;
        var appliedItems = 0;
        var manualReviewItems = 0;
        var unsupportedItems = 0;
        var diagnosticCount = 0;

        foreach (var entry in entries)
        {
            if (string.Equals(entry.ApplyMode, ApplyMode, StringComparison.Ordinal))
            {
                appliedSources++;
            }
            else
            {
                dryRunSources++;
            }

            appliedItems += entry.Outcome.AppliedItemCount;
            manualReviewItems += entry.Outcome.ManualReviewItemCount;
            unsupportedItems += entry.Outcome.UnsupportedItemCount;
            diagnosticCount += entry.Outcome.Diagnostics.Length;
        }

        return new MigrationApplyStageSummary
        {
            SourceCount = entries.Length,
            AppliedSourceCount = appliedSources,
            DryRunSourceCount = dryRunSources,
            AppliedItemCount = appliedItems,
            ManualReviewItemCount = manualReviewItems,
            UnsupportedItemCount = unsupportedItems,
            DiagnosticCount = diagnosticCount
        };
    }
}

public sealed record MigrationAcceptanceApplyStageInput
{
    public required string FixtureId { get; init; }

    public required MigrationSourceInventoryArtifact Inventory { get; init; }

    public MigrationManifestArtifact? Manifest { get; init; }

    public MigrationManifestTranslationOptions? ManifestOptions { get; init; }

    public string? ApplyMode { get; init; }
}
