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
            DataScripts = NormalizeDataScripts(DataScripts),
        };

    private static MetadataDataScriptEntry[] NormalizeDataScripts(
        IReadOnlyList<MetadataDataScriptEntry>? dataScripts)
    {
        if (dataScripts is null || dataScripts.Count == 0)
        {
            return Array.Empty<MetadataDataScriptEntry>();
        }

        var normalized = new MetadataDataScriptEntry[dataScripts.Count];
        for (var index = 0; index < dataScripts.Count; index++)
        {
            var script = dataScripts[index];
            normalized[index] = script is null
                ? null!
                : script with
                {
                    DeclaredOperations = script.DeclaredOperations ?? Array.Empty<string>(),
                    BeforeContract = NormalizeContract(script.BeforeContract),
                    AfterContract = NormalizeContract(script.AfterContract),
                };
        }

        return normalized;
    }

    private static MetadataDataScriptContract? NormalizeContract(MetadataDataScriptContract? contract)
        => contract is null
            ? null
            : contract with
            {
                Resources = NormalizeResources(contract.Resources),
            };

    private static MetadataScriptResourceContract[] NormalizeResources(
        IReadOnlyList<MetadataScriptResourceContract>? resources)
    {
        if (resources is null || resources.Count == 0)
        {
            return Array.Empty<MetadataScriptResourceContract>();
        }

        var normalized = new MetadataScriptResourceContract[resources.Count];
        for (var index = 0; index < resources.Count; index++)
        {
            var resource = resources[index];
            normalized[index] = resource is null
                ? null!
                : resource with
                {
                    Fields = NormalizeFields(resource.Fields),
                    RequiredIdentifiers = resource.RequiredIdentifiers ?? Array.Empty<string>(),
                    Domains = resource.Domains ?? Array.Empty<string>(),
                    Indexes = resource.Indexes ?? Array.Empty<string>(),
                    Storage = NormalizeStorage(resource.Storage),
                    Capabilities = resource.Capabilities ?? Array.Empty<string>(),
                    SupportedFormats = resource.SupportedFormats ?? Array.Empty<string>(),
                };
        }

        return normalized;
    }

    private static MetadataScriptFieldContract[] NormalizeFields(
        IReadOnlyList<MetadataScriptFieldContract>? fields)
    {
        if (fields is null || fields.Count == 0)
        {
            return Array.Empty<MetadataScriptFieldContract>();
        }

        var normalized = new MetadataScriptFieldContract[fields.Count];
        for (var index = 0; index < fields.Count; index++)
        {
            var field = fields[index];
            normalized[index] = field is null
                ? null!
                : field with
                {
                    SemanticRoles = field.SemanticRoles ?? Array.Empty<string>(),
                    Domains = field.Domains ?? Array.Empty<string>(),
                    Indexes = field.Indexes ?? Array.Empty<string>(),
                };
        }

        return normalized;
    }

    private static MetadataScriptStorageContract? NormalizeStorage(MetadataScriptStorageContract? storage)
        => storage is null
            ? null
            : storage with
            {
                Capabilities = storage.Capabilities ?? Array.Empty<MetadataV2StorageBindingCapability>(),
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
