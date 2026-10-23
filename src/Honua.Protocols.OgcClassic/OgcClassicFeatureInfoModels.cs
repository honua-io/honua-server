// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Protocols.Ogc.Classic;

/// <summary>
/// WMS/WMTS GetFeatureInfo JSON response in the GeoJSON shape used by native clients.
/// </summary>
internal sealed class OgcClassicFeatureInfoResponse
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = "FeatureCollection";

    [JsonPropertyName("features")]
    public required OgcClassicFeatureInfoFeature[] Features { get; init; }
}

/// <summary>
/// Single WMS/WMTS GetFeatureInfo JSON result.
/// </summary>
internal sealed class OgcClassicFeatureInfoFeature
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = "Feature";

    // GetFeatureInfo currently returns attributes only. GeoJSON still requires
    // a geometry member; emit null explicitly despite the context's null policy.
    [JsonPropertyName("geometry")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public object? Geometry { get; init; }

    [JsonPropertyName("properties")]
    public Dictionary<string, object?> Properties => Attributes;

    // Retain the existing members as GeoJSON foreign members for consumers
    // that read layer and attributes directly from earlier feature-info responses.
    [JsonPropertyName("layer")]
    public required string Layer { get; init; }

    [JsonPropertyName("attributes")]
    public required Dictionary<string, object?> Attributes { get; init; }
}
