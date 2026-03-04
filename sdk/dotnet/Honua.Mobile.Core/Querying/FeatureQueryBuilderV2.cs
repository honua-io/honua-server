// Copyright (c) Honua. All rights reserved.
// Licensed under the Apache License 2.0. See LICENSE in the project root.

using Honua.Mobile.Core.Models;

namespace Honua.Mobile.Core.Querying;

/// <summary>
/// Enhanced fluent query builder with v2 protocol features.
/// Supports complex filtering, mobile optimizations, and multiple geometry encodings.
/// </summary>
public class FeatureQueryBuilderV2
{
    private readonly EnhancedFeatureQuery _query = new();

    private FeatureQueryBuilderV2() { }

    /// <summary>
    /// Creates a new query builder instance.
    /// </summary>
    public static FeatureQueryBuilderV2 Create() => new();

    #region Basic Query Configuration

    /// <summary>
    /// Sets the object IDs to query for.
    /// </summary>
    public FeatureQueryBuilderV2 WithObjectIds(params long[] objectIds)
    {
        _query.ObjectIds = objectIds;
        return this;
    }

    /// <summary>
    /// Sets the object IDs to query for.
    /// </summary>
    public FeatureQueryBuilderV2 WithObjectIds(IEnumerable<long> objectIds)
    {
        _query.ObjectIds = objectIds;
        return this;
    }

    /// <summary>
    /// Specifies which fields to return.
    /// </summary>
    public FeatureQueryBuilderV2 WithFields(params string[] fields)
    {
        _query.OutFields = fields;
        return this;
    }

    /// <summary>
    /// Specifies which fields to return.
    /// </summary>
    public FeatureQueryBuilderV2 WithFields(IEnumerable<string> fields)
    {
        _query.OutFields = fields;
        return this;
    }

    /// <summary>
    /// Returns all available fields.
    /// </summary>
    public FeatureQueryBuilderV2 WithAllFields()
    {
        _query.OutFields = null;
        return this;
    }

    /// <summary>
    /// Sets whether to return geometry with features.
    /// </summary>
    public FeatureQueryBuilderV2 WithGeometry(bool returnGeometry = true)
    {
        _query.ReturnGeometry = returnGeometry;
        return this;
    }

    /// <summary>
    /// Excludes geometry from the response for better performance.
    /// </summary>
    public FeatureQueryBuilderV2 WithoutGeometry()
    {
        _query.ReturnGeometry = false;
        return this;
    }

    /// <summary>
    /// Sets the geometry encoding format.
    /// </summary>
    public FeatureQueryBuilderV2 WithGeometryEncoding(GeometryEncoding encoding)
    {
        _query.GeometryEncoding = encoding;
        return this;
    }

    /// <summary>
    /// Sets the output spatial reference system.
    /// </summary>
    public FeatureQueryBuilderV2 WithSpatialReference(SpatialReference spatialReference)
    {
        _query.OutputSpatialReference = spatialReference;
        return this;
    }

    #endregion

    #region Enhanced Filtering

    /// <summary>
    /// Sets a simple attribute filter (WHERE clause).
    /// </summary>
    public FeatureQueryBuilderV2 Where(string expression)
    {
        _query.Filter = QueryFilter.Attribute(expression);
        return this;
    }

    /// <summary>
    /// Sets a complex query filter.
    /// </summary>
    public FeatureQueryBuilderV2 WithFilter(QueryFilter filter)
    {
        _query.Filter = filter;
        return this;
    }

    /// <summary>
    /// Adds an attribute filter to an existing compound filter.
    /// </summary>
    public FeatureQueryBuilderV2 AndWhere(string expression)
    {
        var newFilter = QueryFilter.Attribute(expression);
        _query.Filter = _query.Filter == null
            ? newFilter
            : QueryFilter.And(_query.Filter, newFilter);
        return this;
    }

    /// <summary>
    /// Adds an OR attribute filter to an existing compound filter.
    /// </summary>
    public FeatureQueryBuilderV2 OrWhere(string expression)
    {
        var newFilter = QueryFilter.Attribute(expression);
        _query.Filter = _query.Filter == null
            ? newFilter
            : QueryFilter.Or(_query.Filter, newFilter);
        return this;
    }

    #endregion

    #region Spatial Filtering

    /// <summary>
    /// Filters features that intersect with the given geometry.
    /// </summary>
    public FeatureQueryBuilderV2 Intersects(Geometry geometry)
    {
        return WithSpatialFilter(geometry, SpatialRelationship.Intersects);
    }

    /// <summary>
    /// Filters features that are within the given geometry.
    /// </summary>
    public FeatureQueryBuilderV2 Within(Geometry geometry)
    {
        return WithSpatialFilter(geometry, SpatialRelationship.Within);
    }

    /// <summary>
    /// Filters features that contain the given geometry.
    /// </summary>
    public FeatureQueryBuilderV2 Contains(Geometry geometry)
    {
        return WithSpatialFilter(geometry, SpatialRelationship.Contains);
    }

    /// <summary>
    /// Filters features within a specified distance of a point.
    /// </summary>
    public FeatureQueryBuilderV2 Near(double longitude, double latitude, double distance, DistanceUnit unit = DistanceUnit.Meters)
    {
        var point = PointGeometry.Create(longitude, latitude);
        var spatialFilter = QueryFilter.Spatial(point, SpatialRelationship.WithinDistance);
        _query.Filter = _query.Filter == null
            ? spatialFilter
            : QueryFilter.And(_query.Filter, spatialFilter);
        return this;
    }

    /// <summary>
    /// Filters features within a specified distance of the given geometry.
    /// </summary>
    public FeatureQueryBuilderV2 WithinDistance(Geometry geometry, double distance, DistanceUnit unit = DistanceUnit.Meters)
    {
        return WithSpatialFilter(geometry, SpatialRelationship.WithinDistance, distance, unit);
    }

    /// <summary>
    /// Filters features beyond a specified distance of the given geometry.
    /// </summary>
    public FeatureQueryBuilderV2 BeyondDistance(Geometry geometry, double distance, DistanceUnit unit = DistanceUnit.Meters)
    {
        return WithSpatialFilter(geometry, SpatialRelationship.BeyondDistance, distance, unit);
    }

    /// <summary>
    /// Finds the nearest features to the given geometry.
    /// </summary>
    public FeatureQueryBuilderV2 NearestTo(Geometry geometry, int count = 1)
    {
        return WithSpatialFilter(geometry, SpatialRelationship.NearestNeighbor, nearestCount: count);
    }

    private FeatureQueryBuilderV2 WithSpatialFilter(Geometry geometry, SpatialRelationship relationship,
        double? distance = null, DistanceUnit unit = DistanceUnit.Meters, int? nearestCount = null)
    {
        var spatialFilter = new SpatialFilter
        {
            Geometry = geometry,
            SpatialRelationship = relationship,
            Distance = distance ?? 0,
            DistanceUnit = unit,
            NearestCount = nearestCount ?? 0
        };

        var queryFilter = QueryFilter.Spatial(geometry, relationship);
        _query.Filter = _query.Filter == null
            ? queryFilter
            : QueryFilter.And(_query.Filter, queryFilter);

        return this;
    }

    #endregion

    #region Temporal Filtering

    /// <summary>
    /// Filters features created after the specified date.
    /// </summary>
    public FeatureQueryBuilderV2 CreatedAfter(DateTime date)
    {
        var temporalFilter = TemporalFilter.CreatedAfter(date);
        _query.Filter = _query.Filter == null
            ? temporalFilter
            : QueryFilter.And(_query.Filter, temporalFilter);
        return this;
    }

    /// <summary>
    /// Filters features modified since the specified date.
    /// </summary>
    public FeatureQueryBuilderV2 ModifiedSince(DateTime date)
    {
        var temporalFilter = TemporalFilter.ModifiedSince(date);
        _query.Filter = _query.Filter == null
            ? temporalFilter
            : QueryFilter.And(_query.Filter, temporalFilter);
        return this;
    }

    /// <summary>
    /// Filters features with a date field between two dates.
    /// </summary>
    public FeatureQueryBuilderV2 Between(string dateField, DateTime startDate, DateTime endDate)
    {
        var temporalFilter = TemporalFilter.Between(dateField, startDate, endDate);
        _query.Filter = _query.Filter == null
            ? temporalFilter
            : QueryFilter.And(_query.Filter, temporalFilter);
        return this;
    }

    #endregion

    #region Pagination and Ordering

    /// <summary>
    /// Sets pagination parameters.
    /// </summary>
    public FeatureQueryBuilderV2 WithPaging(int offset, int limit)
    {
        _query.Offset = offset;
        _query.Limit = limit;
        return this;
    }

    /// <summary>
    /// Sets the maximum number of features to return.
    /// </summary>
    public FeatureQueryBuilderV2 WithLimit(int limit)
    {
        _query.Limit = limit;
        return this;
    }

    /// <summary>
    /// Sets the number of features to skip.
    /// </summary>
    public FeatureQueryBuilderV2 WithOffset(int offset)
    {
        _query.Offset = offset;
        return this;
    }

    /// <summary>
    /// Orders results by the specified field in ascending order.
    /// </summary>
    public FeatureQueryBuilderV2 OrderByAsc(string fieldName)
    {
        _query.OrderBy = $"{fieldName} ASC";
        return this;
    }

    /// <summary>
    /// Orders results by the specified field in descending order.
    /// </summary>
    public FeatureQueryBuilderV2 OrderByDesc(string fieldName)
    {
        _query.OrderBy = $"{fieldName} DESC";
        return this;
    }

    /// <summary>
    /// Sets custom ordering expression.
    /// </summary>
    public FeatureQueryBuilderV2 OrderBy(string orderExpression)
    {
        _query.OrderBy = orderExpression;
        return this;
    }

    #endregion

    #region Result Types

    /// <summary>
    /// Returns distinct features only.
    /// </summary>
    public FeatureQueryBuilderV2 Distinct()
    {
        _query.Distinct = true;
        return this;
    }

    /// <summary>
    /// Returns only the count of matching features.
    /// </summary>
    public FeatureQueryBuilderV2 CountOnly()
    {
        _query.CountOnly = true;
        return this;
    }

    /// <summary>
    /// Returns only the object IDs of matching features.
    /// </summary>
    public FeatureQueryBuilderV2 IdsOnly()
    {
        _query.IdsOnly = true;
        return this;
    }

    /// <summary>
    /// Returns only the extent (bounding box) of matching features.
    /// </summary>
    public FeatureQueryBuilderV2 ExtentOnly()
    {
        _query.ExtentOnly = true;
        return this;
    }

    #endregion

    #region Statistics and Grouping

    /// <summary>
    /// Adds a statistic definition to the query.
    /// </summary>
    public FeatureQueryBuilderV2 WithStatistic(string field, StatisticType type, string? outputFieldName = null)
    {
        var statistics = _query.Statistics?.ToList() ?? new List<StatisticDefinition>();
        statistics.Add(new StatisticDefinition
        {
            Field = field,
            Type = type,
            OutputFieldName = outputFieldName ?? $"{field}_{type}".ToUpper()
        });
        _query.Statistics = statistics;
        return this;
    }

    /// <summary>
    /// Adds common statistics (count, sum, min, max, avg) for a field.
    /// </summary>
    public FeatureQueryBuilderV2 WithCommonStatistics(string field, string? fieldPrefix = null)
    {
        var prefix = fieldPrefix ?? field;
        return WithStatistic(field, StatisticType.Count, $"{prefix}_COUNT")
               .WithStatistic(field, StatisticType.Sum, $"{prefix}_SUM")
               .WithStatistic(field, StatisticType.Min, $"{prefix}_MIN")
               .WithStatistic(field, StatisticType.Max, $"{prefix}_MAX")
               .WithStatistic(field, StatisticType.Average, $"{prefix}_AVG");
    }

    /// <summary>
    /// Groups results by the specified fields.
    /// </summary>
    public FeatureQueryBuilderV2 GroupBy(params string[] fields)
    {
        _query.GroupBy = fields;
        return this;
    }

    /// <summary>
    /// Groups results by the specified fields.
    /// </summary>
    public FeatureQueryBuilderV2 GroupBy(IEnumerable<string> fields)
    {
        _query.GroupBy = fields;
        return this;
    }

    #endregion

    #region Mobile Optimizations

    /// <summary>
    /// Configures mobile optimizations for the query.
    /// </summary>
    public FeatureQueryBuilderV2 WithMobileOptimizations(Action<MobileOptimizationsBuilder> configure)
    {
        var builder = new MobileOptimizationsBuilder();
        configure(builder);
        _query.MobileOptimizations = builder.Build();
        return this;
    }

    /// <summary>
    /// Enables low power mode for battery conservation.
    /// </summary>
    public FeatureQueryBuilderV2 WithLowPowerMode()
    {
        return WithMobileOptimizations(opt => opt.UseLowPowerMode());
    }

    /// <summary>
    /// Sets priority fields for progressive loading.
    /// </summary>
    public FeatureQueryBuilderV2 WithPriorityFields(params string[] fields)
    {
        return WithMobileOptimizations(opt => opt.PrioritizeFields(fields));
    }

    /// <summary>
    /// Configures geometry level of detail for mobile rendering.
    /// </summary>
    public FeatureQueryBuilderV2 WithLevelOfDetail(double minScale, double maxScale, double tolerance)
    {
        _query.LevelOfDetail = new LevelOfDetail
        {
            MinScale = minScale,
            MaxScale = maxScale,
            Tolerance = tolerance
        };
        return this;
    }

    /// <summary>
    /// Configures level of detail for mobile map viewing.
    /// </summary>
    public FeatureQueryBuilderV2 ForMobileMap(double zoomLevel)
    {
        _query.LevelOfDetail = LevelOfDetail.ForMobileMap(zoomLevel);
        return this;
    }

    /// <summary>
    /// Configures level of detail for list/table display.
    /// </summary>
    public FeatureQueryBuilderV2 ForListDisplay()
    {
        _query.LevelOfDetail = LevelOfDetail.ForListDisplay();
        return this;
    }

    #endregion

    #region Geometry Configuration

    /// <summary>
    /// Sets geometry precision for coordinate values.
    /// </summary>
    public FeatureQueryBuilderV2 WithGeometryPrecision(int precision)
    {
        _query.GeometryPrecision = precision;
        return this;
    }

    /// <summary>
    /// Sets maximum allowable offset for geometry simplification.
    /// </summary>
    public FeatureQueryBuilderV2 WithMaxAllowableOffset(double offset)
    {
        _query.MaxAllowableOffset = offset;
        return this;
    }

    /// <summary>
    /// Optimizes geometry encoding for mobile performance.
    /// </summary>
    public FeatureQueryBuilderV2 OptimizeForMobile()
    {
        return WithGeometryEncoding(GeometryEncoding.Wkb)
               .WithMobileOptimizations(opt => opt
                   .UseCompression(CompressionLevel.High)
                   .PrioritizeFields("OBJECTID", "NAME"));
    }

    /// <summary>
    /// Optimizes encoding for web/JavaScript clients.
    /// </summary>
    public FeatureQueryBuilderV2 OptimizeForWeb()
    {
        return WithGeometryEncoding(GeometryEncoding.GeoJson)
               .WithMobileOptimizations(opt => opt
                   .UseCompression(CompressionLevel.Medium));
    }

    /// <summary>
    /// Optimizes for debugging and development.
    /// </summary>
    public FeatureQueryBuilderV2 OptimizeForDebug()
    {
        return WithGeometryEncoding(GeometryEncoding.Wkt)
               .WithAllFields();
    }

    #endregion

    #region Build Methods

    /// <summary>
    /// Builds the enhanced feature query.
    /// </summary>
    public EnhancedFeatureQuery Build() => _query;

    /// <summary>
    /// Implicit conversion to EnhancedFeatureQuery.
    /// </summary>
    public static implicit operator EnhancedFeatureQuery(FeatureQueryBuilderV2 builder) => builder._query;

    #endregion
}

/// <summary>
/// Builder for mobile optimization configuration.
/// </summary>
public class MobileOptimizationsBuilder
{
    private readonly MobileOptimizations _optimizations = new();

    /// <summary>
    /// Enables low power mode for battery conservation.
    /// </summary>
    public MobileOptimizationsBuilder UseLowPowerMode()
    {
        _optimizations.LowPowerMode = true;
        return this;
    }

    /// <summary>
    /// Sets priority fields for progressive loading.
    /// </summary>
    public MobileOptimizationsBuilder PrioritizeFields(params string[] fields)
    {
        _optimizations.PriorityFields.AddRange(fields);
        return this;
    }

    /// <summary>
    /// Sets compression level for network optimization.
    /// </summary>
    public MobileOptimizationsBuilder UseCompression(CompressionLevel level)
    {
        _optimizations.Compression = level;
        return this;
    }

    /// <summary>
    /// Configures caching policy.
    /// </summary>
    public MobileOptimizationsBuilder WithCaching(Action<CachePolicyBuilder> configure)
    {
        var builder = new CachePolicyBuilder();
        configure(builder);
        _optimizations.CachePolicy = builder.Build();
        return this;
    }

    /// <summary>
    /// Uses aggressive caching for better performance.
    /// </summary>
    public MobileOptimizationsBuilder UseAggressiveCaching()
    {
        _optimizations.CachePolicy = CachePolicy.Aggressive();
        return this;
    }

    /// <summary>
    /// Uses conservative caching for data freshness.
    /// </summary>
    public MobileOptimizationsBuilder UseConservativeCaching()
    {
        _optimizations.CachePolicy = CachePolicy.Conservative();
        return this;
    }

    /// <summary>
    /// Builds the mobile optimizations configuration.
    /// </summary>
    public MobileOptimizations Build() => _optimizations;
}

/// <summary>
/// Builder for cache policy configuration.
/// </summary>
public class CachePolicyBuilder
{
    private readonly CachePolicy _policy = new();

    /// <summary>
    /// Sets the maximum age for cached data.
    /// </summary>
    public CachePolicyBuilder WithMaxAge(TimeSpan maxAge)
    {
        _policy.MaxAge = maxAge;
        return this;
    }

    /// <summary>
    /// Allows serving stale data while revalidating in background.
    /// </summary>
    public CachePolicyBuilder AllowStaleWhileRevalidate(bool allow = true)
    {
        _policy.AllowStaleWhileRevalidate = allow;
        return this;
    }

    /// <summary>
    /// Adds cache tags for selective invalidation.
    /// </summary>
    public CachePolicyBuilder WithTags(params string[] tags)
    {
        _policy.CacheTags.AddRange(tags);
        return this;
    }

    /// <summary>
    /// Builds the cache policy.
    /// </summary>
    public CachePolicy Build() => _policy;
}

/// <summary>
/// Static factory methods for common query patterns.
/// </summary>
public static class CommonQueries
{
    /// <summary>
    /// Creates a query for active features.
    /// </summary>
    public static FeatureQueryBuilderV2 ActiveFeatures() =>
        FeatureQueryBuilderV2.Create().Where("STATUS = 'Active'");

    /// <summary>
    /// Creates a query for features created in the last N days.
    /// </summary>
    public static FeatureQueryBuilderV2 CreatedInLastDays(int days) =>
        FeatureQueryBuilderV2.Create().CreatedAfter(DateTime.Now.AddDays(-days));

    /// <summary>
    /// Creates a query for features near a location.
    /// </summary>
    public static FeatureQueryBuilderV2 NearbyFeatures(double longitude, double latitude, double radiusMeters) =>
        FeatureQueryBuilderV2.Create().Near(longitude, latitude, radiusMeters);

    /// <summary>
    /// Creates a query for text search in specified fields.
    /// </summary>
    public static FeatureQueryBuilderV2 AttributeSearch(string field, string searchText,
        IEnumerable<string>? returnFields = null, int limit = 20) =>
        FeatureQueryBuilderV2.Create()
            .Where($"{field} LIKE '%{searchText}%'")
            .WithFields(returnFields ?? new[] { "OBJECTID", field })
            .WithLimit(limit)
            .OptimizeForMobile();

    /// <summary>
    /// Creates a query for grouped statistics by field.
    /// </summary>
    public static FeatureQueryBuilderV2 GroupedCounts(string groupByField) =>
        FeatureQueryBuilderV2.Create()
            .WithStatistic("*", StatisticType.Count, "COUNT")
            .GroupBy(groupByField)
            .WithoutGeometry()
            .OrderByDesc("COUNT");

    /// <summary>
    /// Creates a mobile-optimized query for map display.
    /// </summary>
    public static FeatureQueryBuilderV2 ForMobileMap(double zoomLevel,
        string[]? essentialFields = null) =>
        FeatureQueryBuilderV2.Create()
            .ForMobileMap(zoomLevel)
            .WithPriorityFields(essentialFields ?? new[] { "OBJECTID", "NAME" })
            .OptimizeForMobile();

    /// <summary>
    /// Creates a query optimized for list display.
    /// </summary>
    public static FeatureQueryBuilderV2 ForListDisplay(string[]? displayFields = null) =>
        FeatureQueryBuilderV2.Create()
            .ForListDisplay()
            .WithFields(displayFields ?? new[] { "OBJECTID", "NAME", "STATUS" })
            .WithLowPowerMode();
}