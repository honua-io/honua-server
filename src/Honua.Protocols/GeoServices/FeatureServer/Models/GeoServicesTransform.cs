// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Protocols.GeoServices.FeatureServer.Models;

public sealed class GeoServicesTransform
{
    [JsonPropertyName("originPosition")]
    public required string OriginPosition { get; init; }

    [JsonPropertyName("scale")]
    public required double[] Scale { get; init; }

    [JsonPropertyName("translate")]
    public required double[] Translate { get; init; }
}
