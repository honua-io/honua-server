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
    /// Maximum image width.
    /// </summary>
    [JsonPropertyName("maxImageWidth")]
    public int MaxImageWidth { get; init; } = 4096;

    /// <summary>
    /// Maximum image height.
    /// </summary>
    [JsonPropertyName("maxImageHeight")]
    public int MaxImageHeight { get; init; } = 4096;
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
    /// Base64-encoded image data (for inline responses).
    /// </summary>
    [JsonPropertyName("imageData")]
    public string? ImageData { get; init; }

    /// <summary>
    /// Content type of the image.
    /// </summary>
    [JsonPropertyName("contentType")]
    public string? ContentType { get; init; }
}
