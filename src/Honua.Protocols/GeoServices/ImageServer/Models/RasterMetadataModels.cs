// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Protocols.GeoServices.ImageServer.Models;

public sealed class StatisticsResourceResponse
{
    [JsonPropertyName("statistics")]
    public StatisticsEntry[] Statistics { get; init; } = [];
}

public sealed class StatisticsEntry
{
    [JsonPropertyName("min")]
    public double Min { get; init; }

    [JsonPropertyName("max")]
    public double Max { get; init; }

    [JsonPropertyName("mean")]
    public double Mean { get; init; }

    [JsonPropertyName("standardDeviation")]
    public double StandardDeviation { get; init; }

    [JsonPropertyName("count")]
    public long Count { get; init; }
}

public sealed class HistogramsResourceResponse
{
    [JsonPropertyName("histograms")]
    public BandHistogram[] Histograms { get; init; } = [];
}

public sealed class ColormapResourceResponse
{
    [JsonPropertyName("colormap")]
    public int[][] Colormap { get; init; } = [];
}

public sealed class RasterFunctionInfosResponse
{
    [JsonPropertyName("rasterFunctionInfos")]
    public RasterFunctionInfoEntry[] RasterFunctionInfos { get; init; } = [];
}

public sealed class RasterFunctionInfoEntry
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; init; } = string.Empty;

    [JsonPropertyName("help")]
    public string Help { get; init; } = string.Empty;
}

public sealed class RasterAttributeTableResponse
{
    [JsonPropertyName("objectIdFieldName")]
    public string ObjectIdFieldName { get; init; } = "OBJECTID";

    [JsonPropertyName("fields")]
    public RasterAttributeTableField[] Fields { get; init; } = [];

    [JsonPropertyName("features")]
    public RasterAttributeTableFeature[] Features { get; init; } = [];
}

public sealed class RasterAttributeTableField
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; init; } = string.Empty;

    [JsonPropertyName("alias")]
    public string Alias { get; init; } = string.Empty;
}

public sealed class RasterAttributeTableFeature
{
    [JsonPropertyName("attributes")]
    public Dictionary<string, object?> Attributes { get; init; } = new();
}
