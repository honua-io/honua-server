// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Core.Features.Metadata.Domain.V2;

/// <summary>
/// JSON serialization context for Metadata v2 compatibility prevalidation contracts.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(MetadataCompatibilityPrevalidationRequest))]
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
[JsonSerializable(typeof(MetadataV2ServiceType))]
[JsonSerializable(typeof(MetadataV2LifecycleStatus))]
[JsonSerializable(typeof(MetadataV2OperationalState))]
[JsonSerializable(typeof(MetadataCompatibilityFinding[]))]
[JsonSerializable(typeof(MetadataAffectedDependent[]))]
[JsonSerializable(typeof(MetadataDataScriptEntry[]))]
[JsonSerializable(typeof(MetadataScriptResourceContract[]))]
[JsonSerializable(typeof(MetadataScriptFieldContract[]))]
[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(IReadOnlyDictionary<string, string>), TypeInfoPropertyName = "ReadOnlyDictionaryStringString")]
public sealed partial class MetadataCompatibilityJsonContext : JsonSerializerContext
{
}
