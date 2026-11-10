// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Protocols.GeoServices.FeatureServer.Models;

public sealed class ServiceGetEstimatesResponse
{
    [JsonPropertyName("layers")]
    public ServiceLayerEstimateInfo[] Layers { get; init; } = [];
}

public sealed class ServiceLayerEstimateInfo
{
    [JsonPropertyName("id")]
    public int Id { get; init; }

    [JsonPropertyName("count")]
    public long Count { get; init; }

    [JsonPropertyName("extent")]
    public ExtentInfo? Extent { get; init; }
}
