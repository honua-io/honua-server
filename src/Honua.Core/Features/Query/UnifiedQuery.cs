// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Shared.Models;
using Honua.Core.Queries.Filters;

namespace Honua.Core.Features.Query;

/// <summary>
/// Unified query model that supports all protocol features while maintaining clean separation.
/// Acts as a common abstraction for query semantics across WFS, OGC API Features, GeoServices, and OData.
/// </summary>
public readonly record struct UnifiedQuery
{
    /// <summary>
    /// Attribute filter conditions (combined from various protocol filter syntaxes).
    /// </summary>
    public QueryFilter? Filter { get; init; }

    /// <summary>
    /// Spatial filtering criteria.
    /// </summary>
    public SpatialFilter? SpatialFilter { get; init; }

    /// <summary>
    /// Temporal filtering criteria.
    /// </summary>
    public TemporalFilter? TemporalFilter { get; init; }

    /// <summary>
    /// Specific object IDs to retrieve (for by-ID queries).
    /// </summary>
    public ImmutableArray<long>? ObjectIds { get; init; }

    /// <summary>
    /// Field selection for projection (null = all fields, empty = no attribute fields).
    /// </summary>
    public ImmutableArray<string>? OutFields { get; init; }

    /// <summary>
    /// Pagination: Number of records to skip.
    /// </summary>
    public int? Offset { get; init; }

    /// <summary>
    /// Pagination: Maximum number of records to return.
    /// </summary>
    public int? Limit { get; init; }

    /// <summary>
    /// Ordering specifications for result sorting.
    /// </summary>
    public ImmutableArray<OrderByClause>? OrderBy { get; init; }

    /// <summary>
    /// Output coordinate reference system.
    /// </summary>
    public QueryCrs? OutputCrs { get; init; }

    /// <summary>
    /// Aggregation specifications (for statistics queries).
    /// </summary>
    public QueryAggregation? Aggregation { get; init; }

    /// <summary>
    /// Query execution hints for optimization.
    /// </summary>
    public QueryHints? Hints { get; init; }

    /// <summary>
    /// Protocol-specific extensions that don't fit the unified model.
    /// </summary>
    public ImmutableDictionary<string, object>? Extensions { get; init; }

    /// <summary>
    /// Creates a simple filter query.
    /// </summary>
    /// <param name="filter">Query filter</param>
    /// <returns>Unified query</returns>
    public static UnifiedQuery WithFilter(QueryFilter filter)
        => new() { Filter = filter };

    /// <summary>
    /// Creates a spatial query.
    /// </summary>
    /// <param name="spatialFilter">Spatial filter</param>
    /// <returns>Unified query</returns>
    public static UnifiedQuery WithSpatialFilter(SpatialFilter spatialFilter)
        => new() { SpatialFilter = spatialFilter };

    /// <summary>
    /// Creates a paginated query.
    /// </summary>
    /// <param name="offset">Records to skip</param>
    /// <param name="limit">Maximum records</param>
    /// <returns>Unified query</returns>
    public static UnifiedQuery WithPagination(int offset, int limit)
        => new() { Offset = offset, Limit = limit };

    /// <summary>
    /// Creates an object ID query.
    /// </summary>
    /// <param name="objectIds">Object IDs to retrieve</param>
    /// <returns>Unified query</returns>
    public static UnifiedQuery WithObjectIds(ImmutableArray<long> objectIds)
        => new() { ObjectIds = objectIds };

    /// <summary>
    /// Checks if this is an empty query (no filters or constraints).
    /// </summary>
    public bool IsEmpty => Filter == null &&
                          SpatialFilter == null &&
                          TemporalFilter == null &&
                          ObjectIds?.IsDefaultOrEmpty != false;

    /// <summary>
    /// Checks if this query has any filtering conditions.
    /// </summary>
    public bool HasFilters => Filter != null ||
                             SpatialFilter != null ||
                             TemporalFilter != null ||
                             ObjectIds?.IsDefaultOrEmpty == false;

    /// <summary>
    /// Checks if this query requires aggregation.
    /// </summary>
    public bool RequiresAggregation => Aggregation != null &&
                                      (Aggregation.Value.Statistics?.IsDefaultOrEmpty == false ||
                                       Aggregation.Value.GroupByFields?.IsDefaultOrEmpty == false);

    /// <summary>
    /// Gets the effective limit considering defaults.
    /// </summary>
    /// <param name="defaultLimit">Default limit if none specified</param>
    /// <returns>Effective limit</returns>
    public int GetEffectiveLimit(int defaultLimit) => Limit ?? defaultLimit;

    /// <summary>
    /// Gets the effective offset.
    /// </summary>
    /// <returns>Effective offset</returns>
    public int GetEffectiveOffset() => Offset ?? 0;
}

/// <summary>
/// Unified filter representation that can express filters from all protocols.
/// </summary>
public readonly record struct QueryFilter
{
    /// <summary>
    /// Filter expression tree (preferred for complex filters).
    /// </summary>
    public FilterExpression? Expression { get; init; }

    /// <summary>
    /// Raw SQL fragment (fallback for protocol-specific SQL).
    /// </summary>
    public SqlFragment? SqlFragment { get; init; }

    /// <summary>
    /// Original filter text and language for debugging/logging.
    /// </summary>
    public FilterSource? Source { get; init; }

    /// <summary>
    /// Creates a filter from a filter expression.
    /// </summary>
    /// <param name="expression">Filter expression</param>
    /// <param name="source">Optional source information</param>
    /// <returns>Query filter</returns>
    public static QueryFilter FromExpression(FilterExpression expression, FilterSource? source = null)
        => new() { Expression = expression, Source = source };

    /// <summary>
    /// Creates a filter from a SQL fragment.
    /// </summary>
    /// <param name="sqlFragment">SQL fragment</param>
    /// <param name="source">Optional source information</param>
    /// <returns>Query filter</returns>
    public static QueryFilter FromSql(SqlFragment sqlFragment, FilterSource? source = null)
        => new() { SqlFragment = sqlFragment, Source = source };

    /// <summary>
    /// Gets the best available filter representation.
    /// </summary>
    /// <returns>Filter expression if available, null otherwise</returns>
    public FilterExpression? GetFilterExpression() => Expression;

    /// <summary>
    /// Gets the SQL fragment representation.
    /// </summary>
    /// <returns>SQL fragment if available, null otherwise</returns>
    public SqlFragment? GetSqlFragment() => SqlFragment;
}

/// <summary>
/// Source information for a filter.
/// </summary>
public readonly record struct FilterSource
{
    /// <summary>
    /// Original filter text.
    /// </summary>
    public string Text { get; init; }

    /// <summary>
    /// Filter language/syntax.
    /// </summary>
    public FilterLanguage Language { get; init; }

    /// <summary>
    /// Protocol that provided this filter.
    /// </summary>
    public string Protocol { get; init; }

    /// <summary>
    /// Creates filter source information.
    /// </summary>
    /// <param name="text">Original filter text</param>
    /// <param name="language">Filter language</param>
    /// <param name="protocol">Source protocol</param>
    [SetsRequiredMembers]
    public FilterSource(string text, FilterLanguage language, string protocol)
    {
        Text = text;
        Language = language;
        Protocol = protocol;
    }
}

/// <summary>
/// Coordinate reference system specification for query output.
/// </summary>
public readonly record struct QueryCrs
{
    /// <summary>
    /// SRID for output geometry transformation.
    /// </summary>
    public int? Srid { get; init; }

    /// <summary>
    /// Axis order for output coordinates.
    /// </summary>
    public AxisOrder? AxisOrder { get; init; }

    /// <summary>
    /// CRS URI for standards compliance.
    /// </summary>
    public string? Uri { get; init; }

    /// <summary>
    /// Creates a CRS specification.
    /// </summary>
    /// <param name="srid">SRID</param>
    /// <param name="axisOrder">Axis order</param>
    /// <param name="uri">CRS URI</param>
    /// <returns>Query CRS</returns>
    public static QueryCrs Create(int srid, AxisOrder? axisOrder = null, string? uri = null)
        => new() { Srid = srid, AxisOrder = axisOrder, Uri = uri };
}

/// <summary>
/// Aggregation specifications for statistical queries.
/// </summary>
public readonly record struct QueryAggregation
{
    /// <summary>
    /// Statistical calculations to perform.
    /// </summary>
    public ImmutableArray<StatisticDefinition>? Statistics { get; init; }

    /// <summary>
    /// Fields to group results by.
    /// </summary>
    public ImmutableArray<string>? GroupByFields { get; init; }

    /// <summary>
    /// Filter to apply after grouping (HAVING clause equivalent).
    /// </summary>
    public QueryFilter? HavingFilter { get; init; }

    /// <summary>
    /// Whether to return only distinct combinations.
    /// </summary>
    public bool Distinct { get; init; }

    /// <summary>
    /// Creates an aggregation specification.
    /// </summary>
    /// <param name="statistics">Statistics to calculate</param>
    /// <param name="groupByFields">Grouping fields</param>
    /// <param name="distinct">Whether to use DISTINCT</param>
    /// <returns>Query aggregation</returns>
    public static QueryAggregation Create(
        ImmutableArray<StatisticDefinition>? statistics = null,
        ImmutableArray<string>? groupByFields = null,
        bool distinct = false)
        => new() { Statistics = statistics, GroupByFields = groupByFields, Distinct = distinct };
}

/// <summary>
/// Query execution hints for optimization.
/// </summary>
public readonly record struct QueryHints
{
    /// <summary>
    /// Prefer streaming for large result sets.
    /// </summary>
    public bool PreferStreaming { get; init; }

    /// <summary>
    /// Enable query result caching.
    /// </summary>
    public bool EnableCaching { get; init; }

    /// <summary>
    /// Estimated result size (for optimization).
    /// </summary>
    public long? EstimatedResultSize { get; init; }

    /// <summary>
    /// Whether an exact count is needed.
    /// </summary>
    public bool RequireExactCount { get; init; }

    /// <summary>
    /// Query timeout in milliseconds.
    /// </summary>
    public int? TimeoutMs { get; init; }

    /// <summary>
    /// Additional optimization hints.
    /// </summary>
    public ImmutableDictionary<string, object>? OptimizationHints { get; init; }

    /// <summary>
    /// Creates query hints.
    /// </summary>
    /// <param name="preferStreaming">Prefer streaming</param>
    /// <param name="enableCaching">Enable caching</param>
    /// <param name="requireExactCount">Require exact count</param>
    /// <returns>Query hints</returns>
    public static QueryHints Create(
        bool preferStreaming = false,
        bool enableCaching = true,
        bool requireExactCount = true)
        => new() { PreferStreaming = preferStreaming, EnableCaching = enableCaching, RequireExactCount = requireExactCount };
}