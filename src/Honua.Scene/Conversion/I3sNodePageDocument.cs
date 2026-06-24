// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Core.Features.Scene.Conversion;

/// <summary>
/// An Esri I3S 1.7 node page (OGC 19-008 <c>nodepages/{n}.json</c>): a fixed-size
/// window of HLOD nodes projected from the 3D Tiles tree. Each node carries its
/// minimum bounding sphere (<c>obb</c>), an LOD threshold derived from the source
/// geometric error, parent/child references by global node index, and the
/// per-resource (mesh geometry / attribute / texture) reference indices a client
/// uses to fetch node content.
/// </summary>
public sealed class I3sNodePageDocument
{
    /// <summary>The node entries carried on this page (root-first global order).</summary>
    [JsonPropertyName("nodes")]
    public IReadOnlyList<I3sNodePageEntry> Nodes { get; set; } = [];
}

/// <summary>
/// A single I3S 1.7 node-page entry (OGC 19-008 <c>nodePage.nodes[]</c>).
/// </summary>
public sealed class I3sNodePageEntry
{
    /// <summary>
    /// LOD selection threshold for this node, in the metric declared by
    /// <c>store.nodePages.lodSelectionMetricType</c>. Higher thresholds refine to
    /// finer LODs; the root carries 0 (always selectable).
    /// </summary>
    [JsonPropertyName("lodThreshold")]
    public double LodThreshold { get; set; }

    /// <summary>
    /// Global index of this node's parent within the flattened node array, or
    /// omitted for the root node.
    /// </summary>
    [JsonPropertyName("parentIndex")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? ParentIndex { get; set; }

    /// <summary>
    /// Global indices of this node's children within the flattened node array.
    /// Empty for leaf nodes.
    /// </summary>
    [JsonPropertyName("children")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<int>? Children { get; set; }

    /// <summary>
    /// Oriented bounding box (I3S <c>obb</c>): centre in the layer's index CRS
    /// (WGS-84 lon/lat/elevation), half-sizes in metres, and an orientation
    /// quaternion. Honua emits an axis-aligned box (identity quaternion) derived
    /// from the source region bounding volume.
    /// </summary>
    [JsonPropertyName("obb")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public I3sOrientedBoundingBox? Obb { get; set; }

    /// <summary>
    /// Reference to this node's mesh geometry / attribute / texture resources by
    /// index. Present only on nodes that carry renderable content.
    /// </summary>
    [JsonPropertyName("mesh")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public I3sNodeMesh? Mesh { get; set; }
}

/// <summary>
/// I3S oriented bounding box (OGC 19-008 <c>obb</c>).
/// </summary>
public sealed class I3sOrientedBoundingBox
{
    /// <summary>Centre <c>[x, y, z]</c> in the layer index CRS (lon, lat, elevation m).</summary>
    [JsonPropertyName("center")]
    public IReadOnlyList<double> Center { get; set; } = [];

    /// <summary>Half-sizes <c>[hx, hy, hz]</c> in metres.</summary>
    [JsonPropertyName("halfSize")]
    public IReadOnlyList<double> HalfSize { get; set; } = [];

    /// <summary>Orientation quaternion <c>[x, y, z, w]</c>; identity for axis-aligned boxes.</summary>
    [JsonPropertyName("quaternion")]
    public IReadOnlyList<double> Quaternion { get; set; } = [];
}

/// <summary>
/// I3S 1.7 node mesh reference block (OGC 19-008 <c>node.mesh</c>): binds a node
/// to its geometry / attribute / material resources by reference index.
/// </summary>
public sealed class I3sNodeMesh
{
    /// <summary>Geometry resource reference (definition + resource id).</summary>
    [JsonPropertyName("geometry")]
    public I3sNodeResourceReference Geometry { get; set; } = new();

    /// <summary>Attribute resource reference (per-field attribute files).</summary>
    [JsonPropertyName("attribute")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public I3sNodeResourceReference? Attribute { get; set; }

    /// <summary>Material/texture resource reference.</summary>
    [JsonPropertyName("material")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public I3sNodeResourceReference? Material { get; set; }
}

/// <summary>
/// I3S 1.7 node resource reference (OGC 19-008): a definition index plus the
/// per-node resource id a client substitutes into the resource URL template.
/// </summary>
public sealed class I3sNodeResourceReference
{
    /// <summary>
    /// Index into the layer's definition array (geometry/material/attribute
    /// definition the resource conforms to). Honua serves a single definition,
    /// so this is always 0.
    /// </summary>
    [JsonPropertyName("definition")]
    public int Definition { get; set; }

    /// <summary>
    /// Per-node resource id substituted into the node resource URL
    /// (<c>nodes/{resource}/geometries/0</c>). Maps to the global node index.
    /// </summary>
    [JsonPropertyName("resource")]
    public int Resource { get; set; }
}

/// <summary>
/// Source-generated JSON context for serving I3S node pages. AOT-safe.
/// </summary>
[JsonSourceGenerationOptions(
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(I3sNodePageDocument))]
[JsonSerializable(typeof(I3sNodePageEntry))]
[JsonSerializable(typeof(I3sOrientedBoundingBox))]
[JsonSerializable(typeof(I3sNodeMesh))]
[JsonSerializable(typeof(I3sNodeResourceReference))]
public sealed partial class I3sNodePageJsonContext : JsonSerializerContext
{
}
