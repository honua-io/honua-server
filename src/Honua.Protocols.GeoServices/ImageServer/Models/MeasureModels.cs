// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Protocols.GeoServices.ImageServer.Models;

/// <summary>
/// Response for the ImageServer <c>measure</c> operation.
/// </summary>
public sealed class ImageServerMeasureResponse
{
    /// <summary>
    /// Raster dataset name used for measurement.
    /// </summary>
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    /// <summary>
    /// Sensor name. Honua emits <c>Unknown</c> until sensor metadata is modeled.
    /// </summary>
    [JsonPropertyName("sensorName")]
    public string SensorName { get; init; } = "Unknown";

    /// <summary>
    /// Measured distance.
    /// </summary>
    [JsonPropertyName("distance")]
    public ImageServerMeasureValue? Distance { get; init; }

    /// <summary>
    /// Measured azimuth angle.
    /// </summary>
    [JsonPropertyName("azimuthAngle")]
    public ImageServerMeasureValue? AzimuthAngle { get; init; }

    /// <summary>
    /// Measured area.
    /// </summary>
    [JsonPropertyName("area")]
    public ImageServerMeasureValue? Area { get; init; }

    /// <summary>
    /// Measured perimeter.
    /// </summary>
    [JsonPropertyName("perimeter")]
    public ImageServerMeasureValue? Perimeter { get; init; }

    /// <summary>
    /// Point or centroid measurement result.
    /// </summary>
    [JsonPropertyName("point")]
    public ImageServerMeasurePointResult? Point { get; init; }
}

/// <summary>
/// Numeric mensuration value.
/// </summary>
public sealed class ImageServerMeasureValue
{
    /// <summary>
    /// Raw measured value in the requested unit.
    /// </summary>
    [JsonPropertyName("value")]
    public double Value { get; init; }

    /// <summary>
    /// Display value formatted for Esri clients.
    /// </summary>
    [JsonPropertyName("displayValue")]
    public required string DisplayValue { get; init; }

    /// <summary>
    /// Measurement uncertainty. Basic map-space measurements use -1.
    /// </summary>
    [JsonPropertyName("uncertainty")]
    public double Uncertainty { get; init; } = -1d;

    /// <summary>
    /// Esri unit identifier.
    /// </summary>
    [JsonPropertyName("unit")]
    public required string Unit { get; init; }
}

/// <summary>
/// Point measurement value wrapper.
/// </summary>
public sealed class ImageServerMeasurePointResult
{
    /// <summary>
    /// Point value.
    /// </summary>
    [JsonPropertyName("value")]
    public required ImageServerMeasurePoint Value { get; init; }
}

/// <summary>
/// Point returned by ImageServer measure operations.
/// </summary>
public sealed class ImageServerMeasurePoint
{
    /// <summary>
    /// X coordinate.
    /// </summary>
    [JsonPropertyName("x")]
    public required double X { get; init; }

    /// <summary>
    /// Y coordinate.
    /// </summary>
    [JsonPropertyName("y")]
    public required double Y { get; init; }

    /// <summary>
    /// Optional Z coordinate.
    /// </summary>
    [JsonPropertyName("z")]
    public double? Z { get; init; }

    /// <summary>
    /// Spatial reference.
    /// </summary>
    [JsonPropertyName("spatialReference")]
    public SpatialReference? SpatialReference { get; init; }
}
