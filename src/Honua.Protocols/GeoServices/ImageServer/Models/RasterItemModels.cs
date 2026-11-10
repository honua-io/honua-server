// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Protocols.GeoServices.ImageServer.Models;

public sealed class RasterItemInfoResponse
{
    [JsonPropertyName("origin")]
    public Point? Origin { get; init; }

    [JsonPropertyName("blockWidth")]
    public int BlockWidth { get; init; }

    [JsonPropertyName("blockHeight")]
    public int BlockHeight { get; init; }

    [JsonPropertyName("pixelSizeX")]
    public double? PixelSizeX { get; init; }

    [JsonPropertyName("pixelSizeY")]
    public double? PixelSizeY { get; init; }

    [JsonPropertyName("extent")]
    public ImageServerExtent? Extent { get; init; }

    [JsonPropertyName("bandCount")]
    public int BandCount { get; init; }

    [JsonPropertyName("pixelType")]
    public required string PixelType { get; init; }

    [JsonPropertyName("firstPyramidLevel")]
    public int FirstPyramidLevel { get; init; }

    [JsonPropertyName("maxPyramidLevel")]
    public int MaxPyramidLevel { get; init; }
}

public sealed class RasterItemImageSupportDataResponse
{
    [JsonPropertyName("rasterId")]
    public long RasterId { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("sensorName")]
    public string? SensorName { get; init; }

    [JsonPropertyName("cameraModel")]
    public string? CameraModel { get; init; }

    [JsonPropertyName("hasInteriorOrientation")]
    public bool HasInteriorOrientation { get; init; }

    [JsonPropertyName("hasExteriorOrientation")]
    public bool HasExteriorOrientation { get; init; }

    [JsonPropertyName("hasRationalPolynomialCoefficients")]
    public bool HasRationalPolynomialCoefficients { get; init; }

    [JsonPropertyName("demSource")]
    public string? DemSource { get; init; }
}
