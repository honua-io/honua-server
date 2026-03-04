// Copyright (c) Honua. All rights reserved.
// Licensed under the Apache License 2.0. See LICENSE in the project root.

using Honua.Mobile.Core.Models;

namespace Honua.Mobile.Core.Querying;

/// <summary>
/// Pre-built query templates for common geospatial scenarios.
/// Provides quick access to frequently used query patterns.
/// </summary>
public static class CommonQueries
{
    /// <summary>
    /// Creates a query to find all active features (where STATUS = 'Active').
    /// </summary>
    /// <param name="statusField">Name of the status field (defaults to "STATUS")</param>
    /// <returns>Query builder for active features</returns>
    public static FeatureQueryBuilder ActiveFeatures(string statusField = "STATUS")
    {
        return FeatureQueryBuilder.Create()
            .Where($"{statusField} = 'Active'");
    }

    /// <summary>
    /// Creates a query to find features created in the last N days.
    /// </summary>
    /// <param name="days">Number of days to look back</param>
    /// <param name="dateField">Name of the creation date field (defaults to "CREATED_DATE")</param>
    /// <returns>Query builder for recently created features</returns>
    public static FeatureQueryBuilder CreatedInLastDays(int days, string dateField = "CREATED_DATE")
    {
        var cutoffDate = DateTime.Now.AddDays(-Math.Abs(days));
        return FeatureQueryBuilder.Create()
            .CreatedAfter(dateField, cutoffDate);
    }

    /// <summary>
    /// Creates a query to find features near a point of interest for mobile location-based services.
    /// </summary>
    /// <param name="longitude">Longitude of the point of interest</param>
    /// <param name="latitude">Latitude of the point of interest</param>
    /// <param name="radiusMeters">Search radius in meters (defaults to 1000m)</param>
    /// <param name="limit">Maximum number of results (defaults to 50)</param>
    /// <returns>Query builder for nearby features</returns>
    public static FeatureQueryBuilder NearbyFeatures(
        double longitude,
        double latitude,
        double radiusMeters = 1000,
        int limit = 50)
    {
        return FeatureQueryBuilder.Create()
            .Near(longitude, latitude, radiusMeters, DistanceUnit.Meters)
            .WithLimit(limit)
            .OrderByAsc("OBJECTID"); // Ensure consistent ordering for pagination
    }

    /// <summary>
    /// Creates a query to find features within a map viewport (bounding box).
    /// Optimized for web/mobile map applications.
    /// </summary>
    /// <param name="westLon">Western longitude boundary</param>
    /// <param name="southLat">Southern latitude boundary</param>
    /// <param name="eastLon">Eastern longitude boundary</param>
    /// <param name="northLat">Northern latitude boundary</param>
    /// <param name="maxFeatures">Maximum features to return (defaults to 1000)</param>
    /// <returns>Query builder for features in viewport</returns>
    public static FeatureQueryBuilder InViewport(
        double westLon,
        double southLat,
        double eastLon,
        double northLat,
        int maxFeatures = 1000)
    {
        return FeatureQueryBuilder.Create()
            .WithinBounds(westLon, southLat, eastLon, northLat)
            .WithLimit(maxFeatures)
            .WithGeometry(true); // Include geometry for rendering
    }

    /// <summary>
    /// Creates a query optimized for attribute-only searches (no geometry).
    /// Useful for populating lists, dropdowns, or search results.
    /// </summary>
    /// <param name="searchField">Field to search in</param>
    /// <param name="searchText">Text to search for</param>
    /// <param name="displayFields">Fields to include in results</param>
    /// <param name="limit">Maximum results (defaults to 25)</param>
    /// <returns>Query builder for attribute search</returns>
    public static FeatureQueryBuilder AttributeSearch(
        string searchField,
        string searchText,
        string[] displayFields,
        int limit = 25)
    {
        return FeatureQueryBuilder.Create()
            .WhereContains(searchField, searchText)
            .WithFields(displayFields)
            .WithoutGeometry()
            .WithLimit(limit)
            .OrderByAsc(searchField);
    }

    /// <summary>
    /// Creates a query to get feature counts grouped by a categorical field.
    /// Useful for dashboard statistics and charts.
    /// </summary>
    /// <param name="groupByField">Field to group results by</param>
    /// <param name="countField">Field to count (defaults to "*" for row count)</param>
    /// <returns>Query builder for grouped statistics</returns>
    public static FeatureQueryBuilder GroupedCounts(string groupByField, string countField = "*")
    {
        var countStat = new StatisticDefinition
        {
            Field = countField,
            Type = StatisticType.Count,
            OutputFieldName = "COUNT"
        };

        return FeatureQueryBuilder.Create()
            .WithStatistics(countStat)
            .GroupBy(groupByField)
            .WithoutGeometry()
            .OrderByDesc("COUNT"); // Show highest counts first
    }

    /// <summary>
    /// Creates a query for field data collection scenarios.
    /// Finds features assigned to a user that need inspection or update.
    /// </summary>
    /// <param name="userField">Field containing assigned user</param>
    /// <param name="userId">User ID to filter by</param>
    /// <param name="statusField">Status field name</param>
    /// <param name="pendingStatuses">Status values indicating pending work</param>
    /// <returns>Query builder for assigned work</returns>
    public static FeatureQueryBuilder AssignedWork(
        string userField,
        string userId,
        string statusField = "STATUS",
        string[]? pendingStatuses = null)
    {
        pendingStatuses ??= new[] { "Assigned", "In Progress", "Needs Review" };

        return FeatureQueryBuilder.Create()
            .Where($"{userField} = '{userId.Replace("'", "''")}'")
            .WhereIn(statusField, pendingStatuses)
            .OrderByAsc("PRIORITY") // Assume priority field exists
            .WithLimit(100); // Reasonable limit for field work
    }

    /// <summary>
    /// Creates a query to find features that need attention (errors, warnings, overdue).
    /// Common for maintenance and quality assurance workflows.
    /// </summary>
    /// <param name="statusField">Status field name</param>
    /// <param name="problemStatuses">Status values indicating issues</param>
    /// <param name="dueDateField">Due date field name (optional)</param>
    /// <returns>Query builder for problematic features</returns>
    public static FeatureQueryBuilder ProblematicFeatures(
        string statusField = "STATUS",
        string[]? problemStatuses = null,
        string? dueDateField = null)
    {
        problemStatuses ??= new[] { "Error", "Failed", "Warning", "Needs Attention" };

        var builder = FeatureQueryBuilder.Create()
            .WhereIn(statusField, problemStatuses);

        // Add overdue condition if due date field is specified
        if (!string.IsNullOrWhiteSpace(dueDateField))
        {
            var today = DateTime.Today.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
            var existingWhere = builder.Build().Where;
            var overdueClause = $"{dueDateField} < date '{today}'";
            var combinedWhere = $"({existingWhere}) OR ({overdueClause})";
            builder = builder.Where(combinedWhere);
        }

        return builder
            .OrderByDesc("PRIORITY") // Show highest priority issues first
            .WithLimit(200);
    }

    /// <summary>
    /// Creates a query for mobile offline sync scenarios.
    /// Finds features modified since the last sync time.
    /// </summary>
    /// <param name="lastSyncTime">Timestamp of last successful sync</param>
    /// <param name="modifiedDateField">Modified date field name</param>
    /// <param name="includeGeometry">Whether to include geometry for offline storage</param>
    /// <returns>Query builder for delta sync</returns>
    public static FeatureQueryBuilder DeltaSync(
        DateTime lastSyncTime,
        string modifiedDateField = "MODIFIED_DATE",
        bool includeGeometry = true)
    {
        return FeatureQueryBuilder.Create()
            .CreatedAfter(modifiedDateField, lastSyncTime)
            .WithGeometry(includeGeometry)
            .OrderByAsc(modifiedDateField) // Chronological order for sync
            .WithLimit(5000); // Reasonable batch size for mobile
    }

    /// <summary>
    /// Creates a query for AR/VR visualization scenarios.
    /// Finds features in a small area around the user with full geometry and styling info.
    /// </summary>
    /// <param name="userLongitude">User's current longitude</param>
    /// <param name="userLatitude">User's current latitude</param>
    /// <param name="radiusMeters">Radius to search within (defaults to 100m for AR)</param>
    /// <returns>Query builder for AR/VR features</returns>
    public static FeatureQueryBuilder ArVisualization(
        double userLongitude,
        double userLatitude,
        double radiusMeters = 100)
    {
        return FeatureQueryBuilder.Create()
            .Near(userLongitude, userLatitude, radiusMeters, DistanceUnit.Meters)
            .WithAllFields() // Include all attributes for rich AR display
            .WithGeometry(true) // Essential for 3D visualization
            .WithLimit(50) // Keep reasonable for AR performance
            .OrderByAsc("OBJECTID");
    }
}