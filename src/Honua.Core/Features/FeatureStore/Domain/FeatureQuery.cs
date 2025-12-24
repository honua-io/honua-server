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
    /// Order by clauses for sorting results (e.g., "name asc", "population desc")
    /// </summary>
    public ImmutableArray<OrderByClause>? OrderBy { get; init; }

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
    /// Creates a spatial filter
    /// </summary>
    /// <param name="geometry">Filter geometry in WKB format</param>
    /// <param name="spatialRelationship">Type of spatial relationship</param>
    /// <returns>Spatial filter instance</returns>
    public static SpatialFilter Create(byte[] geometry, SpatialRelationship spatialRelationship)
        => new() { Geometry = geometry, SpatialRelationship = spatialRelationship };
}

/// <summary>
/// Represents an order by clause for sorting results
/// </summary>
public readonly record struct OrderByClause
{
    /// <summary>
    /// Field name to sort by
    /// </summary>
    public required string Field { get; init; }

    /// <summary>
    /// Sort direction (true = ascending, false = descending)
    /// </summary>
    public bool Ascending { get; init; } = true;

    /// <summary>
    /// Initializes a new instance of the OrderByClause struct
    /// </summary>
    /// <param name="field">Field name to sort by</param>
    /// <param name="ascending">Sort direction (true = ascending, false = descending)</param>
    public OrderByClause(string field, bool ascending = true)
    {
        Field = field;
        Ascending = ascending;
    }

    /// <summary>
    /// Creates an ascending order by clause
    /// </summary>
    /// <param name="field">Field to sort by</param>
    /// <returns>Order by clause instance</returns>
    public static OrderByClause Asc(string field)
        => new() { Field = field, Ascending = true };

    /// <summary>
    /// Creates a descending order by clause
    /// </summary>
    /// <param name="field">Field to sort by</param>
    /// <returns>Order by clause instance</returns>
    public static OrderByClause Desc(string field)
        => new() { Field = field, Ascending = false };
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
