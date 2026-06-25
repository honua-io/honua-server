// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Honua.Protocols.GeoServices.FeatureServer.Models;

/// <summary>
/// GeoServices geometry representation (point)
/// </summary>
public sealed class GeoServicesGeometry
{
    /// <summary>
    /// Indicates whether the geometry includes Z values
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    [JsonPropertyName("hasZ")]
    public bool HasZ { get; init; }

    /// <summary>
    /// Indicates whether the geometry includes M values
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    [JsonPropertyName("hasM")]
    public bool HasM { get; init; }

    /// <summary>
    /// X coordinate (longitude)
    /// </summary>
    public double? X { get; init; }

    /// <summary>
    /// Y coordinate (latitude)
    /// </summary>
    public double? Y { get; init; }

    /// <summary>
    /// Z coordinate (elevation)
    /// </summary>
    public double? Z { get; init; }

    /// <summary>
    /// Measure value
    /// </summary>
    public double? M { get; init; }

    /// <summary>
    /// Envelope minimum X
    /// </summary>
    [JsonPropertyName("xmin")]
    public double? Xmin { get; init; }

    /// <summary>
    /// Envelope minimum Y
    /// </summary>
    [JsonPropertyName("ymin")]
    public double? Ymin { get; init; }

    /// <summary>
    /// Envelope maximum X
    /// </summary>
    [JsonPropertyName("xmax")]
    public double? Xmax { get; init; }

    /// <summary>
    /// Envelope maximum Y
    /// </summary>
    [JsonPropertyName("ymax")]
    public double? Ymax { get; init; }

    /// <summary>
    /// MultiPoint coordinates
    /// </summary>
    public double[][]? Points { get; init; }

    /// <summary>
    /// Polyline paths
    /// </summary>
    public double[][][]? Paths { get; init; }

    /// <summary>
    /// Polygon rings
    /// </summary>
    public double[][][]? Rings { get; init; }

    /// <summary>
    /// Esri true-curve polyline paths. Each path is an array whose elements are
    /// either a plain <c>[x, y(, z)(, m)]</c> vertex (a JSON array) or a curve-segment
    /// object: <c>{"c": [[endX, endY], [centerX, centerY]]}</c> (circular arc),
    /// <c>{"b": [[endX, endY], [ctrl1X, ctrl1Y], [ctrl2X, ctrl2Y]]}</c> (cubic Bézier),
    /// or <c>{"a": [...]}</c> (elliptic arc — parsed but not densified). Stored as raw
    /// <see cref="JsonElement"/> so the heterogeneous vertex/segment shape round-trips
    /// losslessly and is AOT-safe under the source-generated JSON context (#1877 Part A/B).
    /// </summary>
    [JsonPropertyName("curvePaths")]
    public JsonElement[][]? CurvePaths { get; init; }

    /// <summary>
    /// Esri true-curve polygon rings. Same element grammar as
    /// <see cref="CurvePaths"/> (mixed vertex/segment arrays), used for curved polygon
    /// boundaries (#1877 Part A/B).
    /// </summary>
    [JsonPropertyName("curveRings")]
    public JsonElement[][]? CurveRings { get; init; }

    /// <summary>
    /// Spatial reference information
    /// </summary>
    public GeoServicesSpatialReference? SpatialReference { get; init; }
}
