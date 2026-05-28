// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;
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
/// AOT-safe JSON serialization context for the slice-5 OGC migration evidence
/// pack. Source-generated metadata avoids reflection-based serialization so the
/// pack — and the canonical bundle bytes that feed the SHA-256 fingerprint —
/// can be produced under <c>PublishAot</c> builds without trimming warnings.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    WriteIndented = false)]
[JsonSerializable(typeof(OgcMigrationEvidencePackArtifact))]
[JsonSerializable(typeof(OgcMigrationEvidencePackBundle))]
[JsonSerializable(typeof(OgcMigrationEvidencePackSummary))]
[JsonSerializable(typeof(OgcMigrationEvidencePackRenderStage))]
[JsonSerializable(typeof(OgcMigrationEvidencePackRenderStage[]))]
[JsonSerializable(typeof(MigrationSourceInventoryArtifact))]
[JsonSerializable(typeof(MigrationManifestArtifact))]
[JsonSerializable(typeof(MigrationManifestPlanEntry))]
[JsonSerializable(typeof(MigrationManifestPlanEntry[]))]
[JsonSerializable(typeof(MigrationManifestPlanDiagnostic))]
[JsonSerializable(typeof(MigrationManifestPlanDiagnostic[]))]
[JsonSerializable(typeof(MigrationSourceIdentity))]
[JsonSerializable(typeof(MigrationCompatibilityAssessment))]
[JsonSerializable(typeof(OgcWfsImportResult))]
[JsonSerializable(typeof(OgcWfsImportedFeatureType))]
[JsonSerializable(typeof(OgcWfsImportedFeatureType[]))]
[JsonSerializable(typeof(OgcTileCacheExportResult))]
[JsonSerializable(typeof(OgcTileCacheExportedTileSet))]
[JsonSerializable(typeof(OgcTileCacheExportedTileSet[]))]
public sealed partial class OgcMigrationEvidencePackJsonContext : JsonSerializerContext
{
}
