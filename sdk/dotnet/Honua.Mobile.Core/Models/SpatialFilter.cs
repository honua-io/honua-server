// Copyright (c) Honua. All rights reserved.
// Licensed under the Apache License 2.0. See LICENSE in the project root.

namespace Honua.Mobile.Core.Models;

/// <summary>
/// Represents a spatial filter for geometric queries.
/// </summary>
public sealed record SpatialFilter
{
    /// <summary>
    /// The geometry to use for the spatial operation.
    /// </summary>
    public Geometry Geometry { get; init; } = null!;

    /// <summary>
    /// The spatial relationship to test.
    /// </summary>
    public SpatialRelationship Relationship { get; init; } = SpatialRelationship.Intersects;

    /// <summary>
    /// The spatial reference of the filter geometry.
    /// </summary>
    public SpatialReference? SpatialReference { get; init; }

    /// <summary>
    /// Distance for distance-based operations.
    /// </summary>
    public double? Distance { get; init; }

    /// <summary>
    /// Units for the distance value.
    /// </summary>
    public DistanceUnit DistanceUnit { get; init; } = DistanceUnit.Meters;

    /// <summary>
    /// Number of nearest features to return (for nearest neighbor queries).
    /// </summary>
    public int? NearestCount { get; init; }

    /// <summary>
    /// Whether to return distance values in the results.
    /// </summary>
    public bool ReturnDistance { get; init; }

    /// <summary>
    /// Creates a spatial filter for intersection testing.
    /// </summary>
    public static SpatialFilter Intersects(Geometry geometry, SpatialReference? spatialReference = null)
    {
        return new SpatialFilter
        {
            Geometry = geometry,
            Relationship = SpatialRelationship.Intersects,
            SpatialReference = spatialReference
        };
    }

    /// <summary>
    /// Creates a spatial filter for within testing.
    /// </summary>
    public static SpatialFilter Within(Geometry geometry, SpatialReference? spatialReference = null)
    {
        return new SpatialFilter
        {
            Geometry = geometry,
            Relationship = SpatialRelationship.Within,
            SpatialReference = spatialReference
        };
    }

    /// <summary>
    /// Creates a spatial filter for distance-based queries.
    /// </summary>
    public static SpatialFilter WithinDistance(Geometry geometry, double distance, DistanceUnit unit = DistanceUnit.Meters, SpatialReference? spatialReference = null)
    {
        return new SpatialFilter
        {
            Geometry = geometry,
            Relationship = SpatialRelationship.WithinDistance,
            Distance = distance,
            DistanceUnit = unit,
            SpatialReference = spatialReference
        };
    }
}

/// <summary>
/// Spatial relationship types for filtering.
/// </summary>
public enum SpatialRelationship
{
    Intersects,
    Within,
    Contains,
    EnvelopeIntersects,
    Crosses,
    Touches,
    Overlaps,
    Disjoint,
    Equals,
    WithinDistance,
    BeyondDistance,
    NearestNeighbor
}

/// <summary>
/// Distance units for spatial operations.
/// </summary>
public enum DistanceUnit
{
    Meters,
    Feet,
    Kilometers,
    Miles
}