// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;
using Honua.Core.Features.Console.Domain;

namespace Honua.Core.Features.Metadata.Domain.V2;

/// <summary>
/// JSON serialization context for Metadata v2 release package contracts.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(MetadataSemanticInventoryResponse))]
[JsonSerializable(typeof(MetadataSemanticInventoryEntry))]
[JsonSerializable(typeof(MetadataEnvironmentBindingsRequest))]
[JsonSerializable(typeof(MetadataEnvironmentBindingsResponse))]
[JsonSerializable(typeof(MetadataEnvironmentBindingSummary))]
[JsonSerializable(typeof(MetadataBoundResourceSummary))]
[JsonSerializable(typeof(MetadataBoundFieldSummary))]
[JsonSerializable(typeof(MetadataBoundServiceSummary))]
[JsonSerializable(typeof(MetadataBoundPublicationSummary))]
[JsonSerializable(typeof(MetadataBoundStorageSummary))]
[JsonSerializable(typeof(MetadataBoundConnectionSummary))]
[JsonSerializable(typeof(CreateMetadataReleasePackageRequest))]
[JsonSerializable(typeof(MetadataReleasePackage))]
[JsonSerializable(typeof(MetadataReleaseEntry))]
[JsonSerializable(typeof(MetadataReleaseTargetState))]
[JsonSerializable(typeof(GitOpsMetadataReleaseManifest))]
[JsonSerializable(typeof(GitOpsMetadataReleaseSpec))]
[JsonSerializable(typeof(GitOpsMetadataReleaseSource))]
[JsonSerializable(typeof(GitOpsMetadataReleaseTarget))]
[JsonSerializable(typeof(GitOpsMetadataReleaseEntry))]
[JsonSerializable(typeof(MetadataV2EnvironmentRevision))]
[JsonSerializable(typeof(MetadataV2ObjectMetadata))]
[JsonSerializable(typeof(MetadataV2Status))]
[JsonSerializable(typeof(MetadataV2Condition))]
[JsonSerializable(typeof(ConsoleProvenanceRef))]
[JsonSerializable(typeof(MetadataReleaseEntry[]))]
[JsonSerializable(typeof(GitOpsMetadataReleaseEntry[]))]
[JsonSerializable(typeof(MetadataReleaseTargetState[]))]
[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(IReadOnlyDictionary<string, string>), TypeInfoPropertyName = "ReadOnlyDictionaryStringString")]
[JsonSerializable(typeof(IReadOnlyDictionary<string, JsonElement>), TypeInfoPropertyName = "ReadOnlyDictionaryStringJsonElement")]
public sealed partial class MetadataReleaseJsonContext : JsonSerializerContext
{
}
