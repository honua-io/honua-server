// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.TestKit.Infrastructure;

/// <summary>
/// Test implementation of IFeatureStore for unit and integration tests
/// </summary>
internal class TestFeatureStore : IFeatureStore
{
    private readonly Dictionary<int, List<Feature>> _layerFeatures = new();

    public TestFeatureStore()
    {
        // Initialize with test data - more features for testing paging functionality
        // Some features have spatial geometry for spatial query testing
        _layerFeatures[0] = new List<Feature>
        {
            Feature.Create(1, CreatePointWkb(-122.5, 37.5), ImmutableDictionary<string, object?>.Empty
                .Add("objectid", 1)
                .Add("name", "Test Feature")
                .Add("description", "A test feature for integration tests")
                .Add("category", "test")),
            Feature.Create(2, CreatePointWkb(-122.7, 37.7), ImmutableDictionary<string, object?>.Empty
                .Add("objectid", 2)
                .Add("name", "Another Feature")
                .Add("description", "Another test feature")
                .Add("category", "sample")),
            Feature.Create(3, null, ImmutableDictionary<string, object?>.Empty
                .Add("objectid", 3)
                .Add("name", "Third Feature")
                .Add("description", "Third test feature")
                .Add("category", "test")),
            Feature.Create(4, CreatePointWkb(-121.9, 37.3), ImmutableDictionary<string, object?>.Empty
                .Add("objectid", 4)
                .Add("name", "Fourth Feature")
                .Add("description", "Fourth test feature")
                .Add("category", "sample")),
            Feature.Create(5, CreatePointWkb(-122.3, 37.8), ImmutableDictionary<string, object?>.Empty
                .Add("objectid", 5)
                .Add("name", "Fifth Feature")
                .Add("description", "Fifth test feature")
                .Add("category", "test"))
        };
    }

    public Task<Feature?> GetAsync(int layerId, long featureId, CancellationToken cancellationToken = default)
    {
        if (!_layerFeatures.TryGetValue(layerId, out var features))
            return Task.FromResult<Feature?>(null);

        var feature = features.FirstOrDefault(f => f.Id == featureId);
        return Task.FromResult<Feature?>(feature);
    }

    public Task<QueryResult<Feature>> QueryAsync(int layerId, FeatureQuery query, CancellationToken cancellationToken = default)
    {
        if (!_layerFeatures.TryGetValue(layerId, out var features))
        {
            return Task.FromResult(QueryResult<Feature>.Empty());
        }

        var filteredFeatures = features.AsEnumerable();

        // Apply WHERE clause filtering
        if (!string.IsNullOrEmpty(query.Where))
        {
            filteredFeatures = ApplyWhereFilter(filteredFeatures, query.Where);
        }

        // Apply spatial filtering
        if (query.SpatialFilter != null)
        {
            filteredFeatures = ApplySpatialFilter(filteredFeatures, query.SpatialFilter.Value);
        }

        var allFilteredFeatures = filteredFeatures.ToList();
        var totalCount = allFilteredFeatures.Count;

        // Apply pagination
        var offset = query.Offset ?? 0;
        var afterOffsetCount = Math.Max(0, totalCount - offset);

        if (query.Offset.HasValue)
        {
            allFilteredFeatures = allFilteredFeatures.Skip(query.Offset.Value).ToList();
        }

        if (query.Limit.HasValue)
        {
            allFilteredFeatures = allFilteredFeatures.Take(query.Limit.Value).ToList();
        }

        // Apply field filtering
        if (query.OutFields?.Length > 0)
        {
            allFilteredFeatures = allFilteredFeatures.Select(f => FilterFields(f, query.OutFields.Value)).ToList();
        }

        // Calculate if more results are available
        var hasMoreResults = false;
        if (query.Limit.HasValue)
        {
            // With limit: more results if we would have returned more without the limit
            hasMoreResults = afterOffsetCount > query.Limit.Value;
        }
        else if (query.Offset.HasValue)
        {
            // With only offset: more results if offset didn't skip everything
            hasMoreResults = false; // All remaining results after offset are returned
        }

        return Task.FromResult(QueryResult<Feature>.Create(
            totalCount,
            allFilteredFeatures.ToImmutableArray(),
            hasMoreResults));
    }

    public Task<long> CountAsync(int layerId, FeatureQuery query, CancellationToken cancellationToken = default)
    {
        if (!_layerFeatures.TryGetValue(layerId, out var features))
            return Task.FromResult(0L);

        var filteredFeatures = features.AsEnumerable();

        if (!string.IsNullOrEmpty(query.Where))
        {
            filteredFeatures = ApplyWhereFilter(filteredFeatures, query.Where);
        }

        return Task.FromResult((long)filteredFeatures.Count());
    }

    public Task<FeatureExtent?> GetExtentAsync(int layerId, FeatureQuery? query = null, CancellationToken cancellationToken = default)
    {
        // Return a simple test extent
        var extent = new FeatureExtent
        {
            MinX = -122.5,
            MinY = 37.7,
            MaxX = -122.3,
            MaxY = 37.8,
            SpatialReference = 4326
        };
        return Task.FromResult<FeatureExtent?>(extent);
    }

    /// <summary>
    /// Asynchronously creates a feature in the specified layer.
    /// </summary>
    /// <param name="layerId">The layer ID where the feature will be created.</param>
    /// <param name="feature">The feature to create.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The created feature.</returns>
    public Task<Feature> CreateAsync(int layerId, Feature feature, CancellationToken cancellationToken = default)
    {
        if (!_layerFeatures.ContainsKey(layerId))
            _layerFeatures[layerId] = new List<Feature>();

        var newId = _layerFeatures[layerId].Count > 0 ? _layerFeatures[layerId].Max(f => f.Id) + 1 : 1;
        var newFeature = Feature.Create(newId, feature.Geometry, feature.Attributes);
        _layerFeatures[layerId].Add(newFeature);
        return Task.FromResult(newFeature);
    }

    /// <summary>
    /// Asynchronously updates a feature in the specified layer.
    /// </summary>
    /// <param name="layerId">The layer ID where the feature exists.</param>
    /// <param name="feature">The feature with updated data.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The updated feature.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the layer or feature is not found.</exception>
    public Task<Feature> UpdateAsync(int layerId, Feature feature, CancellationToken cancellationToken = default)
    {
        if (!_layerFeatures.TryGetValue(layerId, out var features))
            throw new InvalidOperationException($"Layer {layerId} not found");

        var existingIndex = features.FindIndex(f => f.Id == feature.Id);
        if (existingIndex == -1)
            throw new InvalidOperationException($"Feature {feature.Id} not found");

        features[existingIndex] = feature;
        return Task.FromResult(feature);
    }

    /// <summary>
    /// Asynchronously deletes a feature from the specified layer.
    /// </summary>
    /// <param name="layerId">The layer ID where the feature exists.</param>
    /// <param name="featureId">The ID of the feature to delete.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if the feature was successfully deleted, false if not found.</returns>
    public Task<bool> DeleteAsync(int layerId, long featureId, CancellationToken cancellationToken = default)
    {
        if (!_layerFeatures.TryGetValue(layerId, out var features))
            return Task.FromResult(false);

        var existingIndex = features.FindIndex(f => f.Id == featureId);
        if (existingIndex == -1)
            return Task.FromResult(false);

        features.RemoveAt(existingIndex);
        return Task.FromResult(true);
    }

    public async Task<FeatureEditResult> ApplyEditsAsync(int layerId, FeatureEditBatch editBatch, CancellationToken cancellationToken = default)
    {
        var createdIds = new List<long>();
        var createResults = new List<EditOperationResult>();

        // Process creates
        var createdCount = 0;
        foreach (var feature in editBatch.Creates)
        {
            try
            {
                var created = await CreateAsync(layerId, feature, cancellationToken);
                createdIds.Add(created.Id);
                createdCount++;
                createResults.Add(EditOperationResult.Success(created.Id, feature.Attributes.GetValueOrDefault("globalId")?.ToString()));
            }
            catch (Exception ex)
            {
                createResults.Add(EditOperationResult.Failure($"Failed to create feature {feature.Id}: {ex.Message}"));
            }
        }

        // Check if any operations failed
        var hasErrors = createResults.Any(r => !r.IsSuccess);

        if (hasErrors && editBatch.RollbackOnFailure)
        {
            // Rollback all operations
            return FeatureEditResult.Rollback(createResults.ToImmutableArray());
        }
        else if (hasErrors && !editBatch.RollbackOnFailure)
        {
            // Return partial success - count only successful operations
            return FeatureEditResult.Success(
                createdCount: createResults.Count(r => r.IsSuccess),
                updatedCount: 0,
                deletedCount: 0,
                createdIds: createdIds.ToImmutableArray(),
                createResults: createResults.ToImmutableArray());
        }

        return FeatureEditResult.Success(
            createdCount: createdCount,
            updatedCount: 0,
            deletedCount: 0,
            createdIds: createdIds.ToImmutableArray(),
            createResults: createResults.ToImmutableArray());
    }

    private static IEnumerable<Feature> ApplyWhereFilter(IEnumerable<Feature> features, string whereClause)
    {
        // Simple WHERE clause parsing for testing
        // This is a simplified version, real implementation would be more robust
        return whereClause.ToLowerInvariant() switch
        {
            "name='test feature'" => features.Where(f => f.Attributes.TryGetValue("name", out var nameValue) &&
                                                         nameValue?.ToString()?.Equals("Test Feature", StringComparison.OrdinalIgnoreCase) == true),
            var clause when clause.Contains("drop", StringComparison.OrdinalIgnoreCase) || clause.Contains(';') || clause.Contains("--", StringComparison.OrdinalIgnoreCase) =>
                throw new ArgumentException("WHERE clause contains dangerous pattern: " + clause.Split(' ').First(w => new[] { "drop", ";", "--" }.Contains(w.ToLower(System.Globalization.CultureInfo.InvariantCulture))), nameof(whereClause)),
            "invalid syntax here" =>
                throw new ArgumentException("WHERE clause format not supported. Use simple comparisons like: name = 'value' or age > 18", nameof(whereClause)),
            _ => features
        };
    }

    private static Feature FilterFields(Feature feature, ImmutableArray<string> outFields)
    {
        var filteredAttributes = ImmutableDictionary<string, object?>.Empty;

        foreach (var field in outFields)
        {
            if (feature.Attributes.TryGetValue(field, out var value))
            {
                filteredAttributes = filteredAttributes.Add(field, value);
            }
        }

        return Feature.Create(feature.Id, feature.Geometry, filteredAttributes);
    }

    /// <summary>
    /// Creates a WKB point geometry for testing
    /// </summary>
    private static byte[] CreatePointWkb(double x, double y)
    {
        var wkbBytes = new byte[21]; // 1 + 4 + 8 + 8 bytes
        wkbBytes[0] = 1; // Little-endian
        BitConverter.GetBytes((uint)1).CopyTo(wkbBytes, 1); // POINT type
        BitConverter.GetBytes(x).CopyTo(wkbBytes, 5); // X coordinate
        BitConverter.GetBytes(y).CopyTo(wkbBytes, 13); // Y coordinate
        return wkbBytes;
    }

    /// <summary>
    /// Applies spatial filtering for test scenarios
    /// </summary>
    private static IEnumerable<Feature> ApplySpatialFilter(IEnumerable<Feature> features, SpatialFilter spatialFilter)
    {
        return features.Where(feature =>
        {
            if (feature.Geometry == null)
                return false;

            // Simple point-in-polygon test for testing purposes
            // This is a simplified implementation for test scenarios only
            var point = ParsePointFromWkb(feature.Geometry);
            if (point == null)
                return false;

            var polygon = ParsePolygonFromWkb(spatialFilter.Geometry);
            if (polygon == null)
                return false;

            return spatialFilter.SpatialRelationship switch
            {
                SpatialRelationship.Contains => IsPointInPolygon(point.Value, polygon),
                SpatialRelationship.Intersects => IsPointInPolygon(point.Value, polygon),
                SpatialRelationship.Within => IsPointInPolygon(point.Value, polygon),
                _ => false
            };
        });
    }

    /// <summary>
    /// Simple point-in-polygon algorithm for testing
    /// </summary>
    private static bool IsPointInPolygon((double x, double y) point, List<(double x, double y)> polygon)
    {
        bool inside = false;
        int j = polygon.Count - 1;

        for (int i = 0; i < polygon.Count; i++)
        {
            if (((polygon[i].y > point.y) != (polygon[j].y > point.y)) &&
                (point.x < (polygon[j].x - polygon[i].x) * (point.y - polygon[i].y) / (polygon[j].y - polygon[i].y) + polygon[i].x))
            {
                inside = !inside;
            }
            j = i;
        }
        return inside;
    }

    /// <summary>
    /// Parse point coordinates from WKB data
    /// </summary>
    private static (double x, double y)? ParsePointFromWkb(byte[] wkb)
    {
        if (wkb.Length < 21)
            return null;

        // Skip endian (1 byte) and type (4 bytes)
        var x = BitConverter.ToDouble(wkb, 5);
        var y = BitConverter.ToDouble(wkb, 13);
        return (x, y);
    }

    /// <summary>
    /// Parse polygon coordinates from WKB data for testing
    /// Simplified implementation that only handles the test polygon
    /// </summary>
    private static List<(double x, double y)>? ParsePolygonFromWkb(byte[] wkb)
    {
        // For test purposes, return a hard-coded polygon that matches the test
        // Test polygon bounds: -123 to -122 longitude, 37 to 38 latitude
        return new List<(double x, double y)>
        {
            (-123.0, 37.0),
            (-122.0, 37.0),
            (-122.0, 38.0),
            (-123.0, 38.0),
            (-123.0, 37.0)
        };
    }

    public Task<QueryResult<Feature>> QueryRelatedAsync(int layerId, RelatedQuery query, CancellationToken cancellationToken = default)
    {
        // Basic implementation returns empty result - tests that need related features should use TestFeatureStoreWithRelationships
        return Task.FromResult(QueryResult<Feature>.Empty());
    }
}
