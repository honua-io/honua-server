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

/// <summary>
/// Esri-shaped <c>imageSupportData</c> child resource for one raster catalog item. Reflects the
/// sensor/camera/orientation support data hydrated from the raster store's sensor-metadata
/// companion, matching the auxiliary support files (RPC, interior/exterior orientation) an Esri
/// image service advertises alongside a raster. Only items that carry modeled support data expose
/// this resource; items without it receive a precise not-available response instead.
/// </summary>
public sealed class RasterItemImageSupportDataResponse
{
    /// <summary>Catalog identifier of the raster item the support data belongs to.</summary>
    [JsonPropertyName("rasterId")]
    public long RasterId { get; init; }

    /// <summary>Raster item name.</summary>
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    /// <summary>Human-readable sensor name (for example <c>WorldView-3</c>), when modeled.</summary>
    [JsonPropertyName("sensorName")]
    public string? SensorName { get; init; }

    /// <summary>Camera/instrument model identifier, when modeled.</summary>
    [JsonPropertyName("cameraModel")]
    public string? CameraModel { get; init; }

    /// <summary>Whether interior-orientation support data (focal length, principal point) is present.</summary>
    [JsonPropertyName("hasInteriorOrientation")]
    public bool HasInteriorOrientation { get; init; }

    /// <summary>Whether exterior-orientation support data (camera position/look vector) is present.</summary>
    [JsonPropertyName("hasExteriorOrientation")]
    public bool HasExteriorOrientation { get; init; }

    /// <summary>Whether a Rational Polynomial Coefficients (RPC) image-to-ground model is present.</summary>
    [JsonPropertyName("hasRationalPolynomialCoefficients")]
    public bool HasRationalPolynomialCoefficients { get; init; }

    /// <summary>Identifier of the DEM the height-mensuration path can sample, when associated.</summary>
    [JsonPropertyName("demSource")]
    public string? DemSource { get; init; }
}
