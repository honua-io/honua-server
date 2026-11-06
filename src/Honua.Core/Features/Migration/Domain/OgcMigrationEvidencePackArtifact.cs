// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.Migration.Services;
using Honua.Core.Features.FileImport.Abstractions;
using Honua.Core.Features.FileImport.Domain;
using Honua.Core.Features.FileImport.Services;
namespace Honua.Core.Features.Migration.Domain;

// The fingerprint excludes timestamps and the generator label, and the pack never includes credentials, feature payloads, capabilities documents, or tile bytes.
public sealed record OgcMigrationEvidencePackArtifact
{
    public string ArtifactKind { get; init; } = "honua.migration.ogc.evidence-pack";

    public string ArtifactVersion { get; init; } = "1.0";

    public required string RunId { get; init; }

    public required string Generator { get; init; }

    public required DateTimeOffset GeneratedAt { get; init; }

    public required string BundleFingerprint { get; init; }

    public required OgcMigrationEvidencePackBundle Bundle { get; init; }
}

public sealed record OgcMigrationEvidencePackBundle
{
    public required string SourceKind { get; init; }

    public required MigrationSourceIdentity Source { get; init; }

    public required OgcMigrationEvidencePackSummary Summary { get; init; }

    public required MigrationSourceInventoryArtifact Inventory { get; init; }

    public OgcWfsImportResult? WfsImport { get; init; }

    public required OgcMigrationEvidencePackRenderStage WmsPlan { get; init; }

    public required OgcMigrationEvidencePackRenderStage WmtsPlan { get; init; }

    public OgcTileCacheExportResult? TileCacheExport { get; init; }
}

public sealed record OgcMigrationEvidencePackRenderStage
{
    public required string ServiceKind { get; init; }

    public int EntryCount { get; init; }

    public int AutomatedCount { get; init; }

    public int AssistedCount { get; init; }

    public int ManualReviewCount { get; init; }

    public int UnsupportedCount { get; init; }

    public MigrationManifestPlanEntry[] Entries { get; init; } = [];

    public MigrationManifestPlanDiagnostic[] Diagnostics { get; init; } = [];

    public static OgcMigrationEvidencePackRenderStage Empty(string serviceKind) =>
        new()
        {
            ServiceKind = serviceKind,
            EntryCount = 0,
            Entries = [],
            Diagnostics = []
        };
}

public sealed record OgcMigrationEvidencePackSummary
{
    public int InventoryContainerCount { get; init; }

    public int InventoryResourceCount { get; init; }

    public int InventoryStyleCount { get; init; }

    public bool WfsImportExecuted { get; init; }

    public int WfsFeatureTypesImported { get; init; }

    public int WfsFeatureTypesSkipped { get; init; }

    public int WfsFeaturesCopied { get; init; }

    public int WmsPlanEntryCount { get; init; }

    public int WmtsPlanEntryCount { get; init; }

    // Manual-review and unsupported render entries block an automated render-parity claim.
    public int RenderManualReviewOrUnsupportedCount { get; init; }

    public bool TileCacheExportExecuted { get; init; }

    public int TileCacheTileSetsExported { get; init; }

    public int TileCacheTileSetsSkipped { get; init; }

    public int TileCacheTilesPersisted { get; init; }

    public int TileCacheTilesFailed { get; init; }
}
