// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;
using Honua.Core.Features.Metadata.Domain;

namespace Honua.Core.Features.Import.Domain;

/// <summary>
/// Source-generated JSON context for migration manifest contracts.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(GeoServerTranslationRequest))]
[JsonSerializable(typeof(MigrationManifest))]
[JsonSerializable(typeof(GeoServerMigrationSourceSummary))]
[JsonSerializable(typeof(MigrationSourceCompatibilitySummary))]
[JsonSerializable(typeof(GeoServerMigrationSelection))]
[JsonSerializable(typeof(MigrationManifestSummary))]
[JsonSerializable(typeof(MigrationConnectionDraft))]
[JsonSerializable(typeof(MigrationConnectionDraft[]))]
[JsonSerializable(typeof(MigrationSecretRequirement))]
[JsonSerializable(typeof(MigrationSecretRequirement[]))]
[JsonSerializable(typeof(MigrationPublishPlanEntry))]
[JsonSerializable(typeof(MigrationPublishPlanEntry[]))]
[JsonSerializable(typeof(MigrationStylePlanEntry))]
[JsonSerializable(typeof(MigrationStylePlanEntry[]))]
[JsonSerializable(typeof(MigrationDiagnostic))]
[JsonSerializable(typeof(MigrationDiagnostic[]))]
[JsonSerializable(typeof(MigrationSourceType))]
[JsonSerializable(typeof(MigrationPlanStatus))]
[JsonSerializable(typeof(MigrationStyleTranslationStatus))]
[JsonSerializable(typeof(MigrationDiagnosticSeverity))]
[JsonSerializable(typeof(MigrationConnectionEngine))]
[JsonSerializable(typeof(MigrationSecretRequirementKind))]
[JsonSerializable(typeof(MetadataResource))]
[JsonSerializable(typeof(MetadataResource[]))]
[JsonSerializable(typeof(ResourceMetadata))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(IReadOnlyDictionary<string, string>))]
[JsonSerializable(typeof(IReadOnlyList<string>))]
[JsonSerializable(typeof(JsonElement))]
public sealed partial class MigrationManifestJsonContext : JsonSerializerContext
{
}
