// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Server.Features.MapServer.Models;

/// <summary>
/// Response for the MapServer service metadata endpoint.
/// </summary>
internal sealed class MapServerResponse
{
    /// <summary>
    /// ArcGIS service version number.
    /// </summary>
    [JsonPropertyName("currentVersion")]
    public double CurrentVersion { get; init; } = 10.81;

    /// <summary>
    /// Service description displayed by ArcGIS clients.
    /// </summary>
    [JsonPropertyName("serviceDescription")]
    public string? ServiceDescription { get; init; }

    /// <summary>
    /// Service name.
    /// </summary>
    [JsonPropertyName("mapName")]
    public string? MapName { get; init; }

    /// <summary>
    /// Service description.
    /// </summary>
    [JsonPropertyName("description")]
    public string? Description { get; init; }

    /// <summary>
    /// Copyright text.
    /// </summary>
    [JsonPropertyName("copyrightText")]
    public string? CopyrightText { get; init; }

    /// <summary>
    /// Spatial reference.
    /// </summary>
    [JsonPropertyName("spatialReference")]
    public EsriSpatialReference? SpatialReference { get; init; }

    /// <summary>
    /// Layers in the service.
    /// </summary>
    [JsonPropertyName("layers")]
    public MapServerLayerInfo[]? Layers { get; init; }

    /// <summary>
    /// Non-spatial tables in the service.
    /// </summary>
    [JsonPropertyName("tables")]
    public MapServerTableInfo[]? Tables { get; init; }

    /// <summary>
    /// Indicates whether dynamic layer rendering is supported.
    /// </summary>
    [JsonPropertyName("supportsDynamicLayers")]
    public bool SupportsDynamicLayers { get; init; } = true;

    /// <summary>
    /// Indicates whether cached map tiles are used.
    /// </summary>
    [JsonPropertyName("singleFusedMapCache")]
    public bool SingleFusedMapCache { get; init; }

    /// <summary>
    /// Supported image format types.
    /// </summary>
    [JsonPropertyName("supportedImageFormatTypes")]
    public string? SupportedImageFormatTypes { get; init; }

    /// <summary>
    /// Service capabilities.
    /// </summary>
    [JsonPropertyName("capabilities")]
    public string? Capabilities { get; init; }

    /// <summary>
    /// Full extent of the service.
    /// </summary>
    [JsonPropertyName("fullExtent")]
    public EsriExtent? FullExtent { get; init; }

    /// <summary>
    /// Initial extent of the service.
    /// </summary>
    [JsonPropertyName("initialExtent")]
    public EsriExtent? InitialExtent { get; init; }

    /// <summary>
    /// Display units for map scale and measurements.
    /// </summary>
    [JsonPropertyName("units")]
    public string Units { get; init; } = "esriMeters";

    /// <summary>
    /// Maximum image width.
    /// </summary>
    [JsonPropertyName("maxImageWidth")]
    public int MaxImageWidth { get; init; } = 4096;

    /// <summary>
    /// Maximum image height.
    /// </summary>
    [JsonPropertyName("maxImageHeight")]
    public int MaxImageHeight { get; init; } = 4096;

    /// <summary>
    /// Maximum number of records returned per query.
    /// </summary>
    [JsonPropertyName("maxRecordCount")]
    public int MaxRecordCount { get; init; } = 1000;

    /// <summary>
    /// Supported query output formats.
    /// </summary>
    [JsonPropertyName("supportedQueryFormats")]
    public string? SupportedQueryFormats { get; init; }

    /// <summary>
    /// Minimum visible scale for the service.
    /// </summary>
    [JsonPropertyName("minScale")]
    public double? MinScale { get; init; }

    /// <summary>
    /// Maximum visible scale for the service.
    /// </summary>
    [JsonPropertyName("maxScale")]
    public double? MaxScale { get; init; }

    /// <summary>
    /// Document metadata for the service.
    /// </summary>
    [JsonPropertyName("documentInfo")]
    public MapServerDocumentInfo? DocumentInfo { get; init; }

    /// <summary>
    /// Tile caching information for the service (WebMercatorQuad tile matrix set).
    /// </summary>
    [JsonPropertyName("tileInfo")]
    public TileInfo? TileInfo { get; init; }
}

/// <summary>
/// Document metadata for a MapServer service.
/// </summary>
internal sealed class MapServerDocumentInfo
{
    /// <summary>
    /// Document title.
    /// </summary>
    [JsonPropertyName("Title")]
    public string Title { get; init; } = "";

    /// <summary>
    /// Document author.
    /// </summary>
    [JsonPropertyName("Author")]
    public string Author { get; init; } = "";

    /// <summary>
    /// Document comments.
    /// </summary>
    [JsonPropertyName("Comments")]
    public string Comments { get; init; } = "";

    /// <summary>
    /// Document subject.
    /// </summary>
    [JsonPropertyName("Subject")]
    public string Subject { get; init; } = "";

    /// <summary>
    /// Document category.
    /// </summary>
    [JsonPropertyName("Category")]
    public string Category { get; init; } = "";

    /// <summary>
    /// Document keywords.
    /// </summary>
    [JsonPropertyName("Keywords")]
    public string Keywords { get; init; } = "";
}

/// <summary>
/// Layer information in a MapServer service.
/// </summary>
internal sealed class MapServerLayerInfo
{
    /// <summary>
    /// Layer identifier.
    /// </summary>
    [JsonPropertyName("id")]
    public int Id { get; init; }

    /// <summary>
    /// Layer name.
    /// </summary>
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    /// <summary>
    /// Whether the layer is visible by default.
    /// </summary>
    [JsonPropertyName("defaultVisibility")]
    public bool DefaultVisibility { get; init; }

    /// <summary>
    /// Minimum scale for visibility.
    /// </summary>
    [JsonPropertyName("minScale")]
    public double? MinScale { get; init; }

    /// <summary>
    /// Maximum scale for visibility.
    /// </summary>
    [JsonPropertyName("maxScale")]
    public double? MaxScale { get; init; }

    /// <summary>
    /// Parent layer identifier (-1 for root layers).
    /// </summary>
    [JsonPropertyName("parentLayerId")]
    public int ParentLayerId { get; init; } = -1;

    /// <summary>
    /// Sub-layer identifiers (null for leaf layers).
    /// </summary>
    [JsonPropertyName("subLayerIds")]
    public int[]? SubLayerIds { get; init; }
}

/// <summary>
/// Table information in a MapServer service.
/// </summary>
internal sealed class MapServerTableInfo
{
    /// <summary>
    /// Table identifier.
    /// </summary>
    [JsonPropertyName("id")]
    public int Id { get; init; }

    /// <summary>
    /// Table name.
    /// </summary>
    [JsonPropertyName("name")]
    public string? Name { get; init; }
}

/// <summary>
/// Response for layer-specific MapServer metadata.
/// </summary>
internal sealed class MapServerLayerResponse
{
    /// <summary>
    /// ArcGIS service version number.
    /// </summary>
    [JsonPropertyName("currentVersion")]
    public double CurrentVersion { get; init; } = 10.81;

    /// <summary>
    /// Layer identifier.
    /// </summary>
    [JsonPropertyName("id")]
    public int Id { get; init; }

    /// <summary>
    /// Layer name.
    /// </summary>
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    /// <summary>
    /// Layer type text.
    /// </summary>
    [JsonPropertyName("type")]
    public string Type { get; init; } = "Feature Layer";

    /// <summary>
    /// Human-readable description.
    /// </summary>
    [JsonPropertyName("description")]
    public string? Description { get; init; }

    /// <summary>
    /// Geometry type (for feature layers).
    /// </summary>
    [JsonPropertyName("geometryType")]
    public string? GeometryType { get; init; }

    /// <summary>
    /// Layer extent.
    /// </summary>
    [JsonPropertyName("extent")]
    public EsriExtent? Extent { get; init; }

    /// <summary>
    /// Layer spatial reference.
    /// </summary>
    [JsonPropertyName("spatialReference")]
    public EsriSpatialReference? SpatialReference { get; init; }

    /// <summary>
    /// Field name used by clients for display.
    /// </summary>
    [JsonPropertyName("displayField")]
    public string? DisplayField { get; init; }

    /// <summary>
    /// Object ID field name.
    /// </summary>
    [JsonPropertyName("objectIdField")]
    public string? ObjectIdField { get; init; }

    /// <summary>
    /// Layer field definitions.
    /// </summary>
    [JsonPropertyName("fields")]
    public MapServerFieldInfo[]? Fields { get; init; }

    /// <summary>
    /// Layer capabilities.
    /// </summary>
    [JsonPropertyName("capabilities")]
    public string? Capabilities { get; init; }

    /// <summary>
    /// Indicates if advanced query options are supported.
    /// </summary>
    [JsonPropertyName("supportsAdvancedQueries")]
    public bool SupportsAdvancedQueries { get; init; }

    /// <summary>
    /// Indicates whether attachments are enabled for the layer.
    /// </summary>
    [JsonPropertyName("hasAttachments")]
    public bool HasAttachments { get; init; }

    /// <summary>
    /// Min scale visibility threshold.
    /// </summary>
    [JsonPropertyName("minScale")]
    public double? MinScale { get; init; }

    /// <summary>
    /// Max scale visibility threshold.
    /// </summary>
    [JsonPropertyName("maxScale")]
    public double? MaxScale { get; init; }

    /// <summary>
    /// Default map visibility.
    /// </summary>
    [JsonPropertyName("defaultVisibility")]
    public bool DefaultVisibility { get; init; } = true;

    /// <summary>
    /// Maximum records per query.
    /// </summary>
    [JsonPropertyName("maxRecordCount")]
    public int MaxRecordCount { get; init; } = 1000;

    /// <summary>
    /// Drawing info for client-side symbology.
    /// </summary>
    [JsonPropertyName("drawingInfo")]
    public object? DrawingInfo { get; init; }

    /// <summary>
    /// Supported query output formats (comma-separated string per ESRI spec).
    /// </summary>
    [JsonPropertyName("supportedQueryFormats")]
    public string? SupportedQueryFormats { get; init; }

    /// <summary>
    /// Indicates whether statistics queries are supported.
    /// </summary>
    [JsonPropertyName("supportsStatistics")]
    public bool SupportsStatistics { get; init; }

    /// <summary>
    /// Indicates whether ORDER BY is supported.
    /// </summary>
    [JsonPropertyName("supportsOrderBy")]
    public bool SupportsOrderBy { get; init; }

    /// <summary>
    /// Indicates whether DISTINCT is supported.
    /// </summary>
    [JsonPropertyName("supportsDistinct")]
    public bool SupportsDistinct { get; init; }

    /// <summary>
    /// Indicates whether result pagination is supported.
    /// </summary>
    [JsonPropertyName("supportsPagination")]
    public bool SupportsPagination { get; init; }

    /// <summary>
    /// Parent layer identifier (-1 for root layers).
    /// </summary>
    [JsonPropertyName("parentLayerId")]
    public int ParentLayerId { get; init; } = -1;

    /// <summary>
    /// Sub-layer identifiers (null for leaf layers).
    /// </summary>
    [JsonPropertyName("subLayerIds")]
    public int[]? SubLayerIds { get; init; }
}

/// <summary>
/// Field metadata for MapServer layer responses.
/// </summary>
internal sealed class MapServerFieldInfo
{
    /// <summary>
    /// Field name.
    /// </summary>
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    /// <summary>
    /// ArcGIS field type.
    /// </summary>
    [JsonPropertyName("type")]
    public string? Type { get; init; }

    /// <summary>
    /// Display alias.
    /// </summary>
    [JsonPropertyName("alias")]
    public string? Alias { get; init; }

    /// <summary>
    /// Maximum character length for text fields.
    /// </summary>
    [JsonPropertyName("length")]
    public int? Length { get; init; }

    /// <summary>
    /// Indicates whether null values are allowed.
    /// </summary>
    [JsonPropertyName("nullable")]
    public bool Nullable { get; init; } = true;

    /// <summary>
    /// Indicates whether values are editable.
    /// </summary>
    [JsonPropertyName("editable")]
    public bool Editable { get; init; } = true;

    /// <summary>
    /// Default value for the field.
    /// </summary>
    [JsonPropertyName("defaultValue")]
    public object? DefaultValue { get; init; }
}

/// <summary>
/// Response for the identify operation.
/// </summary>
internal sealed class IdentifyResponse
{
    /// <summary>
    /// Identified results.
    /// </summary>
    [JsonPropertyName("results")]
    public IdentifyResult[]? Results { get; init; }
}

/// <summary>
/// Single identify result.
/// </summary>
internal sealed class IdentifyResult
{
    /// <summary>
    /// Layer identifier.
    /// </summary>
    [JsonPropertyName("layerId")]
    public int LayerId { get; init; }

    /// <summary>
    /// Layer name.
    /// </summary>
    [JsonPropertyName("layerName")]
    public string? LayerName { get; init; }

    /// <summary>
    /// Name of the display field for this layer.
    /// </summary>
    [JsonPropertyName("displayFieldName")]
    public string? DisplayFieldName { get; init; }

    /// <summary>
    /// Display field value.
    /// </summary>
    [JsonPropertyName("value")]
    public string? Value { get; init; }

    /// <summary>
    /// Feature attributes.
    /// </summary>
    [JsonPropertyName("attributes")]
    public Dictionary<string, object?>? Attributes { get; init; }

    /// <summary>
    /// Feature geometry type.
    /// </summary>
    [JsonPropertyName("geometryType")]
    public string? GeometryType { get; init; }

    /// <summary>
    /// Feature geometry.
    /// </summary>
    [JsonPropertyName("geometry")]
    public object? Geometry { get; init; }
}

/// <summary>
/// Response for the legend operation.
/// </summary>
internal sealed class LegendResponse
{
    /// <summary>
    /// Legend layers.
    /// </summary>
    [JsonPropertyName("layers")]
    public LegendLayerInfo[]? Layers { get; init; }
}

/// <summary>
/// Legend information for a layer.
/// </summary>
internal sealed class LegendLayerInfo
{
    /// <summary>
    /// Layer identifier.
    /// </summary>
    [JsonPropertyName("layerId")]
    public int LayerId { get; init; }

    /// <summary>
    /// Layer name.
    /// </summary>
    [JsonPropertyName("layerName")]
    public string? LayerName { get; init; }

    /// <summary>
    /// Layer type.
    /// </summary>
    [JsonPropertyName("layerType")]
    public string? LayerType { get; init; }

    /// <summary>
    /// Minimum scale for visibility.
    /// </summary>
    [JsonPropertyName("minScale")]
    public double? MinScale { get; init; }

    /// <summary>
    /// Maximum scale for visibility.
    /// </summary>
    [JsonPropertyName("maxScale")]
    public double? MaxScale { get; init; }

    /// <summary>
    /// Legend entries for this layer.
    /// </summary>
    [JsonPropertyName("legend")]
    public LegendEntry[]? Legend { get; init; }
}

/// <summary>
/// Single legend entry with swatch image and label.
/// </summary>
internal sealed class LegendEntry
{
    /// <summary>
    /// Legend symbol label.
    /// </summary>
    [JsonPropertyName("label")]
    public string? Label { get; init; }

    /// <summary>
    /// Base64-encoded legend image data.
    /// </summary>
    [JsonPropertyName("imageData")]
    public string? ImageData { get; init; }

    /// <summary>
    /// Content type of the image (e.g., "image/png").
    /// </summary>
    [JsonPropertyName("contentType")]
    public string ContentType { get; init; } = "image/png";

    /// <summary>
    /// Width of the legend image in pixels.
    /// </summary>
    [JsonPropertyName("width")]
    public int Width { get; init; } = 20;

    /// <summary>
    /// Height of the legend image in pixels.
    /// </summary>
    [JsonPropertyName("height")]
    public int Height { get; init; } = 20;
}

/// <summary>
/// Export response when f=json.
/// </summary>
internal sealed class ExportImageResponse
{
    /// <summary>
    /// URL to the generated image (not used in inline mode).
    /// </summary>
    [JsonPropertyName("href")]
    public string? Href { get; init; }

    /// <summary>
    /// Width of the exported image.
    /// </summary>
    [JsonPropertyName("width")]
    public int Width { get; init; }

    /// <summary>
    /// Height of the exported image.
    /// </summary>
    [JsonPropertyName("height")]
    public int Height { get; init; }

    /// <summary>
    /// Extent of the exported image.
    /// </summary>
    [JsonPropertyName("extent")]
    public EsriExtent? Extent { get; init; }

    /// <summary>
    /// Map scale denominator.
    /// </summary>
    [JsonPropertyName("scale")]
    public double? Scale { get; init; }
}

/// <summary>
/// Response for the find operation.
/// </summary>
internal sealed class FindResponse
{
    /// <summary>
    /// Find results.
    /// </summary>
    [JsonPropertyName("results")]
    public FindResult[]? Results { get; init; }
}

/// <summary>
/// Single find result.
/// </summary>
internal sealed class FindResult
{
    /// <summary>
    /// Layer identifier.
    /// </summary>
    [JsonPropertyName("layerId")]
    public int LayerId { get; init; }

    /// <summary>
    /// Layer name.
    /// </summary>
    [JsonPropertyName("layerName")]
    public string? LayerName { get; init; }

    /// <summary>
    /// Display field name for the layer.
    /// </summary>
    [JsonPropertyName("displayFieldName")]
    public string? DisplayFieldName { get; init; }

    /// <summary>
    /// Field name where the match was found.
    /// </summary>
    [JsonPropertyName("foundFieldName")]
    public string? FoundFieldName { get; init; }

    /// <summary>
    /// Matched display value.
    /// </summary>
    [JsonPropertyName("value")]
    public string? Value { get; init; }

    /// <summary>
    /// Feature attributes.
    /// </summary>
    [JsonPropertyName("attributes")]
    public Dictionary<string, object?>? Attributes { get; init; }

    /// <summary>
    /// Feature geometry type.
    /// </summary>
    [JsonPropertyName("geometryType")]
    public string? GeometryType { get; init; }

    /// <summary>
    /// Feature geometry.
    /// </summary>
    [JsonPropertyName("geometry")]
    public object? Geometry { get; init; }
}

/// <summary>
/// Tile caching information describing the tiling scheme.
/// </summary>
internal sealed class TileInfo
{
    /// <summary>
    /// Number of pixel rows per tile.
    /// </summary>
    [JsonPropertyName("rows")]
    public int Rows { get; init; } = 256;

    /// <summary>
    /// Number of pixel columns per tile.
    /// </summary>
    [JsonPropertyName("cols")]
    public int Cols { get; init; } = 256;

    /// <summary>
    /// Dots per inch for tile rendering.
    /// </summary>
    [JsonPropertyName("dpi")]
    public int Dpi { get; init; } = 96;

    /// <summary>
    /// Image format used for tiles.
    /// </summary>
    [JsonPropertyName("format")]
    public string Format { get; init; } = "PNG";

    /// <summary>
    /// Tile origin point in map coordinates.
    /// </summary>
    [JsonPropertyName("origin")]
    public TileOrigin? Origin { get; init; }

    /// <summary>
    /// Spatial reference of the tile scheme.
    /// </summary>
    [JsonPropertyName("spatialReference")]
    public EsriSpatialReference? SpatialReference { get; init; }

    /// <summary>
    /// Levels of detail (zoom levels) available for tile access.
    /// </summary>
    [JsonPropertyName("lods")]
    public LevelOfDetail[]? Lods { get; init; }
}

/// <summary>
/// Tile origin point coordinates.
/// </summary>
internal sealed class TileOrigin
{
    /// <summary>
    /// X coordinate of the tile origin.
    /// </summary>
    [JsonPropertyName("x")]
    public double X { get; init; }

    /// <summary>
    /// Y coordinate of the tile origin.
    /// </summary>
    [JsonPropertyName("y")]
    public double Y { get; init; }
}

/// <summary>
/// Describes a single level of detail (zoom level) in a tiling scheme.
/// </summary>
internal sealed class LevelOfDetail
{
    /// <summary>
    /// Zoom level number.
    /// </summary>
    [JsonPropertyName("level")]
    public int Level { get; init; }

    /// <summary>
    /// Map resolution in units per pixel at this zoom level.
    /// </summary>
    [JsonPropertyName("resolution")]
    public double Resolution { get; init; }

    /// <summary>
    /// Scale denominator at this zoom level.
    /// </summary>
    [JsonPropertyName("scale")]
    public double Scale { get; init; }
}
