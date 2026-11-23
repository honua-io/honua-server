// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

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

    [JsonPropertyName("attribution")]
    public string? Attribution { get; init; }

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

    [JsonPropertyName("coverage")]
    public required TerrainCoverageMetadata Coverage { get; init; }

    [JsonPropertyName("capabilities")]
    public required TerrainCapabilitiesMetadata Capabilities { get; init; }

    [JsonPropertyName("links")]
    public required TerrainLinkMetadata[] Links { get; init; }

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

internal sealed record TerrainCoverageMetadata
{
    [JsonPropertyName("wgs84Bounds")]
    public double[]? Wgs84Bounds { get; init; }

    [JsonPropertyName("minZoom")]
    public int MinZoom { get; init; }

    [JsonPropertyName("maxZoom")]
    public int MaxZoom { get; init; }

    [JsonPropertyName("rasterCount")]
    public int RasterCount { get; init; }

    [JsonPropertyName("hasCoverage")]
    public bool HasCoverage { get; init; }
}

internal sealed record TerrainCapabilitiesMetadata
{
    [JsonPropertyName("terrainRgb")]
    public required TerrainCapabilityStatus TerrainRgb { get; init; }

    [JsonPropertyName("quantizedMesh")]
    public required TerrainCapabilityStatus QuantizedMesh { get; init; }
}

internal sealed record TerrainCapabilityStatus
{
    [JsonPropertyName("capability")]
    public required string Capability { get; init; }

    [JsonPropertyName("format")]
    public required string Format { get; init; }

    [JsonPropertyName("available")]
    public bool Available { get; init; }

    [JsonPropertyName("status")]
    public required string Status { get; init; }

    [JsonPropertyName("href")]
    public string? Href { get; init; }

    [JsonPropertyName("reasons")]
    public string[] Reasons { get; init; } = [];
}

internal sealed record TerrainLinkMetadata
{
    [JsonPropertyName("rel")]
    public required string Rel { get; init; }

    [JsonPropertyName("href")]
    public required string Href { get; init; }

    [JsonPropertyName("type")]
    public required string Type { get; init; }

    [JsonPropertyName("format")]
    public required string Format { get; init; }

    [JsonPropertyName("title")]
    public required string Title { get; init; }
}
