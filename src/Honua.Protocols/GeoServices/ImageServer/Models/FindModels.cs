// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Protocols.GeoServices.ImageServer.Models;

public sealed class ImageServerFindResponse
{
    [JsonPropertyName("images")]
    public ImageServerFindImage[] Images { get; init; } = [];
}

public sealed class ImageServerFindImage
{
    [JsonPropertyName("id")]
    public long Id { get; init; }

    [JsonPropertyName("uri")]
    public string? Uri { get; init; }

    [JsonPropertyName("acquisitionDate")]
    public long? AcquisitionDate { get; init; }

    [JsonPropertyName("center")]
    public ImageServerFindPoint? Center { get; init; }

    [JsonPropertyName("pixelSize")]
    public double? PixelSize { get; init; }

    [JsonPropertyName("rows")]
    public int Rows { get; init; }

    [JsonPropertyName("cols")]
    public int Cols { get; init; }
}

public sealed class ImageServerFindPoint
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
