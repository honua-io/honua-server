// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Server.Features.Tiles;

internal sealed record TileJsonResponse
{
    [JsonPropertyName("tilejson")]
    public string TileJson { get; init; } = "3.0.0";

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("scheme")]
    public string? Scheme { get; init; } = "xyz";

    [JsonPropertyName("tiles")]
    public string[] Tiles { get; init; } = Array.Empty<string>();

    [JsonPropertyName("minzoom")]
    public int? MinZoom { get; init; }

    [JsonPropertyName("maxzoom")]
    public int? MaxZoom { get; init; }

    [JsonPropertyName("bounds")]
    public double[]? Bounds { get; init; }

    [JsonPropertyName("center")]
    public double[]? Center { get; init; }

    [JsonPropertyName("vector_layers")]
    public TileJsonVectorLayer[]? VectorLayers { get; init; }

    [JsonPropertyName("style")]
    public string? Style { get; init; }
}

internal sealed record TileJsonVectorLayer
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("fields")]
    public required Dictionary<string, string> Fields { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("minzoom")]
    public int? MinZoom { get; init; }

    [JsonPropertyName("maxzoom")]
    public int? MaxZoom { get; init; }
}
