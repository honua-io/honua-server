// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Core.Features.Metadata.Domain.V2;

/// <summary>
/// JSON serialization context for Metadata v2 domain models.
/// </summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(MetadataV2Graph))]
[JsonSerializable(typeof(MetadataV2Catalog))]
[JsonSerializable(typeof(MetadataV2Resource))]
[JsonSerializable(typeof(MetadataV2Connection))]
[JsonSerializable(typeof(MetadataV2StorageBinding))]
[JsonSerializable(typeof(MetadataV2Service))]
[JsonSerializable(typeof(MetadataV2Publication))]
[JsonSerializable(typeof(MetadataV2ProjectionProfile))]
[JsonSerializable(typeof(MetadataV2Policy))]
[JsonSerializable(typeof(MetadataV2Role))]
[JsonSerializable(typeof(MetadataV2RuntimeSnapshot))]
[JsonSerializable(typeof(MetadataV2ObjectMetadata))]
[JsonSerializable(typeof(MetadataV2Status))]
[JsonSerializable(typeof(MetadataV2Condition))]
[JsonSerializable(typeof(MetadataV2ExtensionPoint))]
[JsonSerializable(typeof(MetadataV2Field))]
[JsonSerializable(typeof(MetadataV2Relationship))]
[JsonSerializable(typeof(MetadataV2SpatialReference))]
[JsonSerializable(typeof(MetadataV2Bbox))]
[JsonSerializable(typeof(MetadataV2ResourceSpatial))]
[JsonSerializable(typeof(MetadataV2TimeRange))]
[JsonSerializable(typeof(MetadataV2ResourceTemporal))]
[JsonSerializable(typeof(IReadOnlyDictionary<string, string>), TypeInfoPropertyName = "ReadOnlyDictionaryStringString")]
public sealed partial class MetadataV2JsonContext : JsonSerializerContext
{
}
