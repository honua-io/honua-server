// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Ai.Protocols.Mcp.MapTools;

// -----------------------------------------------------------------------
// honua_list_layers
// -----------------------------------------------------------------------

/// <summary>
/// Arguments for <c>honua_list_layers</c>. The optional <see cref="Filter"/>
/// substring narrows the published catalog by service or layer name/title.
/// </summary>
internal sealed class McpListLayersArgument
{
    [JsonPropertyName("filter")]
    public string? Filter { get; set; }
}

/// <summary>
/// Output for <c>honua_list_layers</c>: the published services and the layers
/// they expose, projected from the canonical Metadata v2 graph.
/// </summary>
internal sealed class McpListLayersOutput
{
    [JsonPropertyName("layerCount")]
    public int LayerCount { get; set; }

    [JsonPropertyName("layers")]
    public IReadOnlyList<McpLayerSummary> Layers { get; set; } = [];
}

/// <summary>
/// One published layer entry. <see cref="ServiceId"/> + <see cref="LayerId"/>
/// are the addressing tuple consumed by <c>honua_query_features</c> and
/// <c>honua_render_map</c>.
/// </summary>
internal sealed class McpLayerSummary
{
    [JsonPropertyName("serviceId")]
    public string ServiceId { get; set; } = string.Empty;

    [JsonPropertyName("serviceName")]
    public string ServiceName { get; set; } = string.Empty;

    [JsonPropertyName("layerId")]
    public int LayerId { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("geometryType")]
    public string GeometryType { get; set; } = string.Empty;

    [JsonPropertyName("srid")]
    public int? Srid { get; set; }

    [JsonPropertyName("extent")]
    public McpExtent? Extent { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }
}

/// <summary>
/// Axis-aligned bounding box in the order GeoJSON/OGC expect:
/// <c>[minX, minY, maxX, maxY]</c>.
/// </summary>
internal sealed class McpExtent
{
    [JsonPropertyName("minX")]
    public double MinX { get; set; }

    [JsonPropertyName("minY")]
    public double MinY { get; set; }

    [JsonPropertyName("maxX")]
    public double MaxX { get; set; }

    [JsonPropertyName("maxY")]
    public double MaxY { get; set; }
}

// -----------------------------------------------------------------------
// honua_query_features
// -----------------------------------------------------------------------

/// <summary>
/// Arguments for <c>honua_query_features</c>.
/// </summary>
internal sealed class McpQueryFeaturesArgument
{
    [JsonPropertyName("serviceId")]
    public string? ServiceId { get; set; }

    [JsonPropertyName("layerId")]
    public int? LayerId { get; set; }

    [JsonPropertyName("where")]
    public string? Where { get; set; }

    /// <summary>Bounding box filter as <c>[minX, minY, maxX, maxY]</c>.</summary>
    [JsonPropertyName("bbox")]
    public IReadOnlyList<double>? Bbox { get; set; }

    [JsonPropertyName("bboxSrid")]
    public int? BboxSrid { get; set; }

    [JsonPropertyName("outFields")]
    public IReadOnlyList<string>? OutFields { get; set; }

    [JsonPropertyName("limit")]
    public int? Limit { get; set; }

    /// <summary>
    /// Number of matching features to skip before returning results (pagination).
    /// Threads into the canonical query pipeline's <c>ResultOffset</c>.
    /// </summary>
    [JsonPropertyName("resultOffset")]
    public int? ResultOffset { get; set; }

    /// <summary>
    /// When <see langword="false"/>, features are returned without geometry
    /// (attribute-only rows). Defaults to <see langword="true"/>.
    /// </summary>
    [JsonPropertyName("returnGeometry")]
    public bool? ReturnGeometry { get; set; }

    /// <summary>
    /// When <see langword="true"/>, only the matching feature count is returned
    /// and no features. Defaults to <see langword="false"/>.
    /// </summary>
    [JsonPropertyName("returnCountOnly")]
    public bool? ReturnCountOnly { get; set; }

    [JsonPropertyName("outSrid")]
    public int? OutSrid { get; set; }

    /// <summary>
    /// Number of decimal places to round returned geometry coordinates to
    /// (x-honua extension over the geospatial-mcp query_features shape). Defaults
    /// to 6 (~0.1&#160;m at the equator) to keep GeoJSON compact; pass a higher
    /// value for finer precision or a negative value for full, unrounded
    /// coordinates. Ignored when <see cref="ReturnGeometry"/> is false.
    /// </summary>
    [JsonPropertyName("geometryPrecision")]
    public int? GeometryPrecision { get; set; }
}

/// <summary>
/// Output for <c>honua_query_features</c>: a compact summary plus the GeoJSON
/// <c>FeatureCollection</c> emitted by the canonical feature-query pipeline.
/// </summary>
internal sealed class McpQueryFeaturesOutput
{
    [JsonPropertyName("serviceId")]
    public string ServiceId { get; set; } = string.Empty;

    [JsonPropertyName("layerId")]
    public int LayerId { get; set; }

    [JsonPropertyName("returnedCount")]
    public int ReturnedCount { get; set; }

    [JsonPropertyName("limit")]
    public int Limit { get; set; }

    /// <summary>
    /// Offset applied to this request (the <c>resultOffset</c> argument, defaulting to 0).
    /// </summary>
    [JsonPropertyName("resultOffset")]
    public int ResultOffset { get; set; }

    [JsonPropertyName("exceededTransferLimit")]
    public bool ExceededTransferLimit { get; set; }

    /// <summary>
    /// When <see cref="ExceededTransferLimit"/> is <see langword="true"/>, the
    /// <c>resultOffset</c> the caller should send on the next request to fetch the
    /// following page (<c>resultOffset + returnedCount</c>). Omitted (null) when the
    /// last page has been returned.
    /// </summary>
    [JsonPropertyName("nextOffset")]
    public int? NextOffset { get; set; }

    /// <summary>
    /// Matching feature count. Populated only when <c>returnCountOnly=true</c>
    /// (features are omitted in that mode); otherwise null.
    /// </summary>
    [JsonPropertyName("count")]
    public long? Count { get; set; }

    /// <summary>RFC 7946 GeoJSON <c>FeatureCollection</c> for the returned features. Omitted when <c>returnCountOnly=true</c>.</summary>
    [JsonPropertyName("geojson")]
    public McpGeoJsonFeatureCollection? GeoJson { get; set; }
}

/// <summary>
/// Minimal GeoJSON <c>FeatureCollection</c> wrapper. Feature geometries and
/// attribute values are carried as raw JSON nodes so the source-generated
/// serializer can emit them without reflecting over arbitrary attribute types.
/// </summary>
internal sealed class McpGeoJsonFeatureCollection
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "FeatureCollection";

    [JsonPropertyName("features")]
    public IReadOnlyList<System.Text.Json.Nodes.JsonNode> Features { get; set; } = [];
}

// -----------------------------------------------------------------------
// honua_render_map
// -----------------------------------------------------------------------

/// <summary>
/// Arguments for <c>honua_render_map</c>.
/// </summary>
internal sealed class McpRenderMapArgument
{
    /// <summary>
    /// Ordered layers to render, bottom-to-top. Each entry addresses a published
    /// layer by <c>serviceId</c> + <c>layerId</c>.
    /// </summary>
    [JsonPropertyName("layers")]
    public IReadOnlyList<McpRenderLayerRef>? Layers { get; set; }

    /// <summary>Map extent as <c>[minX, minY, maxX, maxY]</c>.</summary>
    [JsonPropertyName("bbox")]
    public IReadOnlyList<double>? Bbox { get; set; }

    [JsonPropertyName("bboxSrid")]
    public int? BboxSrid { get; set; }

    [JsonPropertyName("width")]
    public int? Width { get; set; }

    [JsonPropertyName("height")]
    public int? Height { get; set; }

    [JsonPropertyName("transparent")]
    public bool? Transparent { get; set; }

    /// <summary>
    /// Opt-in ceiling (bytes) for inlining the rendered PNG as a base64
    /// <c>image</c> content block (x-honua extension). When the encoded image is
    /// at or below this size it is returned inline; otherwise — and by default
    /// (null / 0) — the tool returns a fetchable artifact href
    /// (<c>resource_link</c>) plus dimensions and byte size in text, so a
    /// ~2&#160;MB render never floods the model context.
    /// </summary>
    [JsonPropertyName("maxInlineBytes")]
    public int? MaxInlineBytes { get; set; }
}

/// <summary>
/// A single layer reference inside a <c>honua_render_map</c> request.
/// </summary>
internal sealed class McpRenderLayerRef
{
    [JsonPropertyName("serviceId")]
    public string? ServiceId { get; set; }

    [JsonPropertyName("layerId")]
    public int? LayerId { get; set; }
}

// -----------------------------------------------------------------------
// honua_edit_features
// -----------------------------------------------------------------------

/// <summary>
/// Arguments for <c>honua_edit_features</c>. Carries the transactional
/// add/update/delete edit sets against a single published editable layer. The
/// tool is a thin adapter over the shared edit/transaction pipeline
/// (<see cref="Honua.Core.Features.Edit.IEditProcessor"/> +
/// <see cref="Honua.Core.Features.FeatureStore.Abstractions.IFeatureWriter"/>);
/// it introduces no edit semantics of its own.
/// </summary>
internal sealed class McpEditFeaturesArgument
{
    [JsonPropertyName("serviceId")]
    public string? ServiceId { get; set; }

    [JsonPropertyName("layerId")]
    public int? LayerId { get; set; }

    /// <summary>Spatial reference (SRID/WKID) of the input feature geometries; defaults to 4326 (WGS 84).</summary>
    [JsonPropertyName("srid")]
    public int? Srid { get; set; }

    /// <summary>Features to insert. Each carries GeoJSON geometry and an attribute map.</summary>
    [JsonPropertyName("adds")]
    public IReadOnlyList<McpEditFeature>? Adds { get; set; }

    /// <summary>Features to update. Each MUST carry an <c>objectId</c> identifying the existing feature.</summary>
    [JsonPropertyName("updates")]
    public IReadOnlyList<McpEditFeature>? Updates { get; set; }

    /// <summary>Object IDs of features to delete.</summary>
    [JsonPropertyName("deletes")]
    public IReadOnlyList<long>? Deletes { get; set; }

    /// <summary>When true (default), any failed edit rolls back the entire transaction (all-or-nothing).</summary>
    [JsonPropertyName("rollbackOnFailure")]
    public bool? RollbackOnFailure { get; set; }

    /// <summary>When true (default), per-edit results are returned; when false only the transaction summary is emitted.</summary>
    [JsonPropertyName("returnEditResults")]
    public bool? ReturnEditResults { get; set; }
}

/// <summary>
/// A single feature in a <c>honua_edit_features</c> add/update set. Geometry is an
/// RFC 7946 GeoJSON geometry object; <see cref="Attributes"/> is a flat
/// attribute name/value map. <see cref="ObjectId"/> is required for updates
/// (and <see cref="GlobalId"/> may accompany it); both are ignored for adds,
/// where the store assigns the object ID.
/// </summary>
internal sealed class McpEditFeature
{
    [JsonPropertyName("objectId")]
    public long? ObjectId { get; set; }

    [JsonPropertyName("globalId")]
    public string? GlobalId { get; set; }

    /// <summary>RFC 7946 GeoJSON geometry object (e.g. <c>{"type":"Point","coordinates":[1,2]}</c>). Optional for attribute-only edits.</summary>
    [JsonPropertyName("geometry")]
    public System.Text.Json.Nodes.JsonNode? Geometry { get; set; }

    /// <summary>Flat attribute name/value map applied to the feature.</summary>
    [JsonPropertyName("attributes")]
    public System.Text.Json.Nodes.JsonNode? Attributes { get; set; }
}

/// <summary>
/// Output for <c>honua_edit_features</c>: per-edit results grouped by edit kind
/// plus a transaction summary, projected from the shared pipeline's
/// <see cref="Honua.Core.Features.FeatureStore.Domain.FeatureEditResult"/>.
/// </summary>
internal sealed class McpEditFeaturesOutput
{
    [JsonPropertyName("serviceId")]
    public string ServiceId { get; set; } = string.Empty;

    [JsonPropertyName("layerId")]
    public int LayerId { get; set; }

    [JsonPropertyName("addResults")]
    public IReadOnlyList<McpEditResult> AddResults { get; set; } = [];

    [JsonPropertyName("updateResults")]
    public IReadOnlyList<McpEditResult> UpdateResults { get; set; } = [];

    [JsonPropertyName("deleteResults")]
    public IReadOnlyList<McpEditResult> DeleteResults { get; set; } = [];

    [JsonPropertyName("summary")]
    public McpEditSummary Summary { get; set; } = new();
}

/// <summary>
/// One per-edit result. <see cref="Index"/> is the zero-based position of the edit
/// within its submitted array; <see cref="ObjectId"/> carries the assigned
/// (create) or targeted (update/delete) object ID when known.
/// </summary>
internal sealed class McpEditResult
{
    [JsonPropertyName("index")]
    public int Index { get; set; }

    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("objectId")]
    public long? ObjectId { get; set; }

    [JsonPropertyName("globalId")]
    public string? GlobalId { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }
}

/// <summary>
/// Transaction summary for a <c>honua_edit_features</c> call: how many edits were
/// applied, how many failed, and whether the whole transaction was rolled back.
/// </summary>
internal sealed class McpEditSummary
{
    [JsonPropertyName("applied")]
    public int Applied { get; set; }

    [JsonPropertyName("failed")]
    public int Failed { get; set; }

    [JsonPropertyName("rolledBack")]
    public bool RolledBack { get; set; }
}
