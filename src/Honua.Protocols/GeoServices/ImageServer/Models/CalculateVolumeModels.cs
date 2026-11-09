// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Protocols.GeoServices.ImageServer.Models;

public sealed class CalculateVolumeResponse
{
    [JsonPropertyName("results")]
    public required IReadOnlyList<CalculateVolumeResult> Results { get; init; }
}

public sealed class CalculateVolumeResult
{
    [JsonPropertyName("area")]
    public required double Area { get; init; }

    [JsonPropertyName("cut")]
    public required double Cut { get; init; }

    [JsonPropertyName("fill")]
    public required double Fill { get; init; }

    [JsonPropertyName("minz")]
    public required double MinZ { get; init; }

    [JsonPropertyName("maxz")]
    public required double MaxZ { get; init; }

    [JsonPropertyName("meanz")]
    public required double MeanZ { get; init; }
}
