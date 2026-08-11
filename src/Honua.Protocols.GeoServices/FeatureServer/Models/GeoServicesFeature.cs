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
    /// Polygon centroid returned when <c>returnCentroid=true</c>.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public GeoServicesGeometry? Centroid { get; init; }

    /// <summary>
    /// Controls whether the geometry property is emitted when null.
    /// </summary>
    [JsonIgnore]
    public bool IncludeGeometry { get; init; } = true;

    /// <summary>
    /// Internal edit intent that distinguishes restoring an explicit null geometry from an
    /// ordinary attribute-only update whose geometry was omitted.
    /// </summary>
    [JsonIgnore]
    internal bool ClearGeometry { get; init; }

    /// <summary>
    /// Internal complete-state intent: replace the stored attribute bag instead of applying the
    /// ordinary sparse-update overlay. Used only when conflict resolution restores a captured full
    /// server snapshot.
    /// </summary>
    [JsonIgnore]
    internal bool ReplaceAttributes { get; init; }
}
