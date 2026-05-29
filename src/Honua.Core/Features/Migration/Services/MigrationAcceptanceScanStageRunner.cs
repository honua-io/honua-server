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

namespace Honua.Core.Features.Migration.Services;

/// <summary>
/// Drives the scan stage of the migration acceptance suite by aggregating per-source
/// <see cref="MigrationSourceInventoryArtifact"/> outputs into a deterministic
/// <see cref="MigrationScanStageReport"/>.
/// </summary>
/// <remarks>
/// This runner is scanner-agnostic: callers invoke the existing per-source scanners
/// (<c>GeoservicesImportService.ScanSourceAsync</c>, <c>GeoServerImportService.ScanSourceAsync</c>,
/// <see cref="OgcApiFeaturesMigrationInventoryScanner"/>, ...) themselves and hand the resulting
/// inventory artifacts to <see cref="BuildReport"/>. The runner does not reimplement scanning
/// and does not introduce additional protocol surface; it only rolls up classifications and
/// orders results deterministically so the report is stable across re-runs.
/// </remarks>
public static class MigrationAcceptanceScanStageRunner
{
    /// <summary>
    /// Builds a deterministic scan stage report from the supplied per-source inventories.
    /// </summary>
    /// <param name="runId">Stable identifier for the acceptance run (e.g. fixture set name).</param>
    /// <param name="inputs">Per-fixture scan inputs.</param>
    /// <returns>Aggregate report pinning the scan stage outputs.</returns>
    public static MigrationScanStageReport BuildReport(
        string runId,
        IEnumerable<MigrationAcceptanceScanStageInput> inputs)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentNullException.ThrowIfNull(inputs);

        var ordered = inputs
            .Select(input =>
            {
                ArgumentNullException.ThrowIfNull(input);
                if (string.IsNullOrWhiteSpace(input.FixtureId))
                {
                    throw new ArgumentException(
                        "Scan stage inputs must supply a non-empty fixture identifier.",
                        nameof(inputs));
                }

                return new MigrationScanStageEntry
                {
                    FixtureId = input.FixtureId,
                    SourceKind = input.Inventory.SourceKind,
                    Inventory = input.Inventory
                };
            })
            .OrderBy(static entry => entry.FixtureId, StringComparer.Ordinal)
            .ToArray();

        var summary = BuildSummary(ordered);

        return new MigrationScanStageReport
        {
            RunId = runId,
            Summary = summary,
            Sources = ordered
        };
    }

    private static MigrationScanStageSummary BuildSummary(MigrationScanStageEntry[] entries)
    {
        var containerCount = 0;
        var resourceCount = 0;
        var styleCount = 0;
        var externalDependencyCount = 0;
        var automated = 0;
        var assisted = 0;
        var manualReview = 0;
        var unsupported = 0;

        foreach (var entry in entries)
        {
            var inventory = entry.Inventory;
            containerCount += inventory.Summary.ContainerCount;
            resourceCount += inventory.Summary.ResourceCount;
            styleCount += inventory.Summary.StyleCount;
            externalDependencyCount += inventory.Summary.ExternalDependencyCount;

            foreach (var classification in inventory.FidelityClassifications)
            {
                switch (classification.AutomationStatus)
                {
                    case MigrationFidelityAutomationStatuses.Automated:
                        automated++;
                        break;
                    case MigrationFidelityAutomationStatuses.Assisted:
                        assisted++;
                        break;
                    case MigrationFidelityAutomationStatuses.ManualReview:
                        manualReview++;
                        break;
                    case MigrationFidelityAutomationStatuses.Unsupported:
                        unsupported++;
                        break;
                }
            }
        }

        return new MigrationScanStageSummary
        {
            SourceCount = entries.Length,
            ContainerCount = containerCount,
            ResourceCount = resourceCount,
            StyleCount = styleCount,
            ExternalDependencyCount = externalDependencyCount,
            AutomatedCount = automated,
            AssistedCount = assisted,
            ManualReviewCount = manualReview,
            UnsupportedCount = unsupported
        };
    }
}

/// <summary>
/// One per-fixture input to <see cref="MigrationAcceptanceScanStageRunner.BuildReport"/>.
/// </summary>
public sealed record MigrationAcceptanceScanStageInput
{
    /// <summary>
    /// Stable fixture identifier (e.g. <c>arcgis-featureserver-supported</c>).
    /// </summary>
    public required string FixtureId { get; init; }

    /// <summary>
    /// Inventory artifact produced by the underlying scanner for this fixture.
    /// </summary>
    public required MigrationSourceInventoryArtifact Inventory { get; init; }
}
