// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Server.Features.FeatureServer.Models;

/// <summary>
/// GeoJSON FeatureSet response for query endpoint
/// </summary>
public sealed class GeoJsonFeatureSet
{
    /// <summary>
    /// GeoJSON type - always "FeatureCollection"
    /// </summary>
    public string Type { get; init; } = "FeatureCollection";

    /// <summary>
    /// Array of GeoJSON features
    /// </summary>
    public GeoJsonFeature[] Features { get; init; } = [];

    /// <summary>
    /// Whether the transfer limit was exceeded
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool ExceededTransferLimit { get; init; }

    /// <summary>
    /// Additional properties (metadata)
    /// </summary>
    public Dictionary<string, object?>? Properties { get; init; }
}

/// <summary>
/// GeoJSON Feature representation
/// </summary>
public sealed class GeoJsonFeature
{
    /// <summary>
    /// GeoJSON type - always "Feature"
    /// </summary>
    public string Type { get; init; } = "Feature";

    /// <summary>
    /// Feature properties (attributes)
    /// </summary>
    public required Dictionary<string, object?> Properties { get; init; }

    /// <summary>
    /// Feature geometry (optional if returnGeometry=false)
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public GeoJsonGeometry? Geometry { get; init; }

    /// <summary>
    /// Feature ID (typically the objectid field)
    /// </summary>
    public object? Id { get; init; }
}

/// <summary>
/// GeoJSON Geometry representation
/// </summary>
public sealed class GeoJsonGeometry
{
    /// <summary>
    /// Geometry type (Point, LineString, Polygon, etc.)
    /// </summary>
    public required string Type { get; init; }

    /// <summary>
    /// Coordinate array - format depends on geometry type
    /// For Point: [x, y] or [x, y, z]
    /// For LineString: [[x, y], [x, y], ...]
    /// For Polygon: [[[x, y], [x, y], ...], ...]
    /// </summary>
    public required object? Coordinates { get; init; }

    /// <summary>
    /// Geometry collection members (only when Type=GeometryCollection)
    /// </summary>
    public GeoJsonGeometry[]? Geometries { get; init; }
}
