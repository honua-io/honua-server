// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Protocols.GeoServices.ImageServer.Models;

public sealed class ImageServerMeasureResponse
{
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("sensorName")]
    public string SensorName { get; init; } = "Unknown";

    [JsonPropertyName("distance")]
    public ImageServerMeasureValue? Distance { get; init; }

    [JsonPropertyName("azimuthAngle")]
    public ImageServerMeasureValue? AzimuthAngle { get; init; }

    [JsonPropertyName("height")]
    public ImageServerMeasureValue? Height { get; init; }

    [JsonPropertyName("area")]
    public ImageServerMeasureValue? Area { get; init; }

    [JsonPropertyName("perimeter")]
    public ImageServerMeasureValue? Perimeter { get; init; }

    [JsonPropertyName("point")]
    public ImageServerMeasurePointResult? Point { get; init; }
}

public sealed class ImageServerMeasureValue
{
    [JsonPropertyName("value")]
    public double Value { get; init; }

    [JsonPropertyName("displayValue")]
    public required string DisplayValue { get; init; }

    [JsonPropertyName("uncertainty")]
    public double Uncertainty { get; init; } = -1d;

    [JsonPropertyName("unit")]
    public required string Unit { get; init; }
}

public sealed class ImageServerMeasurePointResult
{
    [JsonPropertyName("value")]
    public required ImageServerMeasurePoint Value { get; init; }
}

public sealed class ImageServerMeasurePoint
{
    [JsonPropertyName("x")]
    public required double X { get; init; }

    [JsonPropertyName("y")]
    public required double Y { get; init; }

    [JsonPropertyName("z")]
    public double? Z { get; init; }

    [JsonPropertyName("spatialReference")]
    public SpatialReference? SpatialReference { get; init; }
}
