// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Protocols.GeoServices.ImageServer.Models;

/// <summary>
/// Esri-compatible metadata for one raster catalog item's <c>info</c> resource.
/// </summary>
public sealed class RasterItemInfoResponse
{
    /// <summary>Upper-left raster origin in source coordinates.</summary>
    [JsonPropertyName("origin")]
    public Point? Origin { get; init; }

    /// <summary>Raster width in pixels.</summary>
    [JsonPropertyName("blockWidth")]
    public int BlockWidth { get; init; }

    /// <summary>Raster height in pixels.</summary>
    [JsonPropertyName("blockHeight")]
    public int BlockHeight { get; init; }

    /// <summary>Horizontal pixel size in source units.</summary>
    [JsonPropertyName("pixelSizeX")]
    public double? PixelSizeX { get; init; }

    /// <summary>Vertical pixel size in source units.</summary>
    [JsonPropertyName("pixelSizeY")]
    public double? PixelSizeY { get; init; }

    /// <summary>Spatial extent of the selected raster.</summary>
    [JsonPropertyName("extent")]
    public ImageServerExtent? Extent { get; init; }

    /// <summary>Number of raster bands.</summary>
    [JsonPropertyName("bandCount")]
    public int BandCount { get; init; }

    /// <summary>Esri pixel type token.</summary>
    [JsonPropertyName("pixelType")]
    public required string PixelType { get; init; }

    /// <summary>
    /// First available pyramid level. Honua currently advertises only native-resolution
    /// item pixels, represented by level zero.
    /// </summary>
    [JsonPropertyName("firstPyramidLevel")]
    public int FirstPyramidLevel { get; init; }

    /// <summary>
    /// Maximum available pyramid level. A zero maximum paired with a zero first level
    /// means no additional overview pyramid levels are advertised.
    /// </summary>
    [JsonPropertyName("maxPyramidLevel")]
    public int MaxPyramidLevel { get; init; }
}
