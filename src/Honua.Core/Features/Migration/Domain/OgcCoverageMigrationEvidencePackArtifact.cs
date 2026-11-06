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

// The fingerprint excludes the timestamp, run id, and generator label, and the pack never includes credentials, raster payloads, or style documents.
public sealed record OgcCoverageMigrationEvidencePackArtifact
{
    public string ArtifactKind { get; init; } = "honua.migration.ogc-coverage-evidence-pack";

    public string ArtifactVersion { get; init; } = "1.0";

    public required string RunId { get; init; }

    public required string Generator { get; init; }

    public required DateTimeOffset GeneratedAt { get; init; }

    public required string BundleFingerprint { get; init; }

    public required OgcCoverageMigrationEvidencePackBundle Bundle { get; init; }
}

public sealed record OgcCoverageMigrationEvidencePackBundle
{
    public required string SourceKind { get; init; }

    public required MigrationSourceIdentity Source { get; init; }

    public required OgcCoverageMigrationEvidencePackScope CoverageScope { get; init; }

    public required OgcCoverageMigrationEvidencePackSummary Summary { get; init; }

    // Channels are emitted in canonical order so the fingerprint does not depend on caller order.
    public OgcCoverageMigrationEvidencePackChannel[] Channels { get; init; } = [];

    public MigrationCoverageStyleDiagnostic[] StyleDiagnostics { get; init; } = [];

    public required MigrationSourceInventoryArtifact Inventory { get; init; }
}

public sealed record OgcCoverageMigrationEvidencePackScope
{
    public required bool Restricted { get; init; }

    public string[] CoverageIds { get; init; } = [];
}

public sealed record OgcCoverageMigrationEvidencePackSummary
{
    public int TotalCoverageCount { get; init; }

    public int ImportedCount { get; init; }

    public int PlannedCount { get; init; }

    public int SkippedCount { get; init; }

    public int ManualReviewCount { get; init; }

    public int FailedCount { get; init; }

    public int StyleDiagnosticCount { get; init; }

    public int StyleManualReviewCount { get; init; }
}

public sealed record OgcCoverageMigrationEvidencePackChannel
{
    public required string Id { get; init; }

    public bool ApplyMode { get; init; }

    public bool DryRun { get; init; }

    public string? ResolvedVersion { get; init; }

    public string? RequestedOutputFormat { get; init; }

    public int CoverageCount { get; init; }

    public int ImportedCount { get; init; }

    public int PlannedCount { get; init; }

    public int SkippedCount { get; init; }

    public int ManualReviewCount { get; init; }

    public int FailedCount { get; init; }

    public OgcCoverageImportRecord[] Records { get; init; } = [];

    public required MigrationManifestArtifact Manifest { get; init; }
}

public static class OgcCoverageMigrationEvidencePackChannelIds
{
    public const string OgcApiCoverages = "ogc-api-coverages";

    public const string Wcs = "ogc-wcs";
}
