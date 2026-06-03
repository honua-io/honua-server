// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;
using Honua.Core.Features.Scene.Domain;

namespace Honua.Core.Features.Metadata.Domain.V2;

/// <summary>
/// JSON serialization context for Metadata v2 domain models.
/// </summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(MetadataV2Graph))]
[JsonSerializable(typeof(MetadataV2Resource))]
[JsonSerializable(typeof(MetadataV2ResourceDisplay))]
[JsonSerializable(typeof(MetadataV2ResourceEditing))]
[JsonSerializable(typeof(MetadataV2ResourceStyle))]
[JsonSerializable(typeof(MetadataV2StyleEncoding))]
[JsonSerializable(typeof(MetadataV2Connection))]
[JsonSerializable(typeof(MetadataV2StorageBinding))]
[JsonSerializable(typeof(MetadataV2Service))]
[JsonSerializable(typeof(MetadataV2ServiceSettings))]
[JsonSerializable(typeof(MetadataV2Publication))]
[JsonSerializable(typeof(MetadataV2Catalog))]
[JsonSerializable(typeof(MetadataV2ProjectionProfile))]
[JsonSerializable(typeof(MetadataV2Policy))]
[JsonSerializable(typeof(MetadataV2Role))]
[JsonSerializable(typeof(MetadataV2RuntimeSnapshot))]
[JsonSerializable(typeof(MetadataV2ObjectMetadata))]
[JsonSerializable(typeof(MetadataV2Status))]
[JsonSerializable(typeof(MetadataV2Condition))]
[JsonSerializable(typeof(MetadataV2ExtensionPoint))]
[JsonSerializable(typeof(MetadataV2ContactPoint))]
[JsonSerializable(typeof(MetadataV2Link))]
[JsonSerializable(typeof(MetadataV2Field))]
[JsonSerializable(typeof(MetadataV2FieldDomain))]
[JsonSerializable(typeof(MetadataV2CodedValue))]
[JsonSerializable(typeof(MetadataV2Subtypes))]
[JsonSerializable(typeof(MetadataV2Subtype))]
[JsonSerializable(typeof(MetadataV2SubtypeFieldOverride))]
[JsonSerializable(typeof(MetadataV2Relationship))]
[JsonSerializable(typeof(MetadataV2PublicationIdentifier))]
[JsonSerializable(typeof(MetadataV2SpatialReference))]
[JsonSerializable(typeof(MetadataV2Bbox))]
[JsonSerializable(typeof(MetadataV2ResourceSpatial))]
[JsonSerializable(typeof(MetadataV2TimeRange))]
[JsonSerializable(typeof(MetadataV2ResourceTemporal))]
[JsonSerializable(typeof(MetadataV2PermanentFilter))]
[JsonSerializable(typeof(MetadataV2ExtrusionInfo))]
[JsonSerializable(typeof(Symbology3D))]
[JsonSerializable(typeof(Symbology3DRule))]
[JsonSerializable(typeof(Symbology3DColor))]
[JsonSerializable(typeof(IReadOnlyDictionary<string, string>), TypeInfoPropertyName = "ReadOnlyDictionaryStringString")]
public sealed partial class MetadataV2JsonContext : JsonSerializerContext
{
}
