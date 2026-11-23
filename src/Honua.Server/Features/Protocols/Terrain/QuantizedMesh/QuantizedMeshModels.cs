// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Server.Features.Protocols.Terrain.QuantizedMesh;

internal sealed record QuantizedMeshLayerMetadataResponse
{
    [JsonPropertyName("tilejson")]
    public string TileJson { get; init; } = "2.1.0";

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("attribution")]
    public string? Attribution { get; init; }

    [JsonPropertyName("version")]
    public string Version { get; init; } = "1.1.0";

    [JsonPropertyName("format")]
    public string Format { get; init; } = "quantized-mesh-1.0";

    [JsonPropertyName("scheme")]
    public string Scheme { get; init; } = "tms";

    [JsonPropertyName("projection")]
    public string Projection { get; init; } = "EPSG:4326";

    [JsonPropertyName("bounds")]
    public double[]? Bounds { get; init; }

    [JsonPropertyName("minzoom")]
    public int MinZoom { get; init; }

    [JsonPropertyName("maxzoom")]
    public int MaxZoom { get; init; }

    [JsonPropertyName("tiles")]
    public required string[] Tiles { get; init; }

    [JsonPropertyName("extensions")]
    public required string[] Extensions { get; init; }

    [JsonPropertyName("available")]
    public required QuantizedMeshTileRangeResponse[][] Available { get; init; }

    [JsonPropertyName("coverage")]
    public required TerrainCoverageMetadata Coverage { get; init; }

    [JsonPropertyName("capabilities")]
    public required TerrainCapabilitiesMetadata Capabilities { get; init; }

    [JsonPropertyName("links")]
    public required TerrainLinkMetadata[] Links { get; init; }

    [JsonPropertyName("source")]
    public required TerrainSourceMetadata Source { get; init; }

    [JsonPropertyName("supported")]
    public bool Supported { get; init; }

    [JsonPropertyName("unsupportedReasons")]
    public string[] UnsupportedReasons { get; init; } = [];
}

internal sealed record QuantizedMeshTileRangeResponse
{
    [JsonPropertyName("startX")]
    public int StartX { get; init; }

    [JsonPropertyName("startY")]
    public int StartY { get; init; }

    [JsonPropertyName("endX")]
    public int EndX { get; init; }

    [JsonPropertyName("endY")]
    public int EndY { get; init; }
}
