// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Protocols.Ogc.Classic.Wms;

/// <summary>
/// WMS GetFeatureInfo JSON response.
/// </summary>
internal sealed class WmsFeatureInfoResponse
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = "FeatureInfoResponse";

    [JsonPropertyName("features")]
    public required WmsFeatureInfoFeature[] Features { get; init; }
}

/// <summary>
/// Single WMS GetFeatureInfo JSON result.
/// </summary>
internal sealed class WmsFeatureInfoFeature
{
    [JsonPropertyName("layer")]
    public required string Layer { get; init; }

    [JsonPropertyName("attributes")]
    public required Dictionary<string, object?> Attributes { get; init; }
}
