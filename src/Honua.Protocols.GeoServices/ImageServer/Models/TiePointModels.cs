// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Protocols.GeoServices.ImageServer.Models;

public sealed class ImageServerComputeTiePointsResponse
{
    [JsonPropertyName("tiePoints")]
    public required ImageServerTiePointSet TiePoints { get; init; }
}

public sealed class ImageServerTiePointSet
{
    [JsonPropertyName("sourcePoints")]
    public required IReadOnlyList<ImageServerTiePoint> SourcePoints { get; init; }

    [JsonPropertyName("targetPoints")]
    public required IReadOnlyList<ImageServerTiePoint> TargetPoints { get; init; }
}

public sealed class ImageServerTiePoint
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
