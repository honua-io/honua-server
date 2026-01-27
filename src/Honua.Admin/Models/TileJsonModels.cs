// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Admin.Models;

public sealed class TileJsonResponse
{
    [JsonPropertyName("tilejson")]
    public string? TileJson { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("bounds")]
    public double[]? Bounds { get; init; }

    [JsonPropertyName("center")]
    public double[]? Center { get; init; }
}
