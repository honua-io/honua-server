// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.Migration.Services;
using Honua.Core.Features.FileImport.Abstractions;
using Honua.Core.Features.FileImport.Domain;
using Honua.Core.Features.FileImport.Services;

namespace Honua.Core.Features.Migration.Services;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    WriteIndented = false)]
[JsonSerializable(typeof(OgcCoverageMigrationEvidencePackArtifact))]
[JsonSerializable(typeof(OgcCoverageMigrationEvidencePackBundle))]
[JsonSerializable(typeof(OgcCoverageMigrationEvidencePackScope))]
[JsonSerializable(typeof(OgcCoverageMigrationEvidencePackSummary))]
[JsonSerializable(typeof(OgcCoverageMigrationEvidencePackChannel))]
[JsonSerializable(typeof(OgcCoverageMigrationEvidencePackChannel[]))]
[JsonSerializable(typeof(OgcCoverageImportRecord))]
[JsonSerializable(typeof(OgcCoverageImportRecord[]))]
[JsonSerializable(typeof(MigrationCoverageStyleDiagnostic))]
[JsonSerializable(typeof(MigrationCoverageStyleDiagnostic[]))]
[JsonSerializable(typeof(MigrationSourceInventoryArtifact))]
[JsonSerializable(typeof(MigrationManifestArtifact))]
[JsonSerializable(typeof(MigrationSourceIdentity))]
[JsonSerializable(typeof(MigrationCompatibilityAssessment))]
public sealed partial class OgcCoverageMigrationEvidencePackJsonContext : JsonSerializerContext
{
}
