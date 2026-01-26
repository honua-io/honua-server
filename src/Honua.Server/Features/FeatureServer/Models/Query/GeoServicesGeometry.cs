// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Server.Features.FeatureServer.Models;

/// <summary>
/// GeoServices geometry representation (point)
/// </summary>
public sealed class GeoServicesGeometry
{
    /// <summary>
    /// Indicates whether the geometry includes Z values
    /// </summary>
    [JsonPropertyName("hasZ")]
    public bool? HasZ { get; init; }

    /// <summary>
    /// Indicates whether the geometry includes M values
    /// </summary>
    [JsonPropertyName("hasM")]
    public bool? HasM { get; init; }

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
    /// Spatial reference information
    /// </summary>
    public GeoServicesSpatialReference? SpatialReference { get; init; }
}
