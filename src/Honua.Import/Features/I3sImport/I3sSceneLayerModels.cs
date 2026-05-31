// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Import.Features.I3sImport;

/// <summary>
/// Minimal I3S 1.7 scene-layer descriptor (<c>3dSceneLayer.json</c>). Only the
/// fields the converter reads are modeled; other spec fields are ignored.
/// </summary>
internal sealed record I3sSceneLayer
{
    /// <summary>Layer type discriminator. Initial slice supports <c>3DObject</c>.</summary>
    [JsonPropertyName("layerType")]
    public string LayerType { get; init; } = string.Empty;

    /// <summary>Optional layer display name.</summary>
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    /// <summary>Optional layer description.</summary>
    [JsonPropertyName("description")]
    public string? Description { get; init; }

    /// <summary>Layer spatial reference (WKID + latest WKID).</summary>
    [JsonPropertyName("spatialReference")]
    public I3sSpatialReference? SpatialReference { get; init; }

    /// <summary>Layer extent in spatial reference units (xmin, ymin, xmax, ymax).</summary>
    [JsonPropertyName("fullExtent")]
    public I3sFullExtent? FullExtent { get; init; }

    /// <summary>Store descriptor (geometry schema, paths, profile).</summary>
    [JsonPropertyName("store")]
    public I3sStore? Store { get; init; }
}

/// <summary>I3S spatial reference.</summary>
internal sealed record I3sSpatialReference
{
    /// <summary>Authority code (e.g. 4326).</summary>
    [JsonPropertyName("wkid")]
    public int Wkid { get; init; }

    /// <summary>Latest equivalent authority code.</summary>
    [JsonPropertyName("latestWkid")]
    public int? LatestWkid { get; init; }

    /// <summary>Vertical CRS authority code, when present.</summary>
    [JsonPropertyName("vcsWkid")]
    public int? VcsWkid { get; init; }

    /// <summary>Latest equivalent vertical CRS authority code.</summary>
    [JsonPropertyName("latestVcsWkid")]
    public int? LatestVcsWkid { get; init; }
}

/// <summary>I3S layer extent metadata.</summary>
internal sealed record I3sFullExtent
{
    /// <summary>Minimum longitude (or x).</summary>
    [JsonPropertyName("xmin")]
    public double XMin { get; init; }

    /// <summary>Minimum latitude (or y).</summary>
    [JsonPropertyName("ymin")]
    public double YMin { get; init; }

    /// <summary>Maximum longitude (or x).</summary>
    [JsonPropertyName("xmax")]
    public double XMax { get; init; }

    /// <summary>Maximum latitude (or y).</summary>
    [JsonPropertyName("ymax")]
    public double YMax { get; init; }

    /// <summary>Optional minimum height.</summary>
    [JsonPropertyName("zmin")]
    public double? ZMin { get; init; }

    /// <summary>Optional maximum height.</summary>
    [JsonPropertyName("zmax")]
    public double? ZMax { get; init; }
}

/// <summary>I3S store descriptor.</summary>
internal sealed record I3sStore
{
    /// <summary>Store profile (<c>meshpyramids</c>, <c>points</c>, etc.).</summary>
    [JsonPropertyName("profile")]
    public string Profile { get; init; } = string.Empty;

    /// <summary>Default geometry schema describing the binary buffer layout.</summary>
    [JsonPropertyName("defaultGeometrySchema")]
    public I3sGeometrySchema? DefaultGeometrySchema { get; init; }

    /// <summary>Nodepage descriptor used by I3S 1.7+ compact node tree.</summary>
    [JsonPropertyName("nodePages")]
    public I3sNodePageOptions? NodePages { get; init; }

    /// <summary>Root node URI (legacy I3S &lt; 1.7 only).</summary>
    [JsonPropertyName("rootNode")]
    public string? RootNode { get; init; }

    /// <summary>Store version (when present).</summary>
    [JsonPropertyName("version")]
    public string? Version { get; init; }
}

/// <summary>Nodepage runtime options.</summary>
internal sealed record I3sNodePageOptions
{
    /// <summary>Maximum nodes per page (used to map node index → page index).</summary>
    [JsonPropertyName("nodesPerPage")]
    public int NodesPerPage { get; init; } = 64;

    /// <summary>Lod selection metric (typically <c>maxScreenThresholdSQ</c>).</summary>
    [JsonPropertyName("lodSelectionMetricType")]
    public string? LodSelectionMetricType { get; init; }
}

/// <summary>I3S geometry schema describing a binary geometry buffer layout.</summary>
internal sealed record I3sGeometrySchema
{
    /// <summary>Geometry type (<c>triangles</c>, <c>points</c>, etc.).</summary>
    [JsonPropertyName("geometryType")]
    public string GeometryType { get; init; } = string.Empty;

    /// <summary>Topology (<c>PerAttributeArray</c> or <c>Indexed</c>).</summary>
    [JsonPropertyName("topology")]
    public string Topology { get; init; } = string.Empty;

    /// <summary>Header fields written before the vertex buffer.</summary>
    [JsonPropertyName("header")]
    public I3sHeaderField[]? Header { get; init; }

    /// <summary>Attribute layout order in the buffer.</summary>
    [JsonPropertyName("ordering")]
    public string[]? Ordering { get; init; }

    /// <summary>Index buffer ordering (Indexed topology only).</summary>
    [JsonPropertyName("orderingIndices")]
    public string[]? OrderingIndices { get; init; }

    /// <summary>Vertex attribute definitions, keyed by attribute name.</summary>
    [JsonPropertyName("vertexAttributes")]
    public Dictionary<string, I3sVertexAttribute>? VertexAttributes { get; init; }

    /// <summary>Optional face attributes (Indexed topology only).</summary>
    [JsonPropertyName("faces")]
    public Dictionary<string, I3sVertexAttribute>? Faces { get; init; }
}

/// <summary>Geometry header field descriptor.</summary>
internal sealed record I3sHeaderField
{
    /// <summary>Property name (e.g. <c>vertexCount</c>, <c>faceCount</c>).</summary>
    [JsonPropertyName("property")]
    public string Property { get; init; } = string.Empty;

    /// <summary>Numeric type (e.g. <c>UInt32</c>).</summary>
    [JsonPropertyName("type")]
    public string Type { get; init; } = string.Empty;
}

/// <summary>I3S vertex attribute descriptor.</summary>
internal sealed record I3sVertexAttribute
{
    /// <summary>Numeric type (<c>Float32</c>, <c>UInt8</c>, etc.).</summary>
    [JsonPropertyName("valueType")]
    public string ValueType { get; init; } = string.Empty;

    /// <summary>Element width (1=SCALAR, 2=VEC2, 3=VEC3, 4=VEC4).</summary>
    [JsonPropertyName("valuesPerElement")]
    public int ValuesPerElement { get; init; }
}

/// <summary>One page of compact NodePage entries (I3S 1.7+).</summary>
internal sealed record I3sNodePage
{
    /// <summary>Nodes in this page, ordered by global node index.</summary>
    [JsonPropertyName("nodes")]
    public I3sNodePageEntry[] Nodes { get; init; } = [];
}

/// <summary>One node entry in a compact NodePage.</summary>
internal sealed record I3sNodePageEntry
{
    /// <summary>Global node index.</summary>
    [JsonPropertyName("index")]
    public int Index { get; init; }

    /// <summary>Parent node index (root node has no parent).</summary>
    [JsonPropertyName("parentIndex")]
    public int? ParentIndex { get; init; }

    /// <summary>Lod-selection threshold in screen-space (max-screen-threshold²).</summary>
    [JsonPropertyName("lodThreshold")]
    public double? LodThreshold { get; init; }

    /// <summary>Minimum bounding sphere [centerX, centerY, centerZ, radius] in spatial-reference units.</summary>
    [JsonPropertyName("obb")]
    public I3sOrientedBoundingBox? Obb { get; init; }

    /// <summary>Optional MBS array: [cx, cy, cz, r].</summary>
    [JsonPropertyName("mbs")]
    public double[]? Mbs { get; init; }

    /// <summary>Child node indices.</summary>
    [JsonPropertyName("children")]
    public int[]? Children { get; init; }

    /// <summary>Mesh descriptor (geometry + material resource pointers).</summary>
    [JsonPropertyName("mesh")]
    public I3sNodeMesh? Mesh { get; init; }
}

/// <summary>Oriented bounding box for an I3S node.</summary>
internal sealed record I3sOrientedBoundingBox
{
    /// <summary>Center [x, y, z] in spatial-reference units.</summary>
    [JsonPropertyName("center")]
    public double[]? Center { get; init; }

    /// <summary>Half-sizes [hx, hy, hz] in spatial-reference units.</summary>
    [JsonPropertyName("halfSize")]
    public double[]? HalfSize { get; init; }

    /// <summary>Quaternion [qx, qy, qz, qw] describing orientation.</summary>
    [JsonPropertyName("quaternion")]
    public double[]? Quaternion { get; init; }
}

/// <summary>Mesh descriptor referenced by a NodePage entry.</summary>
internal sealed record I3sNodeMesh
{
    /// <summary>Material resource pointer.</summary>
    [JsonPropertyName("material")]
    public I3sMeshResourceRef? Material { get; init; }

    /// <summary>Geometry resource pointer.</summary>
    [JsonPropertyName("geometry")]
    public I3sMeshResourceRef? Geometry { get; init; }

    /// <summary>Optional attribute-resource pointer.</summary>
    [JsonPropertyName("attribute")]
    public I3sMeshResourceRef? Attribute { get; init; }
}

/// <summary>Resource pointer into the .slpk per-node directory.</summary>
internal sealed record I3sMeshResourceRef
{
    /// <summary>Resource index (file basename in the per-node directory).</summary>
    [JsonPropertyName("resource")]
    public int Resource { get; init; }

    /// <summary>Geometry-definition index (selects layout from <c>geometryDefinitions</c>).</summary>
    [JsonPropertyName("definition")]
    public int? Definition { get; init; }

    /// <summary>Resource byte length (when known).</summary>
    [JsonPropertyName("resourceId")]
    public int? ResourceId { get; init; }
}
