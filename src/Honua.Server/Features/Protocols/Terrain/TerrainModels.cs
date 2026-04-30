// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Server.Features.Protocols.Terrain;

internal sealed record TerrainMetadataResponse
{
    [JsonPropertyName("tilejson")]
    public string TileJson { get; init; } = "3.0.0";

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("scheme")]
    public string Scheme { get; init; } = "xyz";

    [JsonPropertyName("tiles")]
    public required string[] Tiles { get; init; }

    [JsonPropertyName("minzoom")]
    public int MinZoom { get; init; }

    [JsonPropertyName("maxzoom")]
    public int MaxZoom { get; init; }

    [JsonPropertyName("bounds")]
    public double[]? Bounds { get; init; }

    [JsonPropertyName("center")]
    public double[]? Center { get; init; }

    [JsonPropertyName("format")]
    public string Format { get; init; } = "terrain-rgb";

    [JsonPropertyName("encoding")]
    public required TerrainEncodingMetadata Encoding { get; init; }

    [JsonPropertyName("source")]
    public required TerrainSourceMetadata Source { get; init; }

    [JsonPropertyName("noData")]
    public required TerrainNoDataMetadata NoData { get; init; }

    [JsonPropertyName("supported")]
    public bool Supported { get; init; }

    [JsonPropertyName("unsupportedReasons")]
    public string[] UnsupportedReasons { get; init; } = [];
}

internal sealed record TerrainEncodingMetadata
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = "mapbox-terrain-rgb";

    [JsonPropertyName("formula")]
    public string Formula { get; init; } = "elevationMeters = -10000 + ((R * 256 * 256 + G * 256 + B) * 0.1)";

    [JsonPropertyName("units")]
    public string Units { get; init; } = "meters";

    [JsonPropertyName("tileSize")]
    public int TileSize { get; init; } = 256;
}

internal sealed record TerrainSourceMetadata
{
    [JsonPropertyName("datasetId")]
    public required string DatasetId { get; init; }

    [JsonPropertyName("layerId")]
    public int LayerId { get; init; }

    [JsonPropertyName("rasterIds")]
    public required long[] RasterIds { get; init; }

    [JsonPropertyName("rasterCount")]
    public int RasterCount { get; init; }

    [JsonPropertyName("sourceCrs")]
    public string? SourceCrs { get; init; }

    [JsonPropertyName("sourceSrid")]
    public int? SourceSrid { get; init; }

    [JsonPropertyName("sourceExtent")]
    public TerrainExtentMetadata? SourceExtent { get; init; }

    [JsonPropertyName("pixelType")]
    public string? PixelType { get; init; }

    [JsonPropertyName("bandCount")]
    public int? BandCount { get; init; }

    [JsonPropertyName("verticalUnit")]
    public string? VerticalUnit { get; init; }

    [JsonPropertyName("verticalDatum")]
    public string? VerticalDatum { get; init; }

    [JsonPropertyName("verticalUnitAssumption")]
    public string VerticalUnitAssumption { get; init; } = "Source values are encoded as meters when no vertical unit is declared.";
}

internal sealed record TerrainExtentMetadata
{
    [JsonPropertyName("xmin")]
    public double XMin { get; init; }

    [JsonPropertyName("ymin")]
    public double YMin { get; init; }

    [JsonPropertyName("xmax")]
    public double XMax { get; init; }

    [JsonPropertyName("ymax")]
    public double YMax { get; init; }

    [JsonPropertyName("srid")]
    public int? Srid { get; init; }
}

internal sealed record TerrainNoDataMetadata
{
    [JsonPropertyName("sourceNoDataValue")]
    public double? SourceNoDataValue { get; init; }

    [JsonPropertyName("terrainRgbSentinelMeters")]
    public double TerrainRgbSentinelMeters { get; init; }

    [JsonPropertyName("terrainRgbSentinel")]
    public int[] TerrainRgbSentinel { get; init; } = [0, 0, 0];

    [JsonPropertyName("semantics")]
    public string Semantics { get; init; } = "Source no-data and uncovered pixels are encoded as opaque Terrain-RGB [0,0,0] (-10000m).";
}
