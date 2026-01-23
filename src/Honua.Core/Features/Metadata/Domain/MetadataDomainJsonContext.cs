// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Core.Features.Metadata.Domain;

/// <summary>
/// JSON serialization context for metadata resource domain models.
/// </summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(MetadataResource))]
[JsonSerializable(typeof(ResourceMetadata))]
[JsonSerializable(typeof(MetadataResourceIdentifier))]
[JsonSerializable(typeof(CompiledMetadataArtifact))]
[JsonSerializable(typeof(Dictionary<string, string>), TypeInfoPropertyName = "DictionaryStringString")]
public sealed partial class MetadataDomainJsonContext : JsonSerializerContext
{
}
