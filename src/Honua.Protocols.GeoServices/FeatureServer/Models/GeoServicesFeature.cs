// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Protocols.GeoServices.FeatureServer.Models;

/// <summary>
/// GeoServices feature representation
/// </summary>
[JsonConverter(typeof(GeoServicesFeatureJsonConverter))]
public sealed class GeoServicesFeature
{
    /// <summary>
    /// Feature attributes as key-value pairs
    /// </summary>
    public required Dictionary<string, object?> Attributes { get; init; }

    /// <summary>
    /// Feature geometry (optional if returnGeometry=false)
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public GeoServicesGeometry? Geometry { get; init; }

    /// <summary>
    /// Controls whether the geometry property is emitted when null.
    /// </summary>
    [JsonIgnore]
    public bool IncludeGeometry { get; init; } = true;
}
