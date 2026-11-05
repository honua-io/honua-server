// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Server.Features.Protocols.SpatialAnalytics.Models;

internal sealed class SpatialAnalyticsFeatureCollection
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "FeatureCollection";

    [JsonPropertyName("features")]
    public SpatialAnalyticsFeature[] Features { get; set; } = [];

    [JsonPropertyName("numberReturned")]
    public int NumberReturned { get; set; }

    [JsonPropertyName("metadata")]
    public SpatialAnalyticsMetadata? Metadata { get; set; }
}

internal sealed class SpatialAnalyticsFeature
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "Feature";

    [JsonPropertyName("geometry")]
    public SpatialAnalyticsGeometry? Geometry { get; set; }

    [JsonPropertyName("properties")]
    public Dictionary<string, object?> Properties { get; set; } = [];
}

internal sealed class SpatialAnalyticsMetadata
{
    [JsonPropertyName("operation")]
    public string Operation { get; set; } = string.Empty;

    [JsonPropertyName("inputTruncated")]
    public bool InputTruncated { get; set; }

    [JsonPropertyName("resultTruncated")]
    public bool ResultTruncated { get; set; }

    [JsonPropertyName("maxInputFeatures")]
    public int MaxInputFeatures { get; set; }

    [JsonPropertyName("maxOutputRows")]
    public int? MaxOutputRows { get; set; }
}
