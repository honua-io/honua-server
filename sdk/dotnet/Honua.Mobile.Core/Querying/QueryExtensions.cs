// Copyright (c) Honua. All rights reserved.
// Licensed under the Apache License 2.0. See LICENSE in the project root.

using Honua.Mobile.Core.Models;

namespace Honua.Mobile.Core.Querying;

/// <summary>
/// Extension methods for common query patterns and spatial operations.
/// </summary>
public static class QueryExtensions
{
    /// <summary>
    /// Creates a query to find features near a point within a specified distance.
    /// </summary>
    /// <param name="builder">The query builder</param>
    /// <param name="point">The center point for the search</param>
    /// <param name="distance">Search radius</param>
    /// <param name="unit">Distance units</param>
    /// <returns>The query builder for method chaining</returns>
    public static FeatureQueryBuilder Near(
        this FeatureQueryBuilder builder,
        PointGeometry point,
        double distance,
        DistanceUnit unit = DistanceUnit.Meters)
    {
        return builder.WithinDistance(point, distance, unit);
    }

    /// <summary>
    /// Creates a query to find features near a coordinate within a specified distance.
    /// </summary>
    /// <param name="builder">The query builder</param>
    /// <param name="longitude">Longitude of the center point</param>
    /// <param name="latitude">Latitude of the center point</param>
    /// <param name="distance">Search radius</param>
    /// <param name="unit">Distance units</param>
    /// <returns>The query builder for method chaining</returns>
    public static FeatureQueryBuilder Near(
        this FeatureQueryBuilder builder,
        double longitude,
        double latitude,
        double distance,
        DistanceUnit unit = DistanceUnit.Meters)
    {
        var point = PointGeometry.Create(longitude, latitude);
        return builder.Near(point, distance, unit);
    }

    /// <summary>
    /// Creates a query to find features within a bounding box.
    /// </summary>
    /// <param name="builder">The query builder</param>
    /// <param name="xMin">Minimum X coordinate</param>
    /// <param name="yMin">Minimum Y coordinate</param>
    /// <param name="xMax">Maximum X coordinate</param>
    /// <param name="yMax">Maximum Y coordinate</param>
    /// <returns>The query builder for method chaining</returns>
    public static FeatureQueryBuilder WithinBounds(
        this FeatureQueryBuilder builder,
        double xMin,
        double yMin,
        double xMax,
        double yMax)
    {
        // Create a polygon representing the bounding box
        var ring = CoordinateSequence.Create(new[]
        {
            Coordinate.Create(xMin, yMin),
            Coordinate.Create(xMax, yMin),
            Coordinate.Create(xMax, yMax),
            Coordinate.Create(xMin, yMax),
            Coordinate.Create(xMin, yMin) // Close the ring
        });

        var polygon = PolygonGeometry.Create(new[] { ring });
        return builder.Intersects(polygon);
    }

    /// <summary>
    /// Creates a query to find features within the given extent.
    /// </summary>
    /// <param name="builder">The query builder</param>
    /// <param name="extent">The extent to search within</param>
    /// <returns>The query builder for method chaining</returns>
    public static FeatureQueryBuilder WithinExtent(
        this FeatureQueryBuilder builder,
        Extent extent)
    {
        return builder.WithinBounds(extent.XMin, extent.YMin, extent.XMax, extent.YMax);
    }

    /// <summary>
    /// Adds a contains operation (finds features that contain the given geometry).
    /// </summary>
    /// <param name="builder">The query builder</param>
    /// <param name="geometry">Geometry that should be contained within the features</param>
    /// <param name="spatialReference">Optional spatial reference for the geometry</param>
    /// <returns>The query builder for method chaining</returns>
    public static FeatureQueryBuilder Contains(
        this FeatureQueryBuilder builder,
        Geometry geometry,
        SpatialReference? spatialReference = null)
    {
        var filter = new SpatialFilter
        {
            Geometry = geometry,
            Relationship = SpatialRelationship.Contains,
            SpatialReference = spatialReference
        };
        return builder.WithSpatialFilter(filter);
    }

    /// <summary>
    /// Adds a filter for features that were created or modified after a certain date.
    /// </summary>
    /// <param name="builder">The query builder</param>
    /// <param name="fieldName">Name of the date field (e.g., "CREATED_DATE", "MODIFIED_DATE")</param>
    /// <param name="afterDate">The date to filter after</param>
    /// <returns>The query builder for method chaining</returns>
    public static FeatureQueryBuilder CreatedAfter(
        this FeatureQueryBuilder builder,
        string fieldName,
        DateTime afterDate)
    {
        var dateFilter = $"{fieldName} > date '{afterDate:yyyy-MM-dd HH:mm:ss}'";
        var existingWhere = builder.Build().Where;

        var newWhere = string.IsNullOrWhiteSpace(existingWhere)
            ? dateFilter
            : $"({existingWhere}) AND ({dateFilter})";

        return builder.Where(newWhere);
    }

    /// <summary>
    /// Adds a filter for features that match any of the provided values for a field.
    /// </summary>
    /// <param name="builder">The query builder</param>
    /// <param name="fieldName">Name of the field to filter on</param>
    /// <param name="values">Values to match (generates an IN clause)</param>
    /// <returns>The query builder for method chaining</returns>
    public static FeatureQueryBuilder WhereIn(
        this FeatureQueryBuilder builder,
        string fieldName,
        params string[] values)
    {
        return builder.WhereIn(fieldName, values.AsEnumerable());
    }

    /// <summary>
    /// Adds a filter for features that match any of the provided values for a field.
    /// </summary>
    /// <param name="builder">The query builder</param>
    /// <param name="fieldName">Name of the field to filter on</param>
    /// <param name="values">Values to match (generates an IN clause)</param>
    /// <returns>The query builder for method chaining</returns>
    public static FeatureQueryBuilder WhereIn(
        this FeatureQueryBuilder builder,
        string fieldName,
        IEnumerable<string> values)
    {
        var valueList = values.Select(v => $"'{v.Replace("'", "''")}'"); // Escape single quotes
        var inClause = $"{fieldName} IN ({string.Join(", ", valueList)})";

        var existingWhere = builder.Build().Where;
        var newWhere = string.IsNullOrWhiteSpace(existingWhere)
            ? inClause
            : $"({existingWhere}) AND ({inClause})";

        return builder.Where(newWhere);
    }

    /// <summary>
    /// Adds a filter for features where a field contains the specified text.
    /// </summary>
    /// <param name="builder">The query builder</param>
    /// <param name="fieldName">Name of the field to search in</param>
    /// <param name="searchText">Text to search for</param>
    /// <param name="caseInsensitive">Whether the search should be case-insensitive</param>
    /// <returns>The query builder for method chaining</returns>
    public static FeatureQueryBuilder WhereContains(
        this FeatureQueryBuilder builder,
        string fieldName,
        string searchText,
        bool caseInsensitive = true)
    {
        var escapedText = searchText.Replace("'", "''");
        var likeClause = caseInsensitive
            ? $"UPPER({fieldName}) LIKE UPPER('%{escapedText}%')"
            : $"{fieldName} LIKE '%{escapedText}%'";

        var existingWhere = builder.Build().Where;
        var newWhere = string.IsNullOrWhiteSpace(existingWhere)
            ? likeClause
            : $"({existingWhere}) AND ({likeClause})";

        return builder.Where(newWhere);
    }

    /// <summary>
    /// Adds common statistics for a numeric field (count, sum, min, max, average).
    /// </summary>
    /// <param name="builder">The query builder</param>
    /// <param name="fieldName">Name of the numeric field</param>
    /// <param name="outputPrefix">Prefix for output field names (defaults to field name)</param>
    /// <returns>The query builder for method chaining</returns>
    public static FeatureQueryBuilder WithCommonStatistics(
        this FeatureQueryBuilder builder,
        string fieldName,
        string? outputPrefix = null)
    {
        outputPrefix ??= fieldName;

        var statistics = new[]
        {
            new StatisticDefinition
            {
                Field = fieldName,
                Type = StatisticType.Count,
                OutputFieldName = $"{outputPrefix}_COUNT"
            },
            new StatisticDefinition
            {
                Field = fieldName,
                Type = StatisticType.Sum,
                OutputFieldName = $"{outputPrefix}_SUM"
            },
            new StatisticDefinition
            {
                Field = fieldName,
                Type = StatisticType.Min,
                OutputFieldName = $"{outputPrefix}_MIN"
            },
            new StatisticDefinition
            {
                Field = fieldName,
                Type = StatisticType.Max,
                OutputFieldName = $"{outputPrefix}_MAX"
            },
            new StatisticDefinition
            {
                Field = fieldName,
                Type = StatisticType.Average,
                OutputFieldName = $"{outputPrefix}_AVG"
            }
        };

        return builder.WithStatistics(statistics);
    }
}