// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Ai.Protocols.Mcp.MapTools;

// -----------------------------------------------------------------------
// honua_list_layers
// -----------------------------------------------------------------------

internal sealed class McpListLayersArgument
{
    [JsonPropertyName("filter")]
    public string? Filter { get; set; }

    [JsonPropertyName("limit")]
    public int? Limit { get; set; }

    [JsonPropertyName("offset")]
    public int? Offset { get; set; }
}

internal sealed class McpListLayersOutput
{
    [JsonPropertyName("layerCount")]
    public int LayerCount { get; set; }

    [JsonPropertyName("totalCount")]
    public int TotalCount { get; set; }

    [JsonPropertyName("offset")]
    public int Offset { get; set; }

    [JsonPropertyName("hasMore")]
    public bool HasMore { get; set; }

    [JsonPropertyName("nextOffset")]
    public int? NextOffset { get; set; }

    [JsonPropertyName("layers")]
    public IReadOnlyList<McpLayerSummary> Layers { get; set; } = [];
}

internal class McpLayerSummary
{
    [JsonPropertyName("serviceId")]
    public string ServiceId { get; set; } = string.Empty;

    [JsonPropertyName("serviceName")]
    public string ServiceName { get; set; } = string.Empty;

    [JsonPropertyName("serviceLayerIndex")]
    public int ServiceLayerIndex { get; set; }

    [JsonPropertyName("studioSourceId")]
    public string StudioSourceId => StudioCatalogSourceIdentity.Create(ServiceId, ServiceLayerIndex);

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

// GeoJSON and OGC bounding boxes are ordered minX, minY, maxX, maxY.
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
// honua_describe_layer
// -----------------------------------------------------------------------

internal sealed class McpDescribeLayerArgument
{
    [JsonPropertyName("serviceId")]
    public string? ServiceId { get; set; }

    [JsonPropertyName("serviceLayerIndex")]
    public int? ServiceLayerIndex { get; set; }

    [JsonPropertyName("includeRowCount")]
    public bool? IncludeRowCount { get; set; }
}

internal sealed class McpDescribeLayerOutput : McpLayerSummary
{
    [JsonPropertyName("rowCount")]
    public long? RowCount { get; set; }

    [JsonPropertyName("fieldCount")]
    public int FieldCount { get; set; }

    [JsonPropertyName("fields")]
    public IReadOnlyList<McpLayerField> Fields { get; set; } = [];
}

internal sealed class McpLayerField
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("alias")]
    public string? Alias { get; set; }

    [JsonPropertyName("nullable")]
    public bool Nullable { get; set; }
}

// -----------------------------------------------------------------------
// honua_query_features
// -----------------------------------------------------------------------

internal sealed class McpQueryFeaturesArgument
{
    [JsonPropertyName("serviceId")]
    public string? ServiceId { get; set; }

    [JsonPropertyName("serviceLayerIndex")]
    public int? ServiceLayerIndex { get; set; }

    [JsonPropertyName("where")]
    public string? Where { get; set; }

    [JsonPropertyName("bbox")]
    public IReadOnlyList<double>? Bbox { get; set; }

    [JsonPropertyName("bboxSrid")]
    public int? BboxSrid { get; set; }

    [JsonPropertyName("outFields")]
    public IReadOnlyList<string>? OutFields { get; set; }

    [JsonPropertyName("limit")]
    public int? Limit { get; set; }

    [JsonPropertyName("resultOffset")]
    public int? ResultOffset { get; set; }

    [JsonPropertyName("cursor")]
    public string? Cursor { get; set; }

    [JsonPropertyName("returnGeometry")]
    public bool? ReturnGeometry { get; set; }

    [JsonPropertyName("returnCountOnly")]
    public bool? ReturnCountOnly { get; set; }

    [JsonPropertyName("outSrid")]
    public int? OutSrid { get; set; }

    [JsonPropertyName("geometryPrecision")]
    public int? GeometryPrecision { get; set; }
}

internal sealed class McpQueryFeaturesOutput
{
    [JsonPropertyName("serviceId")]
    public string ServiceId { get; set; } = string.Empty;

    [JsonPropertyName("serviceLayerIndex")]
    public int ServiceLayerIndex { get; set; }

    [JsonPropertyName("returnedCount")]
    public int ReturnedCount { get; set; }

    [JsonPropertyName("limit")]
    public int Limit { get; set; }

    [JsonPropertyName("resultOffset")]
    public int ResultOffset { get; set; }

    [JsonPropertyName("exceededTransferLimit")]
    public bool ExceededTransferLimit { get; set; }

    [JsonPropertyName("nextOffset")]
    public int? NextOffset { get; set; }

    [JsonPropertyName("nextCursor")]
    public string? NextCursor { get; set; }

    [JsonPropertyName("count")]
    public long? Count { get; set; }

    // Attributes are projected under attributes while the GeoJSON feature keeps the RFC 7946 properties shape.
    [JsonPropertyName("features")]
    public IReadOnlyList<System.Text.Json.Nodes.JsonNode> Features { get; set; } = [];

    [JsonPropertyName("geojson")]
    public McpGeoJsonFeatureCollection? GeoJson { get; set; }

    [JsonPropertyName("warnings")]
    public IReadOnlyList<string> Warnings { get; set; } = [];
}

// Feature geometry and attributes stay raw JSON nodes so the source-generated serializer can emit them without reflecting over arbitrary attribute types.
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

internal sealed class McpRenderMapArgument
{
    [JsonPropertyName("layers")]
    public IReadOnlyList<McpRenderLayerRef>? Layers { get; set; }

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

    // Images larger than the opt-in ceiling stay as a fetchable href so a render does not flood the model context.
    [JsonPropertyName("maxInlineBytes")]
    public int? MaxInlineBytes { get; set; }
}

internal sealed class McpRenderLayerRef
{
    [JsonPropertyName("serviceId")]
    public string? ServiceId { get; set; }

    [JsonPropertyName("serviceLayerIndex")]
    public int? ServiceLayerIndex { get; set; }
}

internal sealed class McpRenderMapOutput
{
    [JsonPropertyName("format")]
    public string Format { get; set; } = "image/png";

    [JsonPropertyName("width")]
    public int Width { get; set; }

    [JsonPropertyName("height")]
    public int Height { get; set; }

    [JsonPropertyName("byteLength")]
    public int ByteLength { get; set; }

    [JsonPropertyName("bbox")]
    public IReadOnlyList<double> Bbox { get; set; } = [];

    [JsonPropertyName("bboxSrid")]
    public int BboxSrid { get; set; }

    [JsonPropertyName("layers")]
    public IReadOnlyList<McpRenderedLayer> Layers { get; set; } = [];

    [JsonPropertyName("image")]
    public McpRenderedImage Image { get; set; } = new();
}

internal sealed class McpRenderedLayer
{
    [JsonPropertyName("serviceId")]
    public string ServiceId { get; set; } = string.Empty;

    [JsonPropertyName("serviceLayerIndex")]
    public int ServiceLayerIndex { get; set; }

    [JsonPropertyName("styleId")]
    public string? StyleId { get; set; }

    [JsonPropertyName("styleRendering")]
    public string StyleRendering { get; set; } = AppliedStyleRenderSupport.Default;

    [JsonPropertyName("unsupportedStyleConstructs")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? UnsupportedStyleConstructs { get; set; }
}

internal sealed class McpRenderedImage
{
    [JsonPropertyName("format")]
    public string Format { get; set; } = "image/png";

    [JsonPropertyName("width")]
    public int Width { get; set; }

    [JsonPropertyName("height")]
    public int Height { get; set; }

    [JsonPropertyName("byteLength")]
    public int ByteLength { get; set; }

    [JsonPropertyName("uri")]
    public string? Uri { get; set; }

    [JsonPropertyName("base64")]
    public string? Base64 { get; set; }

    [JsonPropertyName("inlined")]
    public bool Inlined { get; set; }
}

// -----------------------------------------------------------------------
// honua_get_style
// -----------------------------------------------------------------------

internal sealed class McpGetStyleArgument
{
    [JsonPropertyName("styleId")]
    public string? StyleId { get; set; }

    [JsonPropertyName("serviceId")]
    public string? ServiceId { get; set; }

    [JsonPropertyName("serviceLayerIndex")]
    public int? ServiceLayerIndex { get; set; }

    [JsonPropertyName("encoding")]
    public string? Encoding { get; set; }

    [JsonPropertyName("includeStylesheet")]
    public bool? IncludeStylesheet { get; set; }
}

internal sealed class McpGetStyleOutput
{
    [JsonPropertyName("styleId")]
    public string? StyleId { get; set; }

    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("styleVersion")]
    public int? StyleVersion { get; set; }

    [JsonPropertyName("encodings")]
    public IReadOnlyList<McpStyleEncodingRef>? Encodings { get; set; }

    [JsonPropertyName("styles")]
    public IReadOnlyList<McpStyleSummary>? Styles { get; set; }
}

internal sealed class McpStyleEncodingRef
{
    [JsonPropertyName("encoding")]
    public string Encoding { get; set; } = string.Empty;

    [JsonPropertyName("mediaType")]
    public string MediaType { get; set; } = string.Empty;

    [JsonPropertyName("inlineBody")]
    public string? InlineBody { get; set; }

    [JsonPropertyName("storageRef")]
    public string? StorageRef { get; set; }
}

internal sealed class McpStyleSummary
{
    [JsonPropertyName("styleId")]
    public string StyleId { get; set; } = string.Empty;

    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("uri")]
    public string Uri { get; set; } = string.Empty;
}

// -----------------------------------------------------------------------
// honua_apply_style_preset
// -----------------------------------------------------------------------

internal sealed class McpApplyStylePresetArgument
{
    [JsonPropertyName("dryRun")]
    public bool DryRun { get; set; }

    [JsonPropertyName("serviceId")]
    public string? ServiceId { get; set; }

    [JsonPropertyName("serviceLayerIndex")]
    public int? ServiceLayerIndex { get; set; }

    [JsonPropertyName("styleId")]
    public string? StyleId { get; set; }
}

internal sealed class McpApplyStylePresetOutput
{
    // Canonical style.apply-preset OperationHandle projection, in the same shape
    // honua_publish_service returns, so a caller can join the style change to its
    // operation instance, correlation, and durable audit record.
    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("operationId")]
    public string OperationId { get; set; } = string.Empty;

    [JsonPropertyName("operationInstanceId")]
    public string OperationInstanceId { get; set; } = string.Empty;

    [JsonPropertyName("handleId")]
    public string HandleId { get; set; } = string.Empty;

    // durable=false means the handle cannot be read back through the status surfaces; the field is omitted once the record is persisted.
    [JsonPropertyName("durable")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Durable { get; set; }

    [JsonPropertyName("correlationId")]
    public string CorrelationId { get; set; } = string.Empty;

    [JsonPropertyName("auditId")]
    public string? AuditId { get; set; }

    [JsonPropertyName("createdAt")]
    public DateTimeOffset CreatedAt { get; set; }

    [JsonPropertyName("updatedAt")]
    public DateTimeOffset UpdatedAt { get; set; }

    [JsonPropertyName("authorizationOutcome")]
    public string? AuthorizationOutcome { get; set; }

    [JsonPropertyName("policyOutcome")]
    public string? PolicyOutcome { get; set; }

    [JsonPropertyName("warning")]
    public string? Warning { get; set; }

    [JsonPropertyName("dryRun")]
    public bool DryRun { get; set; }

    [JsonPropertyName("serviceId")]
    public string ServiceId { get; set; } = string.Empty;

    [JsonPropertyName("serviceLayerIndex")]
    public int ServiceLayerIndex { get; set; }

    [JsonPropertyName("styleId")]
    public string StyleId { get; set; } = string.Empty;

    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("styleVersion")]
    public int StyleVersion { get; set; }

    [JsonPropertyName("applied")]
    public bool Applied { get; set; }
}
