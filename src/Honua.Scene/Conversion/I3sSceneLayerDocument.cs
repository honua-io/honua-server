// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Core.Features.Scene.Conversion;

public sealed class I3sSceneLayerDocument
{
    [JsonPropertyName("fields")]
    public IReadOnlyList<I3sField>? Fields { get; set; }

    [JsonPropertyName("objectIdField")]
    public string? ObjectIdField { get; set; }
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("layerType")]
    public string? LayerType { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("version")]
    public string? Version { get; set; }

    [JsonPropertyName("capabilities")]
    public IReadOnlyList<string>? Capabilities { get; set; }

    [JsonPropertyName("spatialReference")]
    public I3sSpatialReference? SpatialReference { get; set; }

    [JsonPropertyName("fullExtent")]
    public I3sFullExtent? FullExtent { get; set; }

    [JsonPropertyName("heightModelInfo")]
    public I3sHeightModelInfo? HeightModelInfo { get; set; }

    [JsonPropertyName("store")]
    public I3sStore? Store { get; set; }

    [JsonPropertyName("attributeStorageInfo")]
    public IReadOnlyList<I3sAttributeStorageInfo>? AttributeStorageInfo { get; set; }

    [JsonPropertyName("nodePages")]
    public I3sNodePageDefinition? NodePages { get; set; }

    [JsonPropertyName("geometryDefinitions")]
    public IReadOnlyList<I3sGeometryDefinition>? GeometryDefinitions { get; set; }

    [JsonPropertyName("materialDefinitions")]
    public IReadOnlyList<I3sMaterialDefinition>? MaterialDefinitions { get; set; }

    [JsonPropertyName("textureSetDefinitions")]
    public IReadOnlyList<I3sTextureSetDefinition>? TextureSetDefinitions { get; set; }
}

public sealed class I3sAttributeStorageInfo
{
    [JsonPropertyName("key")]
    public string? Key { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("ordering")]
    public IReadOnlyList<string>? Ordering { get; set; }

    [JsonPropertyName("header")]
    public IReadOnlyList<I3sAttributeHeader>? Header { get; set; }

    [JsonPropertyName("attributeByteCounts")]
    public I3sAttributeValues? AttributeByteCounts { get; set; }

    [JsonPropertyName("attributeValues")]
    public I3sAttributeValues? AttributeValues { get; set; }
}

public sealed class I3sAttributeHeader
{
    [JsonPropertyName("property")]
    public string? Property { get; set; }

    [JsonPropertyName("valueType")]
    public string? ValueType { get; set; }
}

public sealed class I3sAttributeValues
{
    [JsonPropertyName("valueType")]
    public string? ValueType { get; set; }

    [JsonPropertyName("encoding")]
    public string? Encoding { get; set; }

    [JsonPropertyName("valuesPerElement")]
    public int? ValuesPerElement { get; set; }
}

public sealed class I3sGeometryDefinition
{
    [JsonPropertyName("topology")]
    public string? Topology { get; set; }

    [JsonPropertyName("geometryBuffers")]
    public IReadOnlyList<I3sGeometryBuffer>? GeometryBuffers { get; set; }
}

public sealed class I3sGeometryBuffer
{
    [JsonPropertyName("offset")]
    public int? Offset { get; set; }

    [JsonPropertyName("compressedAttributes")]
    public I3sCompressedAttributes? CompressedAttributes { get; set; }

    [JsonPropertyName("position")]
    public I3sVertexLayout? Position { get; set; }

    [JsonPropertyName("normal")]
    public I3sVertexLayout? Normal { get; set; }

    [JsonPropertyName("uv0")]
    public I3sVertexLayout? Uv0 { get; set; }

    [JsonPropertyName("uv1")]
    public I3sVertexLayout? Uv1 { get; set; }

    [JsonPropertyName("color")]
    public I3sVertexLayout? Color { get; set; }

    [JsonPropertyName("featureId")]
    public I3sVertexLayout? FeatureId { get; set; }

    [JsonPropertyName("faceRange")]
    public I3sVertexLayout? FaceRange { get; set; }
}

public sealed class I3sCompressedAttributes
{
    [JsonPropertyName("encoding")]
    public string? Encoding { get; set; }

    [JsonPropertyName("attributes")]
    public IReadOnlyList<string>? Attributes { get; set; }
}

public sealed class I3sVertexLayout
{
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("component")]
    public int? Component { get; set; }

    [JsonPropertyName("binding")]
    public string? Binding { get; set; }
}

public sealed class I3sDefaultGeometrySchema
{
    [JsonPropertyName("geometryType")]
    public string? GeometryType { get; set; }

    [JsonPropertyName("header")]
    public IReadOnlyList<I3sGeometryHeader>? Header { get; set; }

    [JsonPropertyName("topology")]
    public string? Topology { get; set; }

    [JsonPropertyName("ordering")]
    public IReadOnlyList<string>? Ordering { get; set; }

    [JsonPropertyName("vertexAttributes")]
    public IReadOnlyDictionary<string, I3sAttributeValues>? VertexAttributes { get; set; }

    [JsonPropertyName("featureAttributeOrder")]
    public IReadOnlyList<string>? FeatureAttributeOrder { get; set; }

    [JsonPropertyName("featureAttributes")]
    public IReadOnlyDictionary<string, I3sAttributeValues>? FeatureAttributes { get; set; }
}

public sealed class I3sGeometryHeader
{
    [JsonPropertyName("property")]
    public string? Property { get; set; }

    [JsonPropertyName("type")]
    public string? Type { get; set; }
}

public sealed class I3sMaterialDefinition
{
    [JsonPropertyName("cullFace")]
    public string? CullFace { get; set; }

    [JsonPropertyName("normalTexture")]
    public I3sMaterialTexture? NormalTexture { get; set; }

    [JsonPropertyName("occlusionTexture")]
    public I3sMaterialTexture? OcclusionTexture { get; set; }

    [JsonPropertyName("emissiveTexture")]
    public I3sMaterialTexture? EmissiveTexture { get; set; }

    [JsonPropertyName("emissiveFactor")]
    public IReadOnlyList<double>? EmissiveFactor { get; set; }

    [JsonPropertyName("alphaCutoff")]
    public double? AlphaCutoff { get; set; }
    [JsonPropertyName("pbrMetallicRoughness")]
    public I3sPbrMetallicRoughness? PbrMetallicRoughness { get; set; }

    [JsonPropertyName("alphaMode")]
    public string? AlphaMode { get; set; }

    [JsonPropertyName("doubleSided")]
    public bool? DoubleSided { get; set; }
}

public sealed class I3sPbrMetallicRoughness
{
    [JsonPropertyName("metallicRoughnessTexture")]
    public I3sMaterialTexture? MetallicRoughnessTexture { get; set; }
    [JsonPropertyName("baseColorTexture")]
    public I3sMaterialTexture? BaseColorTexture { get; set; }
    [JsonPropertyName("baseColorFactor")]
    public IReadOnlyList<double>? BaseColorFactor { get; set; }

    [JsonPropertyName("metallicFactor")]
    public double? MetallicFactor { get; set; }

    [JsonPropertyName("roughnessFactor")]
    public double? RoughnessFactor { get; set; }
}

public sealed class I3sTextureSetDefinition
{
    [JsonPropertyName("formats")]
    public IReadOnlyList<I3sTextureFormat>? Formats { get; set; }
}

public sealed class I3sTextureFormat
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("format")]
    public string? Format { get; set; }
}

public sealed class I3sHeightModelInfo
{
    [JsonPropertyName("heightModel")]
    public string? HeightModel { get; set; }

    [JsonPropertyName("vertCRS")]
    public string? VertCrs { get; set; }

    [JsonPropertyName("heightUnit")]
    public string? HeightUnit { get; set; }
}

public sealed class I3sSpatialReference
{
    [JsonPropertyName("wkid")]
    public int Wkid { get; set; }

    [JsonPropertyName("latestWkid")]
    public int? LatestWkid { get; set; }

    [JsonPropertyName("vcsWkid")]
    public int? VcsWkid { get; set; }
}

public sealed class I3sFullExtent
{
    [JsonPropertyName("xmin")]
    public double Xmin { get; set; }

    [JsonPropertyName("ymin")]
    public double Ymin { get; set; }

    [JsonPropertyName("xmax")]
    public double Xmax { get; set; }

    [JsonPropertyName("ymax")]
    public double Ymax { get; set; }

    [JsonPropertyName("zmin")]
    public double? Zmin { get; set; }

    [JsonPropertyName("zmax")]
    public double? Zmax { get; set; }

    [JsonPropertyName("spatialReference")]
    public I3sSpatialReference? SpatialReference { get; set; }
}

public sealed class I3sStore
{
    [JsonPropertyName("indexCRS")]
    public string? IndexCrs { get; set; }

    [JsonPropertyName("vertexCRS")]
    public string? VertexCrs { get; set; }
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("profile")]
    public string? Profile { get; set; }

    [JsonPropertyName("rootNode")]
    public string? RootNode { get; set; }

    [JsonPropertyName("version")]
    public string? Version { get; set; }

    [JsonPropertyName("normalReferenceFrame")]
    public string? NormalReferenceFrame { get; set; }

    [JsonPropertyName("defaultGeometrySchema")]
    public I3sDefaultGeometrySchema? DefaultGeometrySchema { get; set; }
}

public sealed class I3sField
{
    [JsonPropertyName("name")]
    public required string Name { get; set; }

    [JsonPropertyName("type")]
    public required string Type { get; set; }

    [JsonPropertyName("alias")]
    public required string Alias { get; set; }
}

public sealed class I3sMaterialTexture
{
    [JsonPropertyName("texCoord")]
    public int TexCoord { get; set; }

    [JsonPropertyName("factor")]
    public double? Factor { get; set; }
    [JsonPropertyName("textureSetDefinitionId")]
    public int TextureSetDefinitionId { get; set; }
}

public sealed class I3sNodePageDefinition
{
    [JsonPropertyName("nodesPerPage")]
    public int NodesPerPage { get; set; }

    [JsonPropertyName("lodSelectionMetricType")]
    public string? LodSelectionMetricType { get; set; }
}

[JsonSourceGenerationOptions(
    PropertyNameCaseInsensitive = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(I3sSceneLayerDocument))]
[JsonSerializable(typeof(I3sSpatialReference))]
[JsonSerializable(typeof(I3sFullExtent))]
[JsonSerializable(typeof(I3sHeightModelInfo))]
[JsonSerializable(typeof(I3sStore))]
[JsonSerializable(typeof(I3sAttributeStorageInfo))]
[JsonSerializable(typeof(I3sGeometryDefinition))]
[JsonSerializable(typeof(I3sMaterialDefinition))]
[JsonSerializable(typeof(I3sTextureSetDefinition))]
public sealed partial class I3sSceneLayerJsonContext : System.Text.Json.Serialization.JsonSerializerContext
{
}
