// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.TestKit.Infrastructure;

/// <summary>
/// Test implementation of IFeatureStore for unit and integration tests
/// </summary>
public class TestFeatureStore : IFeatureStore
{
    private readonly Dictionary<int, List<Feature>> _layerFeatures = new();

    public TestFeatureStore()
    {
        // Initialize with test data
        _layerFeatures[0] = new List<Feature>
        {
            Feature.Create(1, null, ImmutableDictionary<string, object?>.Empty
                .Add("objectid", 1)
                .Add("name", "Test Feature")
                .Add("description", "A test feature for integration tests")
                .Add("category", "test")),
            Feature.Create(2, null, ImmutableDictionary<string, object?>.Empty
                .Add("objectid", 2)
                .Add("name", "Another Feature")
                .Add("description", "Another test feature")
                .Add("category", "sample"))
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

        var allFilteredFeatures = filteredFeatures.ToList();
        var totalCount = allFilteredFeatures.Count;

        // Apply pagination
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

        return Task.FromResult(QueryResult<Feature>.Create(
            totalCount,
            allFilteredFeatures.ToImmutableArray(),
            query.Offset.HasValue && query.Limit.HasValue &&
                     (query.Offset.Value + query.Limit.Value) < totalCount));
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

    public Task<Feature> CreateAsync(int layerId, Feature feature, CancellationToken cancellationToken = default)
    {
        if (!_layerFeatures.ContainsKey(layerId))
            _layerFeatures[layerId] = new List<Feature>();

        var newId = _layerFeatures[layerId].Count > 0 ? _layerFeatures[layerId].Max(f => f.Id) + 1 : 1;
        var newFeature = Feature.Create(newId, feature.Geometry, feature.Attributes);
        _layerFeatures[layerId].Add(newFeature);
        return Task.FromResult(newFeature);
    }

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
        var errors = new List<string>();

        // Process creates
        var createdCount = 0;
        foreach (var feature in editBatch.Creates)
        {
            try
            {
                var created = await CreateAsync(layerId, feature, cancellationToken);
                createdIds.Add(created.Id);
                createdCount++;
            }
            catch (Exception ex)
            {
                errors.Add($"Failed to create feature {feature.Id}: {ex.Message}");
            }
        }

        if (errors.Count > 0)
        {
            return FeatureEditResult.Failure(errors.ToArray());
        }

        return FeatureEditResult.Success(
            createdCount: createdCount,
            updatedCount: 0,
            deletedCount: 0,
            createdIds: createdIds.ToImmutableArray());
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
}