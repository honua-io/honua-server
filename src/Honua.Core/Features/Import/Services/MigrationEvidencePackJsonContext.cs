// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;
using Honua.Core.Features.Import.Domain;

namespace Honua.Core.Features.Import.Services;

/// <summary>
/// AOT-safe JSON serialization context for the slice-4 migration evidence
/// pack. Source-generated metadata avoids reflection-based serialization so
/// the pack can be emitted under <c>PublishAot</c> builds without trimming
/// warnings.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    WriteIndented = false)]
[JsonSerializable(typeof(MigrationEvidencePackArtifact))]
[JsonSerializable(typeof(MigrationEvidencePackBundle))]
[JsonSerializable(typeof(MigrationEvidencePackWorkspaceScope))]
[JsonSerializable(typeof(MigrationEvidencePackApplyIdentity))]
[JsonSerializable(typeof(MigrationEvidencePackSummary))]
[JsonSerializable(typeof(MigrationEvidencePackStage))]
[JsonSerializable(typeof(MigrationEvidencePackStage[]))]
[JsonSerializable(typeof(MigrationEvidencePackStyleDiagnostic))]
[JsonSerializable(typeof(MigrationEvidencePackStyleDiagnostic[]))]
[JsonSerializable(typeof(MigrationSourceInventoryArtifact))]
[JsonSerializable(typeof(MigrationManifestArtifact))]
[JsonSerializable(typeof(MigrationApplyExecutionArtifact))]
[JsonSerializable(typeof(MigrationApplyExecutionStepResult))]
[JsonSerializable(typeof(MigrationApplyExecutionStepResult[]))]
[JsonSerializable(typeof(MigrationApplyExecutionSummary))]
[JsonSerializable(typeof(MigrationSourceIdentity))]
[JsonSerializable(typeof(MigrationCompatibilityAssessment))]
[JsonSerializable(typeof(MigrationManifestReviewItem))]
[JsonSerializable(typeof(MigrationManifestReviewItem[]))]
public sealed partial class MigrationEvidencePackJsonContext : JsonSerializerContext
{
}
