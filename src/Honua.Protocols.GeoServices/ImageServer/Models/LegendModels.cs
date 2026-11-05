// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Protocols.GeoServices.ImageServer.Models;

public sealed class LegendResponse
{
    [JsonPropertyName("layers")]
    public LegendLayer[] Layers { get; init; } = [];
}

public sealed class LegendLayer
{
    [JsonPropertyName("layerId")]
    public int LayerId { get; init; }

    [JsonPropertyName("layerName")]
    public required string LayerName { get; init; }

    [JsonPropertyName("layerType")]
    public string LayerType { get; init; } = "Raster Layer";

    [JsonPropertyName("minScale")]
    public double MinScale { get; init; }

    [JsonPropertyName("maxScale")]
    public double MaxScale { get; init; }

    [JsonPropertyName("legendType")]
    public string LegendType { get; init; } = "Stretched";

    [JsonPropertyName("legend")]
    public LegendEntry[] Legend { get; init; } = [];
}

public sealed class LegendEntry
{
    [JsonPropertyName("label")]
    public required string Label { get; init; }

    // Empty when the swatch bytes travel inline in ImageData.
    [JsonPropertyName("url")]
    public string Url { get; init; } = string.Empty;

    [JsonPropertyName("imageData")]
    public required string ImageData { get; init; }

    [JsonPropertyName("contentType")]
    public string ContentType { get; init; } = "image/png";

    [JsonPropertyName("height")]
    public int Height { get; init; }

    [JsonPropertyName("width")]
    public int Width { get; init; }

    [JsonPropertyName("values")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double[]? Values { get; init; }
}
