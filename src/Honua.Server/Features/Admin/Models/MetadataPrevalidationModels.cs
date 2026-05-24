// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Server.Features.Infrastructure.Models;

namespace Honua.Server.Features.Admin.Models;

/// <summary>
/// Request body for Metadata v2 compatibility prevalidation.
/// </summary>
internal sealed record MetadataPrevalidateRequest
{
    /// <summary>Persisted release package identifier.</summary>
    [JsonPropertyName("releasePackageId")]
    public Guid? ReleasePackageId { get; init; }

    /// <summary>Inline release package payload.</summary>
    [JsonPropertyName("releasePackage")]
    public MetadataReleasePackage? ReleasePackage { get; init; }

    /// <summary>Target environment to compare against.</summary>
    [JsonPropertyName("targetEnvironment")]
    public required string TargetEnvironment { get; init; }

    /// <summary>Optional declared data script contracts.</summary>
    [JsonPropertyName("dataScripts")]
    public IReadOnlyList<MetadataDataScriptEntry>? DataScripts { get; init; } =
        Array.Empty<MetadataDataScriptEntry>();

    /// <summary>
    /// Converts the server request DTO into the Core service request.
    /// </summary>
    public MetadataCompatibilityPrevalidationRequest ToCoreRequest()
        => new()
        {
            ReleasePackageId = ReleasePackageId,
            ReleasePackage = ReleasePackage,
            TargetEnvironment = TargetEnvironment,
            DataScripts = DataScripts ?? Array.Empty<MetadataDataScriptEntry>(),
        };
}

/// <summary>
/// JSON source generation context for Metadata v2 compatibility prevalidation APIs.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(MetadataPrevalidateRequest))]
[JsonSerializable(typeof(ApiResponse<MetadataCompatibilityReport>))]
[JsonSerializable(typeof(MetadataCompatibilityReport))]
[JsonSerializable(typeof(MetadataCompatibilityFinding))]
[JsonSerializable(typeof(MetadataCompatibilityValue))]
[JsonSerializable(typeof(MetadataAffectedDependent))]
[JsonSerializable(typeof(MetadataRollbackReadiness))]
[JsonSerializable(typeof(MetadataDataScriptEntry))]
[JsonSerializable(typeof(MetadataDataScriptContract))]
[JsonSerializable(typeof(MetadataScriptResourceContract))]
[JsonSerializable(typeof(MetadataScriptFieldContract))]
[JsonSerializable(typeof(MetadataScriptSpatialContract))]
[JsonSerializable(typeof(MetadataScriptTemporalContract))]
[JsonSerializable(typeof(MetadataScriptStorageContract))]
[JsonSerializable(typeof(MetadataReleasePackage))]
[JsonSerializable(typeof(MetadataReleaseEntry))]
[JsonSerializable(typeof(MetadataReleaseTargetState))]
[JsonSerializable(typeof(MetadataV2ObjectMetadata))]
[JsonSerializable(typeof(MetadataBoundFieldSummary))]
[JsonSerializable(typeof(MetadataCompatibilityFinding[]))]
[JsonSerializable(typeof(MetadataAffectedDependent[]))]
[JsonSerializable(typeof(MetadataDataScriptEntry[]))]
[JsonSerializable(typeof(MetadataScriptResourceContract[]))]
[JsonSerializable(typeof(MetadataScriptFieldContract[]))]
[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(IReadOnlyDictionary<string, string>), TypeInfoPropertyName = "ReadOnlyDictionaryStringString")]
internal sealed partial class MetadataPrevalidationJsonContext : JsonSerializerContext
{
}
