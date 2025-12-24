// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Core.Queries.Filters;

namespace Honua.Core.Features.FeatureStore.Domain;

/// <summary>
/// Represents a query specification for features
/// </summary>
public readonly record struct FeatureQuery
{
    /// <summary>
    /// WHERE clause filter expression (GeoServices REST SQL syntax)
    /// </summary>
    public string? Where { get; init; }

    /// <summary>
    /// Parameterized SQL filter with parameters (takes precedence over Where if provided)
    /// </summary>
    public SqlFragment? SqlFilter { get; init; }

    /// <summary>
    /// Fields to return (null means all fields)
    /// </summary>
    public ImmutableArray<string>? OutFields { get; init; }

    /// <summary>
    /// Spatial filter for geometry-based queries
    /// </summary>
    public SpatialFilter? SpatialFilter { get; init; }

    /// <summary>
    /// Temporal filter for time-based queries
    /// </summary>
    public TemporalFilter? TemporalFilter { get; init; }

    /// <summary>
    /// Include features without geometry when a spatial filter is provided
    /// </summary>
    public bool IncludeNullGeometry { get; init; }

    /// <summary>
    /// Number of records to skip for pagination
    /// </summary>
    public int? Offset { get; init; }

    /// <summary>
    /// Maximum number of records to return
    /// </summary>
    public int? Limit { get; init; }

    /// <summary>
    /// Creates a simple WHERE clause query
    /// </summary>
    /// <param name="where">WHERE clause expression</param>
    /// <returns>Feature query instance</returns>
    public static FeatureQuery WithWhere(string where)
        => new() { Where = where };

    /// <summary>
    /// Creates a parameterized SQL filter query
    /// </summary>
    /// <param name="sqlFilter">Parameterized SQL filter with parameters</param>
    /// <returns>Feature query instance</returns>
    public static FeatureQuery WithSqlFilter(SqlFragment sqlFilter)
        => new() { SqlFilter = sqlFilter };

    /// <summary>
    /// Creates a query with pagination
    /// </summary>
    /// <param name="offset">Number of records to skip</param>
    /// <param name="limit">Maximum number of records</param>
    /// <returns>Feature query instance</returns>
    public static FeatureQuery WithPaging(int offset, int limit)
        => new() { Offset = offset, Limit = limit };

    /// <summary>
    /// Creates a spatial query
    /// </summary>
    /// <param name="spatialFilter">Spatial filter specification</param>
    /// <returns>Feature query instance</returns>
    public static FeatureQuery WithSpatialFilter(SpatialFilter spatialFilter)
        => new() { SpatialFilter = spatialFilter };
}

/// <summary>
/// Temporal filtering criteria
/// </summary>
public readonly record struct TemporalFilter
{
    /// <summary>
    /// Name of the temporal property to filter on
    /// </summary>
    public required string PropertyName { get; init; }

    /// <summary>
    /// Type of the temporal property
    /// </summary>
    public required TemporalPropertyType PropertyType { get; init; }

    /// <summary>
    /// Inclusive start of the temporal interval (null for open start)
    /// </summary>
    public DateTimeOffset? Start { get; init; }

    /// <summary>
    /// Inclusive end of the temporal interval (null for open end)
    /// </summary>
    public DateTimeOffset? End { get; init; }
}

/// <summary>
/// Temporal property type for filtering
/// </summary>
public enum TemporalPropertyType
{
    DateTime,
    Date
}

/// <summary>
/// Represents spatial filtering criteria
/// </summary>
public readonly record struct SpatialFilter
{
    /// <summary>
    /// Geometry for spatial filtering in Well-Known Binary (WKB) format
    /// </summary>
    public required byte[] Geometry { get; init; }

    /// <summary>
    /// Spatial relationship type
    /// </summary>
    public required SpatialRelationship SpatialRelationship { get; init; }

    /// <summary>
    /// Distance value for distance-based spatial queries (WithinDistance, BeyondDistance).
    /// The unit is determined by the DistanceUnit property.
    /// </summary>
    public double? Distance { get; init; }

    /// <summary>
    /// Unit for distance measurements. Defaults to Meters.
    /// </summary>
    public DistanceUnit DistanceUnit { get; init; }

    /// <summary>
    /// Number of nearest neighbors to return for KNN queries.
    /// Only applicable when SpatialRelationship is NearestNeighbor.
    /// </summary>
    public int? NearestCount { get; init; }

    /// <summary>
    /// Whether to include the computed distance value in results for KNN queries.
    /// </summary>
    public bool ReturnDistance { get; init; }

    /// <summary>
    /// Creates a spatial filter
    /// </summary>
    /// <param name="geometry">Filter geometry in WKB format</param>
    /// <param name="spatialRelationship">Type of spatial relationship</param>
    /// <returns>Spatial filter instance</returns>
    public static SpatialFilter Create(byte[] geometry, SpatialRelationship spatialRelationship)
        => new() { Geometry = geometry, SpatialRelationship = spatialRelationship };

    /// <summary>
    /// Creates a distance-based spatial filter
    /// </summary>
    /// <param name="geometry">Filter geometry in WKB format</param>
    /// <param name="distance">Distance value</param>
    /// <param name="unit">Distance unit (defaults to Meters)</param>
    /// <param name="withinDistance">True for within distance, false for beyond distance</param>
    /// <returns>Spatial filter instance</returns>
    public static SpatialFilter CreateDistanceFilter(
        byte[] geometry,
        double distance,
        DistanceUnit unit = DistanceUnit.Meters,
        bool withinDistance = true)
        => new()
        {
            Geometry = geometry,
            SpatialRelationship = withinDistance ? SpatialRelationship.WithinDistance : SpatialRelationship.BeyondDistance,
            Distance = distance,
            DistanceUnit = unit
        };

    /// <summary>
    /// Creates a K-Nearest Neighbor (KNN) spatial filter
    /// </summary>
    /// <param name="geometry">Filter geometry in WKB format</param>
    /// <param name="count">Number of nearest neighbors to return</param>
    /// <param name="returnDistance">Whether to include distance values in results</param>
    /// <returns>Spatial filter instance</returns>
    public static SpatialFilter CreateKnnFilter(byte[] geometry, int count, bool returnDistance = false)
        => new()
        {
            Geometry = geometry,
            SpatialRelationship = SpatialRelationship.NearestNeighbor,
            NearestCount = count,
            ReturnDistance = returnDistance
        };
}

/// <summary>
/// Spatial relationship types for filtering
/// </summary>
public enum SpatialRelationship
{
    /// <summary>
    /// Features that intersect the filter geometry
    /// </summary>
    Intersects,

    /// <summary>
    /// Features completely within the filter geometry
    /// </summary>
    Within,

    /// <summary>
    /// Features that contain the filter geometry
    /// </summary>
    Contains,

    /// <summary>
    /// Features whose envelope intersects the filter geometry
    /// </summary>
    EnvelopeIntersects,

    /// <summary>
    /// Features within a specified distance of the filter geometry (ST_DWithin)
    /// </summary>
    WithinDistance,

    /// <summary>
    /// Features beyond a specified distance from the filter geometry
    /// </summary>
    BeyondDistance,

    /// <summary>
    /// K-Nearest Neighbor query - returns K closest features to the filter geometry
    /// </summary>
    NearestNeighbor
}

/// <summary>
/// Units for distance measurements in spatial queries
/// </summary>
public enum DistanceUnit
{
    /// <summary>
    /// Distance in meters (default for geography types)
    /// </summary>
    Meters,

    /// <summary>
    /// Distance in feet
    /// </summary>
    Feet,

    /// <summary>
    /// Distance in kilometers
    /// </summary>
    Kilometers,

    /// <summary>
    /// Distance in miles
    /// </summary>
    Miles
}
