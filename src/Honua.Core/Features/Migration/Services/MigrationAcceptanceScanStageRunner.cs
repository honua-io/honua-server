// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.Migration.Services;
using Honua.Core.Features.FileImport.Abstractions;
using Honua.Core.Features.FileImport.Domain;
using Honua.Core.Features.FileImport.Services;

namespace Honua.Core.Features.Migration.Services;

public static class MigrationAcceptanceScanStageRunner
{
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

        // Not rewritten with LINQ Select/Sum: this loop accumulates four independent counters
        // from each entry's inventory summary plus a classification breakdown (switch, not a
        // 1:1 map) in a single pass. Splitting it into per-field Sum() calls would re-enumerate
        // entries and FidelityClassifications multiple times for no clarity gain.
        foreach (var inventory in (entries).Select(entry => entry.Inventory))
        {
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

public sealed record MigrationAcceptanceScanStageInput
{
    public required string FixtureId { get; init; }

    public required MigrationSourceInventoryArtifact Inventory { get; init; }
}
