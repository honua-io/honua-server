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

    /// <summary>Store block describing geometry/node-page layout.</summary>
    [JsonPropertyName("store")]
    public I3sStore? Store { get; set; }
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
[JsonSerializable(typeof(I3sStore))]
public sealed partial class I3sSceneLayerJsonContext : System.Text.Json.Serialization.JsonSerializerContext
{
}
