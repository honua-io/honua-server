// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;
using Honua.Core.Features.Import.Domain;

namespace Honua.Core.Features.Import.Services;

/// <summary>
/// AOT-safe JSON serialization context for the slice-5 OGC API Features migration evidence
/// pack. Source-generated metadata avoids reflection-based serialization so the pack can be
/// emitted under <c>PublishAot</c> builds without trimming warnings.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    WriteIndented = false)]
[JsonSerializable(typeof(OgcApiFeaturesMigrationEvidencePackArtifact))]
[JsonSerializable(typeof(OgcApiFeaturesMigrationEvidencePackBundle))]
[JsonSerializable(typeof(OgcApiFeaturesMigrationEvidencePackSummary))]
[JsonSerializable(typeof(OgcApiFeaturesMigrationEvidencePackCollectionResult))]
[JsonSerializable(typeof(OgcApiFeaturesMigrationEvidencePackCollectionResult[]))]
[JsonSerializable(typeof(OgcApiFeaturesMigrationEvidencePackFilterScope))]
[JsonSerializable(typeof(OgcApiFeaturesSchemaMappingDiagnostic))]
[JsonSerializable(typeof(OgcApiFeaturesSchemaMappingDiagnostic[]))]
[JsonSerializable(typeof(OgcApiFeaturesSchemaMappingClassification))]
[JsonSerializable(typeof(MigrationSourceInventoryArtifact))]
[JsonSerializable(typeof(MigrationSourceIdentity))]
[JsonSerializable(typeof(MigrationCompatibilityAssessment))]
public sealed partial class OgcApiFeaturesMigrationEvidencePackJsonContext : JsonSerializerContext
{
}
