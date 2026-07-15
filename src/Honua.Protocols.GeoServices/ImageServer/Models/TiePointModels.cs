// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Protocols.GeoServices.ImageServer.Models;

/// <summary>
/// Response for the ImageServer <c>computeTiePoints</c> operation. Mirrors the documented
/// ArcGIS Enterprise REST response shape: a single <c>tiePoints</c> object carrying
/// index-aligned <c>sourcePoints</c> (image/pixel points) and <c>targetPoints</c>
/// (reference/ground points). See ADR-0065.
/// </summary>
public sealed class ImageServerComputeTiePointsResponse
{
    /// <summary>
    /// The computed tie-point set. In this slice these are a faithful pass-through of the
    /// raster's pre-registered control points; automatic feature matching is not performed
    /// (ADR-0065).
    /// </summary>
    [JsonPropertyName("tiePoints")]
    public required ImageServerTiePointSet TiePoints { get; init; }
}

/// <summary>
/// Index-aligned source/target tie-point arrays returned by <c>computeTiePoints</c>.
/// <c>SourcePoints[i]</c> is the image/pixel location and <c>TargetPoints[i]</c> is the
/// corresponding reference/ground location for the same control point.
/// </summary>
public sealed class ImageServerTiePointSet
{
    /// <summary>
    /// Source (image/pixel-space) points: <c>x</c> = sample/column, <c>y</c> = line/row.
    /// </summary>
    [JsonPropertyName("sourcePoints")]
    public required IReadOnlyList<ImageServerTiePoint> SourcePoints { get; init; }

    /// <summary>
    /// Target (reference/ground-space) points carrying the resolved spatial reference.
    /// </summary>
    [JsonPropertyName("targetPoints")]
    public required IReadOnlyList<ImageServerTiePoint> TargetPoints { get; init; }
}

/// <summary>
/// A single tie point returned by <c>computeTiePoints</c>.
/// </summary>
public sealed class ImageServerTiePoint
{
    /// <summary>
    /// X coordinate (sample/column for source points; map X for target points).
    /// </summary>
    [JsonPropertyName("x")]
    public required double X { get; init; }

    /// <summary>
    /// Y coordinate (line/row for source points; map Y for target points).
    /// </summary>
    [JsonPropertyName("y")]
    public required double Y { get; init; }

    /// <summary>
    /// Optional Z (elevation) coordinate for reference/ground points.
    /// </summary>
    [JsonPropertyName("z")]
    public double? Z { get; init; }

    /// <summary>
    /// Spatial reference of the point. Present on reference/ground (target) points;
    /// omitted for image/pixel-space (source) points.
    /// </summary>
    [JsonPropertyName("spatialReference")]
    public SpatialReference? SpatialReference { get; init; }
}
