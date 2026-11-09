// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Scene.Domain;
using Honua.Core.Serialization;

namespace Honua.Core.Tests.Features.Metadata.Domain.V2;

[JsonSourceGenerationOptions(Converters = new[]
{
    typeof(TolerantEnumJsonConverter<MetadataV2ResourceType>),
    typeof(TolerantEnumJsonConverter<MetadataV2ConnectionType>),
    typeof(TolerantEnumJsonConverter<MetadataV2StorageType>),
    typeof(TolerantEnumJsonConverter<MetadataV2ServiceType>),
    typeof(TolerantEnumJsonConverter<MetadataV2LayerType>),
    typeof(TolerantEnumJsonConverter<MetadataV2StorageBindingCapability>),
    typeof(TolerantEnumJsonConverter<MetadataV2GeometryType>),
    typeof(TolerantEnumJsonConverter<MetadataV2FieldType>),
    typeof(TolerantNumericEnumJsonConverter<MetadataV2AttributeRuleType>),
    typeof(TolerantNumericEnumJsonConverter<Symbology3DComparison>)
})]
[JsonSerializable(typeof(MetadataV2ResourceType))]
[JsonSerializable(typeof(MetadataV2ConnectionType))]
[JsonSerializable(typeof(MetadataV2StorageType))]
[JsonSerializable(typeof(MetadataV2ServiceType))]
[JsonSerializable(typeof(MetadataV2LayerType))]
[JsonSerializable(typeof(MetadataV2StorageBindingCapability))]
[JsonSerializable(typeof(MetadataV2GeometryType))]
[JsonSerializable(typeof(MetadataV2FieldType))]
[JsonSerializable(typeof(MetadataV2AttributeRuleType))]
[JsonSerializable(typeof(Symbology3DComparison))]
internal sealed partial class MetadataV2EnumTestJsonContext : JsonSerializerContext
{
}
