// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Core.Features.Scene.Conversion;

/// <summary>
/// Minimal model of an Esri I3S <c>3dSceneLayer.json</c> document (the root
/// scene-layer descriptor, OGC Community Standard 19-008). Only the fields the
/// Honua converter needs to emit a 3D Tiles <c>tileset.json</c> are modeled;
/// unknown members are ignored. Field names match the I3S spec verbatim so a
/// document parsed from a public <c>.slpk</c> or SceneServer response binds
/// directly.
/// </summary>
public sealed class I3sSceneLayerDocument
{
    /// <summary>Integer layer id within the scene service (usually <c>0</c>).</summary>
    [JsonPropertyName("id")]
    public int Id { get; set; }

    /// <summary>Layer type: <c>3DObject</c>, <c>IntegratedMesh</c>, <c>Point</c>, etc.</summary>
    [JsonPropertyName("layerType")]
    public string? LayerType { get; set; }

    /// <summary>Human-readable layer name.</summary>
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    /// <summary>Optional layer description.</summary>
    [JsonPropertyName("description")]
    public string? Description { get; set; }

    /// <summary>I3S spec version string (e.g. <c>1.7</c>, <c>1.8</c>).</summary>
    [JsonPropertyName("version")]
    public string? Version { get; set; }

    /// <summary>Spatial reference of the layer's coordinates.</summary>
    [JsonPropertyName("spatialReference")]
    public I3sSpatialReference? SpatialReference { get; set; }

    /// <summary>
    /// Full geographic extent of the layer: <c>[xmin, ymin, xmax, ymax]</c> in
    /// the layer's horizontal spatial reference (WGS-84 degrees for geographic
    /// layers).
    /// </summary>
    [JsonPropertyName("fullExtent")]
    public I3sFullExtent? FullExtent { get; set; }

    /// <summary>
    /// Vertical coordinate model for the layer's heights (ellipsoidal or
    /// gravity-related). Required by the I3S spec for 3DObject/IntegratedMesh
    /// layers so clients position geometry vertically.
    /// </summary>
    [JsonPropertyName("heightModelInfo")]
    public I3sHeightModelInfo? HeightModelInfo { get; set; }

    /// <summary>Store block describing geometry/node-page layout.</summary>
    [JsonPropertyName("store")]
    public I3sStore? Store { get; set; }

    /// <summary>
    /// Per-attribute binary storage layout (OGC 19-008 <c>attributeStorageInfo</c>).
    /// Describes how feature attributes are encoded; it is descriptive metadata
    /// only and does not imply a fetchable attribute resource on this slice.
    /// </summary>
    [JsonPropertyName("attributeStorageInfo")]
    public IReadOnlyList<I3sAttributeStorageInfo>? AttributeStorageInfo { get; set; }

    /// <summary>
    /// Default geometry schema (OGC 19-008 <c>store.defaultGeometrySchema</c> is
    /// commonly surfaced at the layer level by 1.7 producers): the vertex/face
    /// layout a node's geometry buffer conforms to. Descriptive only.
    /// </summary>
    [JsonPropertyName("geometryDefinitions")]
    public IReadOnlyList<I3sGeometryDefinition>? GeometryDefinitions { get; set; }

    /// <summary>
    /// Material definitions referenced by the layer's geometry
    /// (OGC 19-008 <c>materialDefinitions</c>). Descriptive only.
    /// </summary>
    [JsonPropertyName("materialDefinitions")]
    public IReadOnlyList<I3sMaterialDefinition>? MaterialDefinitions { get; set; }

    /// <summary>
    /// Texture-set definitions describing the texture formats the layer's
    /// materials reference (OGC 19-008 <c>textureSetDefinitions</c>).
    /// Descriptive only.
    /// </summary>
    [JsonPropertyName("textureSetDefinitions")]
    public IReadOnlyList<I3sTextureSetDefinition>? TextureSetDefinitions { get; set; }
}

/// <summary>
/// I3S per-attribute storage descriptor (OGC 19-008 <c>attributeStorageInfo</c>).
/// Describes the binary encoding of a single feature attribute.
/// </summary>
public sealed class I3sAttributeStorageInfo
{
    /// <summary>Stable attribute key (matches the attribute's field index).</summary>
    [JsonPropertyName("key")]
    public string? Key { get; set; }

    /// <summary>Human-readable attribute name.</summary>
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    /// <summary>Ordered names of the binary value headers (e.g. <c>count</c>).</summary>
    [JsonPropertyName("ordering")]
    public IReadOnlyList<string>? Ordering { get; set; }

    /// <summary>Header value descriptors (property + value type).</summary>
    [JsonPropertyName("header")]
    public IReadOnlyList<I3sAttributeHeader>? Header { get; set; }

    /// <summary>
    /// Per-value byte-count buffer encoding descriptor for variable-length
    /// (string) attributes (OGC 19-008 <c>attributeByteCounts</c>). Present only
    /// for <c>String</c> fields, whose binary file carries a
    /// <c>UInt32</c> byte-count per value before the UTF-8 value blob so a client
    /// can index into the variable-length values; omitted for fixed-width fields.
    /// </summary>
    [JsonPropertyName("attributeByteCounts")]
    public I3sAttributeValues? AttributeByteCounts { get; set; }

    /// <summary>Value-buffer encoding descriptor.</summary>
    [JsonPropertyName("attributeValues")]
    public I3sAttributeValues? AttributeValues { get; set; }
}

/// <summary>I3S attribute-buffer header descriptor.</summary>
public sealed class I3sAttributeHeader
{
    /// <summary>Header property name (e.g. <c>count</c>).</summary>
    [JsonPropertyName("property")]
    public string? Property { get; set; }

    /// <summary>Value type (e.g. <c>UInt32</c>).</summary>
    [JsonPropertyName("valueType")]
    public string? ValueType { get; set; }
}

/// <summary>I3S attribute value-buffer encoding descriptor.</summary>
public sealed class I3sAttributeValues
{
    /// <summary>Element value type (e.g. <c>Float64</c>, <c>String</c>).</summary>
    [JsonPropertyName("valueType")]
    public string? ValueType { get; set; }

    /// <summary>Encoding (e.g. <c>UTF-8</c> for string attributes).</summary>
    [JsonPropertyName("encoding")]
    public string? Encoding { get; set; }

    /// <summary>Values-per-element (1 for scalars).</summary>
    [JsonPropertyName("valuesPerElement")]
    public int? ValuesPerElement { get; set; }
}

/// <summary>
/// I3S geometry-schema definition (OGC 19-008 <c>defaultGeometrySchema</c> /
/// <c>geometryDefinitions</c>): the vertex/face layout a node geometry buffer
/// conforms to. Descriptive metadata only.
/// </summary>
public sealed class I3sGeometryDefinition
{
    /// <summary>Geometry encodings the layer's nodes use.</summary>
    [JsonPropertyName("geometryBuffers")]
    public IReadOnlyList<I3sGeometryBuffer>? GeometryBuffers { get; set; }
}

/// <summary>I3S geometry-buffer descriptor (compressed / uncompressed layout).</summary>
public sealed class I3sGeometryBuffer
{
    /// <summary>Whether the buffer is compressed (e.g. <c>draco</c>).</summary>
    [JsonPropertyName("compressedAttributes")]
    public I3sCompressedAttributes? CompressedAttributes { get; set; }

    /// <summary>Uncompressed offset descriptor for the position stream.</summary>
    [JsonPropertyName("position")]
    public I3sVertexLayout? Position { get; set; }

    /// <summary>Uncompressed offset descriptor for the normal stream.</summary>
    [JsonPropertyName("normal")]
    public I3sVertexLayout? Normal { get; set; }

    /// <summary>Uncompressed offset descriptor for the UV0 stream.</summary>
    [JsonPropertyName("uv0")]
    public I3sVertexLayout? Uv0 { get; set; }
}

/// <summary>I3S compressed-attribute (Draco) descriptor.</summary>
public sealed class I3sCompressedAttributes
{
    /// <summary>Compression encoding (e.g. <c>draco</c>).</summary>
    [JsonPropertyName("encoding")]
    public string? Encoding { get; set; }

    /// <summary>Attribute streams carried in the compressed buffer.</summary>
    [JsonPropertyName("attributes")]
    public IReadOnlyList<string>? Attributes { get; set; }
}

/// <summary>I3S uncompressed geometry-attribute descriptor.</summary>
public sealed class I3sVertexLayout
{
    /// <summary>Component type (e.g. <c>Float32</c>).</summary>
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    /// <summary>Components per vertex (e.g. 3 for position).</summary>
    [JsonPropertyName("component")]
    public int? Component { get; set; }
}

/// <summary>
/// I3S material definition (OGC 19-008 <c>materialDefinitions</c>): a PBR
/// material the layer's geometry references. Descriptive metadata only.
/// </summary>
public sealed class I3sMaterialDefinition
{
    /// <summary>Metallic-roughness PBR block.</summary>
    [JsonPropertyName("pbrMetallicRoughness")]
    public I3sPbrMetallicRoughness? PbrMetallicRoughness { get; set; }

    /// <summary>Alpha mode (<c>OPAQUE</c>, <c>MASK</c>, <c>BLEND</c>).</summary>
    [JsonPropertyName("alphaMode")]
    public string? AlphaMode { get; set; }

    /// <summary>Whether the material is double-sided.</summary>
    [JsonPropertyName("doubleSided")]
    public bool? DoubleSided { get; set; }
}

/// <summary>I3S PBR metallic-roughness descriptor.</summary>
public sealed class I3sPbrMetallicRoughness
{
    /// <summary>Linear-space base color factor (RGBA).</summary>
    [JsonPropertyName("baseColorFactor")]
    public IReadOnlyList<double>? BaseColorFactor { get; set; }

    /// <summary>Metalness factor.</summary>
    [JsonPropertyName("metallicFactor")]
    public double? MetallicFactor { get; set; }

    /// <summary>Roughness factor.</summary>
    [JsonPropertyName("roughnessFactor")]
    public double? RoughnessFactor { get; set; }
}

/// <summary>
/// I3S texture-set definition (OGC 19-008 <c>textureSetDefinitions</c>):
/// the texture formats the layer's materials reference. Descriptive only.
/// </summary>
public sealed class I3sTextureSetDefinition
{
    /// <summary>Texture format entries (e.g. jpg, ktx2).</summary>
    [JsonPropertyName("formats")]
    public IReadOnlyList<I3sTextureFormat>? Formats { get; set; }
}

/// <summary>I3S texture-format descriptor.</summary>
public sealed class I3sTextureFormat
{
    /// <summary>Texture file name token (e.g. <c>0</c>).</summary>
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    /// <summary>Texture encoding format (e.g. <c>jpg</c>, <c>ktx2</c>).</summary>
    [JsonPropertyName("format")]
    public string? Format { get; set; }
}

/// <summary>
/// I3S height-model block describing how layer heights are interpreted
/// vertically (OGC 19-008 <c>heightModelInfo</c>).
/// </summary>
public sealed class I3sHeightModelInfo
{
    /// <summary>Height model: <c>ellipsoidal</c> or <c>gravity_related_height</c>.</summary>
    [JsonPropertyName("heightModel")]
    public string? HeightModel { get; set; }

    /// <summary>Vertical coordinate system unit, e.g. <c>meter</c>.</summary>
    [JsonPropertyName("vertCRS")]
    public string? VertCrs { get; set; }

    /// <summary>Height unit, e.g. <c>meter</c>.</summary>
    [JsonPropertyName("heightUnit")]
    public string? HeightUnit { get; set; }
}

/// <summary>I3S spatial-reference block (WKID-based).</summary>
public sealed class I3sSpatialReference
{
    /// <summary>Horizontal coordinate system well-known id (e.g. <c>4326</c>).</summary>
    [JsonPropertyName("wkid")]
    public int Wkid { get; set; }

    /// <summary>Optional latest WKID alias.</summary>
    [JsonPropertyName("latestWkid")]
    public int? LatestWkid { get; set; }

    /// <summary>Optional vertical coordinate system well-known id.</summary>
    [JsonPropertyName("vcsWkid")]
    public int? VcsWkid { get; set; }
}

/// <summary>
/// I3S full-extent block. <c>zmin</c>/<c>zmax</c> are optional elevations in
/// meters; when absent the converter treats the vertical extent as zero.
/// </summary>
public sealed class I3sFullExtent
{
    /// <summary>Minimum x (longitude for geographic layers).</summary>
    [JsonPropertyName("xmin")]
    public double Xmin { get; set; }

    /// <summary>Minimum y (latitude for geographic layers).</summary>
    [JsonPropertyName("ymin")]
    public double Ymin { get; set; }

    /// <summary>Maximum x (longitude for geographic layers).</summary>
    [JsonPropertyName("xmax")]
    public double Xmax { get; set; }

    /// <summary>Maximum y (latitude for geographic layers).</summary>
    [JsonPropertyName("ymax")]
    public double Ymax { get; set; }

    /// <summary>Optional minimum elevation in meters.</summary>
    [JsonPropertyName("zmin")]
    public double? Zmin { get; set; }

    /// <summary>Optional maximum elevation in meters.</summary>
    [JsonPropertyName("zmax")]
    public double? Zmax { get; set; }

    /// <summary>Spatial reference of the extent (may differ from the layer).</summary>
    [JsonPropertyName("spatialReference")]
    public I3sSpatialReference? SpatialReference { get; set; }
}

/// <summary>I3S store block: geometry/node-page layout descriptor.</summary>
public sealed class I3sStore
{
    /// <summary>Store id.</summary>
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    /// <summary>Store profile, e.g. <c>meshpyramids</c>, <c>points</c>.</summary>
    [JsonPropertyName("profile")]
    public string? Profile { get; set; }

    /// <summary>Relative path to the root node (legacy I3S 1.6 node trees).</summary>
    [JsonPropertyName("rootNode")]
    public string? RootNode { get; set; }

    /// <summary>Store version string.</summary>
    [JsonPropertyName("version")]
    public string? Version { get; set; }

    /// <summary>
    /// Normal reference frame for vertex normals (I3S 1.7
    /// <c>store.normalReferenceFrame</c>): <c>east-north-up</c> for geographic
    /// node stores.
    /// </summary>
    [JsonPropertyName("normalReferenceFrame")]
    public string? NormalReferenceFrame { get; set; }

    /// <summary>
    /// Node-page pagination descriptor (I3S 1.7 <c>store.nodePages</c>): the
    /// fixed number of nodes carried per page plus the LOD selection metric. Its
    /// presence tells a conformant client to traverse the layer through
    /// <c>nodepages/{n}</c> rather than a legacy node tree.
    /// </summary>
    [JsonPropertyName("nodePages")]
    public I3sNodePageDefinition? NodePages { get; set; }

    /// <summary>
    /// Default geometry schema the node geometry buffers conform to (I3S 1.7
    /// <c>store.defaultGeometrySchema</c>).
    /// </summary>
    [JsonPropertyName("defaultGeometrySchema")]
    public I3sGeometryDefinition? DefaultGeometrySchema { get; set; }
}

/// <summary>
/// I3S node-page pagination descriptor (OGC 19-008 <c>store.nodePages</c>).
/// </summary>
public sealed class I3sNodePageDefinition
{
    /// <summary>Fixed number of node entries carried per node page.</summary>
    [JsonPropertyName("nodesPerPage")]
    public int NodesPerPage { get; set; }

    /// <summary>
    /// LOD selection metric the node <c>lodThreshold</c> values are expressed
    /// in (e.g. <c>maxScreenThresholdSQ</c>).
    /// </summary>
    [JsonPropertyName("lodSelectionMetricType")]
    public string? LodSelectionMetricType { get; set; }
}

/// <summary>
/// Source-generated JSON context for parsing I3S scene-layer documents.
/// AOT-safe, reflection-free.
/// </summary>
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
