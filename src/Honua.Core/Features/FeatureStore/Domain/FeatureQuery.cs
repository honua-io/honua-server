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
    /// Creates a spatial filter
    /// </summary>
    /// <param name="geometry">Filter geometry in WKB format</param>
    /// <param name="spatialRelationship">Type of spatial relationship</param>
    /// <returns>Spatial filter instance</returns>
    public static SpatialFilter Create(byte[] geometry, SpatialRelationship spatialRelationship)
        => new() { Geometry = geometry, SpatialRelationship = spatialRelationship };
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
    EnvelopeIntersects
}
