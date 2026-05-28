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
/// AOT-safe JSON serialization context for the slice-5 OGC coverage
/// migration evidence pack (issue #1030). Source-generated metadata avoids
/// reflection-based serialization so the pack can be emitted under
/// <c>PublishAot</c> builds without trimming warnings.
/// </summary>
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
