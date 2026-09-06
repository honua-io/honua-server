// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Protocols.Ogc.Classic.Wms;

/// <summary>
/// WMS GetFeatureInfo JSON response in the GeoJSON shape used by native clients.
/// </summary>
internal sealed class WmsFeatureInfoResponse
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = "FeatureCollection";

    [JsonPropertyName("features")]
    public required WmsFeatureInfoFeature[] Features { get; init; }
}

/// <summary>
/// Single WMS GetFeatureInfo JSON result.
/// </summary>
internal sealed class WmsFeatureInfoFeature
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
    // that read the earlier WMS response's layer and attributes directly.
    [JsonPropertyName("layer")]
    public required string Layer { get; init; }

    [JsonPropertyName("attributes")]
    public required Dictionary<string, object?> Attributes { get; init; }
}
