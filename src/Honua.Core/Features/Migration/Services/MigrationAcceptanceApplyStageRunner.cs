// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.FileImport.Domain;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.Migration.Services;
using Honua.Core.Features.FileImport.Abstractions;
using Honua.Core.Features.FileImport.Services;

namespace Honua.Core.Features.Migration.Services;

/// <summary>
/// Drives the apply stage of the migration acceptance suite by translating per-source
/// <see cref="MigrationSourceInventoryArtifact"/> inputs into deterministic
/// <see cref="MigrationManifestArtifact"/> outputs and rolling them into a
/// <see cref="MigrationApplyStageReport"/>.
/// </summary>
/// <remarks>
/// <para>
/// This runner is import-service-agnostic: it relies on the existing
/// <see cref="MigrationManifestTranslator"/> to translate the slice-2 inventory output into a
/// target manifest, and on <see cref="MigrationApplyPlanBuilder"/> to derive a deterministic
/// apply-plan replay token plus per-item dispositions. The runner does not reimplement apply,
/// does not introduce additional protocol surface, and does not require a database connection
/// or live HTTP target.
/// </para>
/// <para>
/// Fixture sources whose source family does not yet support a non-destructive apply against the
/// fixture target run in <c>dry-run</c> mode; the runner still emits the
/// <see cref="MigrationManifestArtifact"/> and classifies items deterministically so downstream
/// publish, parity, and readiness stages can replay from the same source set. Callers can opt a
/// fixture into <c>apply</c> mode by setting
/// <see cref="MigrationAcceptanceApplyStageInput.ApplyMode"/> when the upstream import service
/// has already produced an apply outcome.
/// </para>
/// </remarks>
public static class MigrationAcceptanceApplyStageRunner
{
    /// <summary>
    /// Default apply mode used when a fixture does not explicitly opt into a real apply.
    /// </summary>
    public const string DryRunMode = "dry-run";

    /// <summary>
    /// Apply mode indicating the upstream import service performed a real apply for the fixture
    /// target.
    /// </summary>
    public const string ApplyMode = "apply";

    private const string AppliedDisposition = "applied";
    private const string ManualReviewDisposition = "manual-review";
    private const string UnsupportedDisposition = "unsupported";
    private const string ReadyApplyPlanDisposition = "ready";

    /// <summary>
    /// Build a deterministic apply stage report from the supplied per-source inventories.
    /// </summary>
    /// <param name="runId">Stable identifier for this acceptance apply run (e.g. fixture set name).</param>
    /// <param name="scanRunId">Identifier of the upstream scan stage report.</param>
    /// <param name="inputs">Per-fixture apply inputs.</param>
    /// <returns>Aggregate report pinning the apply stage outputs.</returns>
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

    /// <summary>
    /// Build a deterministic per-source apply outcome from a translated manifest. Exposed for
    /// callers (and tests) that drive the apply stage one fixture at a time.
    /// </summary>
    /// <param name="manifest">Translated manifest artifact for the source.</param>
    /// <returns>Deterministic per-source outcome.</returns>
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

/// <summary>
/// One per-fixture input to <see cref="MigrationAcceptanceApplyStageRunner.BuildReport"/>.
/// </summary>
public sealed record MigrationAcceptanceApplyStageInput
{
    /// <summary>
    /// Stable fixture identifier (e.g. <c>arcgis-mapserver-mixed-renderers</c>).
    /// </summary>
    public required string FixtureId { get; init; }

    /// <summary>
    /// Inventory artifact produced by the upstream scan stage for this fixture.
    /// </summary>
    public required MigrationSourceInventoryArtifact Inventory { get; init; }

    /// <summary>
    /// Optional pre-built manifest artifact. When supplied the runner skips translation and uses
    /// the supplied manifest directly. When omitted the runner translates the inventory with
    /// <see cref="MigrationManifestTranslator"/>.
    /// </summary>
    public MigrationManifestArtifact? Manifest { get; init; }

    /// <summary>
    /// Optional manifest translation options. Ignored when <see cref="Manifest"/> is supplied.
    /// </summary>
    public MigrationManifestTranslationOptions? ManifestOptions { get; init; }

    /// <summary>
    /// Apply mode for the fixture. Defaults to
    /// <see cref="MigrationAcceptanceApplyStageRunner.DryRunMode"/>. Set to
    /// <see cref="MigrationAcceptanceApplyStageRunner.ApplyMode"/> when the upstream import
    /// service has performed a real apply against the fixture target.
    /// </summary>
    public string? ApplyMode { get; init; }
}
