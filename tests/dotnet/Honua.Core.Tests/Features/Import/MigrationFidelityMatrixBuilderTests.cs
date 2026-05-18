// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Import.Services;

namespace Honua.Core.Tests.Features.Import;

public sealed class MigrationFidelityMatrixBuilderTests
{
    [Fact]
    public void Build_WithClassifications_GroupsCellsAndEnrichesTargetIds()
    {
        var records = new[]
        {
            CreateFidelity(
                "classification:layer:0:identity",
                "layer:0",
                "layer",
                "identity",
                MigrationFidelityAutomationStatuses.Automated,
                ImportCompatibilityCodes.Compatible),
            CreateFidelity(
                "classification:layer:0:domains",
                "layer:0",
                "field-domain",
                "domains",
                MigrationFidelityAutomationStatuses.Assisted,
                ImportCompatibilityCodes.ManualReview,
                manualSteps: ["Review coded-value domains before publishing."]),
            CreateFidelity(
                "classification:layer:0:relationships",
                "layer:0",
                "relationship",
                "relationships",
                MigrationFidelityAutomationStatuses.ManualReview,
                ImportCompatibilityCodes.ArcGisRelationshipsManualReview,
                manualSteps: ["Map related layers to target relationships."]),
            CreateFidelity(
                "classification:renderer:0",
                "renderer:0",
                "renderer",
                "renderers",
                MigrationFidelityAutomationStatuses.Unsupported,
                ImportCompatibilityCodes.ArcGisUnsupportedRenderer,
                relatedIds: ["dependency:symbol"])
        };
        var remaps = new[]
        {
            new MigrationManifestIdentityRemap
            {
                SourceId = "layer:0",
                SourceKind = "layer",
                TargetId = "target:resource:arcgis-fidelity:inspections",
                TargetKind = "resource",
                TargetName = "inspections",
                Action = "manual-review"
            },
            new MigrationManifestIdentityRemap
            {
                SourceId = "renderer:0",
                SourceKind = "renderer",
                TargetId = "target:style:arcgis-fidelity:inspections",
                TargetKind = "style",
                TargetName = "inspections",
                Action = "manual-review"
            }
        };

        var matrix = MigrationFidelityMatrixBuilder.Build(records, remaps);

        matrix.Should().NotBeNull();
        matrix!.Summary.Should().BeEquivalentTo(new MigrationFidelityMatrixSummary
        {
            TotalCount = 4,
            AutomatedCount = 1,
            AssistedCount = 1,
            ManualReviewCount = 1,
            UnsupportedCount = 1
        });
        matrix.Cells.Should().ContainSingle(cell =>
                cell.Category == "domains" &&
                cell.AutomationStatus == MigrationFidelityAutomationStatuses.Assisted)
            .Which.Should().Match<MigrationFidelityMatrixCell>(cell =>
                cell.Count == 1 &&
                cell.SourceIds.SequenceEqual(new[] { "layer:0" }) &&
                cell.TargetIds.SequenceEqual(new[] { "target:resource:arcgis-fidelity:inspections" }) &&
                cell.ManualSteps.SequenceEqual(new[] { "Review coded-value domains before publishing." }));
        matrix.Cells.Should().ContainSingle(cell =>
                cell.Category == "renderers" &&
                cell.AutomationStatus == MigrationFidelityAutomationStatuses.Unsupported)
            .Which.Should().Match<MigrationFidelityMatrixCell>(cell =>
                cell.Codes.SequenceEqual(new[] { ImportCompatibilityCodes.ArcGisUnsupportedRenderer }) &&
                cell.RelatedIds.SequenceEqual(new[] { "dependency:symbol" }) &&
                cell.TargetIds.SequenceEqual(new[] { "target:style:arcgis-fidelity:inspections" }));
    }

    private static MigrationFidelityClassificationRecord CreateFidelity(
        string id,
        string sourceId,
        string kind,
        string category,
        string automationStatus,
        string code,
        string[]? manualSteps = null,
        string[]? relatedIds = null)
        => new()
        {
            Id = id,
            SourceId = sourceId,
            Kind = kind,
            Category = category,
            AutomationStatus = automationStatus,
            Code = code,
            Reason = $"{category} disposition captured.",
            ManualSteps = manualSteps ?? [],
            RelatedIds = relatedIds ?? []
        };
}
