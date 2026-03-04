// Copyright (c) Honua. All rights reserved.
// Licensed under the Apache License 2.0. See LICENSE in the project root.

using Honua.Mobile.Core.Models;

namespace Honua.Mobile.Core.Querying;

/// <summary>
/// Fluent interface for building feature queries with method chaining.
/// Provides a developer-friendly way to construct complex geospatial queries.
/// </summary>
public sealed class FeatureQueryBuilder
{
    private readonly FeatureQuery _query;

    /// <summary>
    /// Initializes a new query builder.
    /// </summary>
    public FeatureQueryBuilder() : this(FeatureQuery.Empty) { }

    /// <summary>
    /// Initializes a new query builder with an existing query as the base.
    /// </summary>
    /// <param name="baseQuery">The base query to build upon</param>
    public FeatureQueryBuilder(FeatureQuery baseQuery)
    {
        _query = baseQuery ?? FeatureQuery.Empty;
    }

    /// <summary>
    /// Sets a WHERE clause for attribute-based filtering.
    /// </summary>
    /// <param name="whereClause">SQL-like WHERE clause (e.g., "STATUS = 'Active' AND PRIORITY > 5")</param>
    /// <returns>The query builder for method chaining</returns>
    public FeatureQueryBuilder Where(string whereClause)
    {
        return new FeatureQueryBuilder(_query with { Where = whereClause });
    }

    /// <summary>
    /// Filters by specific object IDs.
    /// </summary>
    /// <param name="objectIds">Object IDs to include in the query</param>
    /// <returns>The query builder for method chaining</returns>
    public FeatureQueryBuilder WithObjectIds(params long[] objectIds)
    {
        return WithObjectIds(objectIds.AsEnumerable());
    }

    /// <summary>
    /// Filters by specific object IDs.
    /// </summary>
    /// <param name="objectIds">Object IDs to include in the query</param>
    /// <returns>The query builder for method chaining</returns>
    public FeatureQueryBuilder WithObjectIds(IEnumerable<long> objectIds)
    {
        return new FeatureQueryBuilder(_query with { ObjectIds = objectIds.ToList() });
    }

    /// <summary>
    /// Specifies which fields to return in the results.
    /// </summary>
    /// <param name="fields">Field names to include (e.g., "NAME", "STATUS", "CREATED_DATE")</param>
    /// <returns>The query builder for method chaining</returns>
    public FeatureQueryBuilder WithFields(params string[] fields)
    {
        return WithFields(fields.AsEnumerable());
    }

    /// <summary>
    /// Specifies which fields to return in the results.
    /// </summary>
    /// <param name="fields">Field names to include</param>
    /// <returns>The query builder for method chaining</returns>
    public FeatureQueryBuilder WithFields(IEnumerable<string> fields)
    {
        return new FeatureQueryBuilder(_query with { OutFields = fields.ToList() });
    }

    /// <summary>
    /// Includes all fields in the results.
    /// </summary>
    /// <returns>The query builder for method chaining</returns>
    public FeatureQueryBuilder WithAllFields()
    {
        return new FeatureQueryBuilder(_query with { OutFields = null });
    }

    /// <summary>
    /// Controls whether geometry is included in the results.
    /// </summary>
    /// <param name="returnGeometry">True to include geometry, false to exclude</param>
    /// <returns>The query builder for method chaining</returns>
    public FeatureQueryBuilder WithGeometry(bool returnGeometry = true)
    {
        return new FeatureQueryBuilder(_query with { ReturnGeometry = returnGeometry });
    }

    /// <summary>
    /// Excludes geometry from the results (for performance when only attributes are needed).
    /// </summary>
    /// <returns>The query builder for method chaining</returns>
    public FeatureQueryBuilder WithoutGeometry()
    {
        return WithGeometry(false);
    }

    /// <summary>
    /// Sets the spatial reference for output geometry.
    /// </summary>
    /// <param name="spatialReference">Target spatial reference for geometry</param>
    /// <returns>The query builder for method chaining</returns>
    public FeatureQueryBuilder WithSpatialReference(SpatialReference spatialReference)
    {
        return new FeatureQueryBuilder(_query with { OutputSpatialReference = spatialReference });
    }

    /// <summary>
    /// Sets pagination parameters for the query.
    /// </summary>
    /// <param name="offset">Number of records to skip</param>
    /// <param name="limit">Maximum number of records to return</param>
    /// <returns>The query builder for method chaining</returns>
    public FeatureQueryBuilder WithPaging(int offset, int limit)
    {
        return new FeatureQueryBuilder(_query with
        {
            Offset = offset >= 0 ? offset : null,
            Limit = limit > 0 ? limit : null
        });
    }

    /// <summary>
    /// Sets the maximum number of records to return.
    /// </summary>
    /// <param name="limit">Maximum number of records</param>
    /// <returns>The query builder for method chaining</returns>
    public FeatureQueryBuilder WithLimit(int limit)
    {
        return new FeatureQueryBuilder(_query with { Limit = limit > 0 ? limit : null });
    }

    /// <summary>
    /// Sets the number of records to skip (for pagination).
    /// </summary>
    /// <param name="offset">Number of records to skip</param>
    /// <returns>The query builder for method chaining</returns>
    public FeatureQueryBuilder WithOffset(int offset)
    {
        return new FeatureQueryBuilder(_query with { Offset = offset >= 0 ? offset : null });
    }

    /// <summary>
    /// Sets the ordering for query results.
    /// </summary>
    /// <param name="orderBy">ORDER BY clause (e.g., "NAME ASC, CREATED_DATE DESC")</param>
    /// <returns>The query builder for method chaining</returns>
    public FeatureQueryBuilder OrderBy(string orderBy)
    {
        return new FeatureQueryBuilder(_query with { OrderBy = orderBy });
    }

    /// <summary>
    /// Orders results by the specified field in ascending order.
    /// </summary>
    /// <param name="fieldName">Field name to order by</param>
    /// <returns>The query builder for method chaining</returns>
    public FeatureQueryBuilder OrderByAsc(string fieldName)
    {
        return OrderBy($"{fieldName} ASC");
    }

    /// <summary>
    /// Orders results by the specified field in descending order.
    /// </summary>
    /// <param name="fieldName">Field name to order by</param>
    /// <returns>The query builder for method chaining</returns>
    public FeatureQueryBuilder OrderByDesc(string fieldName)
    {
        return OrderBy($"{fieldName} DESC");
    }

    /// <summary>
    /// Returns only distinct values (removes duplicates).
    /// </summary>
    /// <param name="distinct">True to return distinct values</param>
    /// <returns>The query builder for method chaining</returns>
    public FeatureQueryBuilder Distinct(bool distinct = true)
    {
        return new FeatureQueryBuilder(_query with { Distinct = distinct });
    }

    /// <summary>
    /// Adds a spatial filter to find features that intersect with the given geometry.
    /// </summary>
    /// <param name="geometry">Geometry to test intersection with</param>
    /// <param name="spatialReference">Optional spatial reference for the geometry</param>
    /// <returns>The query builder for method chaining</returns>
    public FeatureQueryBuilder Intersects(Geometry geometry, SpatialReference? spatialReference = null)
    {
        var filter = SpatialFilter.Intersects(geometry, spatialReference);
        return new FeatureQueryBuilder(_query with { SpatialFilter = filter });
    }

    /// <summary>
    /// Adds a spatial filter to find features that are within the given geometry.
    /// </summary>
    /// <param name="geometry">Geometry that should contain the features</param>
    /// <param name="spatialReference">Optional spatial reference for the geometry</param>
    /// <returns>The query builder for method chaining</returns>
    public FeatureQueryBuilder Within(Geometry geometry, SpatialReference? spatialReference = null)
    {
        var filter = SpatialFilter.Within(geometry, spatialReference);
        return new FeatureQueryBuilder(_query with { SpatialFilter = filter });
    }

    /// <summary>
    /// Adds a spatial filter to find features within the specified distance.
    /// </summary>
    /// <param name="geometry">Reference geometry for distance calculation</param>
    /// <param name="distance">Maximum distance</param>
    /// <param name="unit">Distance units</param>
    /// <param name="spatialReference">Optional spatial reference for the geometry</param>
    /// <returns>The query builder for method chaining</returns>
    public FeatureQueryBuilder WithinDistance(
        Geometry geometry,
        double distance,
        DistanceUnit unit = DistanceUnit.Meters,
        SpatialReference? spatialReference = null)
    {
        var filter = SpatialFilter.WithinDistance(geometry, distance, unit, spatialReference);
        return new FeatureQueryBuilder(_query with { SpatialFilter = filter });
    }

    /// <summary>
    /// Adds a custom spatial filter.
    /// </summary>
    /// <param name="spatialFilter">The spatial filter to apply</param>
    /// <returns>The query builder for method chaining</returns>
    public FeatureQueryBuilder WithSpatialFilter(SpatialFilter spatialFilter)
    {
        return new FeatureQueryBuilder(_query with { SpatialFilter = spatialFilter });
    }

    /// <summary>
    /// Adds statistical operations to the query.
    /// </summary>
    /// <param name="statistics">Statistical operations to perform</param>
    /// <returns>The query builder for method chaining</returns>
    public FeatureQueryBuilder WithStatistics(params StatisticDefinition[] statistics)
    {
        return WithStatistics(statistics.AsEnumerable());
    }

    /// <summary>
    /// Adds statistical operations to the query.
    /// </summary>
    /// <param name="statistics">Statistical operations to perform</param>
    /// <returns>The query builder for method chaining</returns>
    public FeatureQueryBuilder WithStatistics(IEnumerable<StatisticDefinition> statistics)
    {
        return new FeatureQueryBuilder(_query with { Statistics = statistics.ToList() });
    }

    /// <summary>
    /// Groups results by the specified fields (typically used with statistics).
    /// </summary>
    /// <param name="fields">Fields to group by</param>
    /// <returns>The query builder for method chaining</returns>
    public FeatureQueryBuilder GroupBy(params string[] fields)
    {
        return GroupBy(fields.AsEnumerable());
    }

    /// <summary>
    /// Groups results by the specified fields (typically used with statistics).
    /// </summary>
    /// <param name="fields">Fields to group by</param>
    /// <returns>The query builder for method chaining</returns>
    public FeatureQueryBuilder GroupBy(IEnumerable<string> fields)
    {
        return new FeatureQueryBuilder(_query with { GroupByFields = fields.ToList() });
    }

    /// <summary>
    /// Builds the final FeatureQuery.
    /// </summary>
    /// <returns>The constructed FeatureQuery</returns>
    public FeatureQuery Build()
    {
        return _query;
    }

    /// <summary>
    /// Implicitly converts the builder to a FeatureQuery.
    /// </summary>
    /// <param name="builder">The query builder</param>
    public static implicit operator FeatureQuery(FeatureQueryBuilder builder)
    {
        return builder.Build();
    }

    /// <summary>
    /// Creates a new query builder.
    /// </summary>
    /// <returns>A new FeatureQueryBuilder instance</returns>
    public static FeatureQueryBuilder Create()
    {
        return new FeatureQueryBuilder();
    }
}