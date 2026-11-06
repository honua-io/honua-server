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

// The fingerprint excludes timestamps and the generator label, and the pack never copies credentials, feature payloads, response bodies, or headers.
public sealed record OgcApiFeaturesMigrationEvidencePackArtifact
{
    public string ArtifactKind { get; init; } = "honua.migration.ogc-api-features.evidence-pack";

    public string ArtifactVersion { get; init; } = "1.0";

    public required string RunId { get; init; }

    public required string Generator { get; init; }

    public required DateTimeOffset GeneratedAt { get; init; }

    public required string BundleFingerprint { get; init; }

    public required OgcApiFeaturesMigrationEvidencePackBundle Bundle { get; init; }
}

public sealed record OgcApiFeaturesMigrationEvidencePackBundle
{
    public required string SourceKind { get; init; }

    public required MigrationSourceIdentity Source { get; init; }

    public required OgcApiFeaturesMigrationEvidencePackSummary Summary { get; init; }

    public required MigrationSourceInventoryArtifact Inventory { get; init; }

    public OgcApiFeaturesMigrationEvidencePackCollectionResult[] Collections { get; init; } = [];
}

public sealed record OgcApiFeaturesMigrationEvidencePackSummary
{
    public int InventoryCollectionCount { get; init; }

    public int ConformanceClassCount { get; init; }

    public int CollectionResultCount { get; init; }

    public int SucceededCollectionCount { get; init; }

    public int FailedCollectionCount { get; init; }

    public long TotalFeaturesImported { get; init; }

    public long TotalFeaturesSkipped { get; init; }

    public long TotalPagesFetched { get; init; }

    public int TruncatedCollectionCount { get; init; }

    public int ScopeDriftCollectionCount { get; init; }

    public int TotalSchemaMappingDiagnosticCount { get; init; }

    public int SchemaMappingManualReviewCount { get; init; }

    public int SchemaMappingUnsupportedCount { get; init; }
}

public sealed record OgcApiFeaturesMigrationEvidencePackCollectionResult
{
    public required string CollectionId { get; init; }

    public required bool Success { get; init; }

    public required string Target { get; init; }

    public int FeaturesImported { get; init; }

    public int FeaturesSkipped { get; init; }

    public int PagesFetched { get; init; }

    public bool Truncated { get; init; }

    public string? ErrorCode { get; init; }

    public required OgcApiFeaturesMigrationEvidencePackFilterScope FilterScope { get; init; }

    public OgcApiFeaturesSchemaMappingDiagnostic[] MappingDiagnostics { get; init; } = [];
}

public sealed record OgcApiFeaturesMigrationEvidencePackFilterScope
{
    public string? Filter { get; init; }

    public string? Bbox { get; init; }

    public string? Datetime { get; init; }

    public bool ScopeDriftDetected { get; init; }

    public string? ManualReviewReason { get; init; }
}
