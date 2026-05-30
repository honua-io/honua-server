// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Protocols.GeoServices.ImageServer.Models;

/// <summary>
/// Esri-conformant response for the Image Server <c>legend</c> endpoint.
/// </summary>
public sealed class LegendResponse
{
    /// <summary>
    /// Legend layers (one entry per Image Server layer; ImageServer always has one).
    /// </summary>
    [JsonPropertyName("layers")]
    public LegendLayer[] Layers { get; init; } = [];
}

/// <summary>
/// Single legend layer entry.
/// </summary>
public sealed class LegendLayer
{
    /// <summary>
    /// Numeric layer identifier in the service. Always 0 for Image Server.
    /// </summary>
    [JsonPropertyName("layerId")]
    public int LayerId { get; init; }

    /// <summary>
    /// Layer display name.
    /// </summary>
    [JsonPropertyName("layerName")]
    public required string LayerName { get; init; }

    /// <summary>
    /// Layer type (always <c>Raster Layer</c> for ImageServer).
    /// </summary>
    [JsonPropertyName("layerType")]
    public string LayerType { get; init; } = "Raster Layer";

    /// <summary>
    /// Optional minimum scale at which the layer is visible.
    /// </summary>
    [JsonPropertyName("minScale")]
    public double MinScale { get; init; }

    /// <summary>
    /// Optional maximum scale at which the layer is visible.
    /// </summary>
    [JsonPropertyName("maxScale")]
    public double MaxScale { get; init; }

    /// <summary>
    /// Legend swatches for the layer.
    /// </summary>
    [JsonPropertyName("legend")]
    public LegendEntry[] Legend { get; init; } = [];
}

/// <summary>
/// Single legend swatch matching the Esri spec exactly.
/// </summary>
public sealed class LegendEntry
{
    /// <summary>
    /// Human-readable label (e.g. <c>0 – 64</c>).
    /// </summary>
    [JsonPropertyName("label")]
    public required string Label { get; init; }

    /// <summary>
    /// Esri-compatible URL to the swatch. Always empty when <see cref="ImageData"/> is set.
    /// </summary>
    [JsonPropertyName("url")]
    public string Url { get; init; } = string.Empty;

    /// <summary>
    /// Base64-encoded swatch image bytes.
    /// </summary>
    [JsonPropertyName("imageData")]
    public required string ImageData { get; init; }

    /// <summary>
    /// MIME type of the swatch image (always <c>image/png</c>).
    /// </summary>
    [JsonPropertyName("contentType")]
    public string ContentType { get; init; } = "image/png";

    /// <summary>
    /// Pixel height of the swatch.
    /// </summary>
    [JsonPropertyName("height")]
    public int Height { get; init; }

    /// <summary>
    /// Pixel width of the swatch.
    /// </summary>
    [JsonPropertyName("width")]
    public int Width { get; init; }

    /// <summary>
    /// Optional [min, max] value range that the swatch represents.
    /// </summary>
    [JsonPropertyName("values")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double[]? Values { get; init; }
}
