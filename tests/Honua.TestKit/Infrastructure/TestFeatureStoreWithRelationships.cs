// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.TestKit.Infrastructure;

/// <summary>
/// Test implementation of IFeatureStore that extends TestFeatureStore
/// to provide relationship support for queryRelatedRecords endpoint tests.
/// </summary>
public sealed class TestFeatureStoreWithRelationships : IFeatureStore
{
    private readonly TestFeatureStore _baseStore = new();
    private readonly Dictionary<int, List<Feature>> _relatedLayerFeatures = new();

    public TestFeatureStoreWithRelationships()
    {
        // Set up test related features for layer 1 (related to layer 0)
        _relatedLayerFeatures[1] = new List<Feature>
        {
            Feature.Create(1, CreatePointWkb(-122.4, 37.6), ImmutableDictionary<string, object?>.Empty
                .Add("objectid", 1)
                .Add("name", "Related Feature 1")
                .Add("related_id", 1) // References object 1 from layer 0
                .Add("description", "First related feature")),
            Feature.Create(2, CreatePointWkb(-122.6, 37.4), ImmutableDictionary<string, object?>.Empty
                .Add("objectid", 2)
                .Add("name", "Related Feature 2")
                .Add("related_id", 1) // Also references object 1 from layer 0
                .Add("description", "Second related feature")),
            Feature.Create(3, CreatePointWkb(-122.8, 37.9), ImmutableDictionary<string, object?>.Empty
                .Add("objectid", 3)
                .Add("name", "Related Feature 3")
                .Add("related_id", 2) // References object 2 from layer 0
                .Add("description", "Third related feature")),
            Feature.Create(4, null, ImmutableDictionary<string, object?>.Empty
                .Add("objectid", 4)
                .Add("name", "Related Feature 4")
                .Add("related_id", 2) // Also references object 2 from layer 0
                .Add("description", "Fourth related feature"))
        };

        // Set up test related features for layer 2 (for secondary relationship)
        _relatedLayerFeatures[2] = new List<Feature>
        {
            Feature.Create(1, CreatePointWkb(-121.5, 38.5), ImmutableDictionary<string, object?>.Empty
                .Add("objectid", 1)
                .Add("name", "Secondary Related Feature 1")
                .Add("secondary_id", 1)
                .Add("type", "secondary")),
            Feature.Create(2, CreatePointWkb(-121.7, 38.3), ImmutableDictionary<string, object?>.Empty
                .Add("objectid", 2)
                .Add("name", "Secondary Related Feature 2")
                .Add("secondary_id", 1)
                .Add("type", "secondary"))
        };
    }

    // Delegate all existing feature store methods to the base implementation
    public Task<Feature?> GetAsync(int layerId, long featureId, CancellationToken cancellationToken = default)
        => _baseStore.GetAsync(layerId, featureId, cancellationToken);

    public Task<QueryResult<Feature>> QueryAsync(int layerId, FeatureQuery query, CancellationToken cancellationToken = default)
        => _baseStore.QueryAsync(layerId, query, cancellationToken);

    public Task<long> CountAsync(int layerId, FeatureQuery query, CancellationToken cancellationToken = default)
        => _baseStore.CountAsync(layerId, query, cancellationToken);

    public Task<FeatureExtent?> GetExtentAsync(int layerId, FeatureQuery? query = null, CancellationToken cancellationToken = default)
        => _baseStore.GetExtentAsync(layerId, query, cancellationToken);

    public Task<Feature> CreateAsync(int layerId, Feature feature, CancellationToken cancellationToken = default)
        => _baseStore.CreateAsync(layerId, feature, cancellationToken);

    public Task<Feature> UpdateAsync(int layerId, Feature feature, CancellationToken cancellationToken = default)
        => _baseStore.UpdateAsync(layerId, feature, cancellationToken);

    public Task<bool> DeleteAsync(int layerId, long featureId, CancellationToken cancellationToken = default)
        => _baseStore.DeleteAsync(layerId, featureId, cancellationToken);

    public Task<FeatureEditResult> ApplyEditsAsync(int layerId, FeatureEditBatch editBatch, CancellationToken cancellationToken = default)
        => _baseStore.ApplyEditsAsync(layerId, editBatch, cancellationToken);

    // Implement relationship-specific method
    public Task<QueryResult<Feature>> QueryRelatedAsync(int layerId, RelatedQuery query, CancellationToken cancellationToken = default)
    {
        // Get the related layer features
        if (!_relatedLayerFeatures.TryGetValue(query.Relationship.RelatedLayerId, out var relatedFeatures))
        {
            return Task.FromResult(QueryResult<Feature>.Empty());
        }

        // Filter features that match the foreign key relationship
        var matchingFeatures = new List<Feature>();

        foreach (var objectId in query.ObjectIds)
        {
            var relatedToObject = relatedFeatures.Where(f =>
            {
                // Check if the destination foreign key field matches the object ID
                if (f.Attributes.TryGetValue(query.Relationship.DestinationForeignKeyField, out var foreignKeyValue))
                {
                    return foreignKeyValue?.ToString() == objectId.ToString(System.Globalization.CultureInfo.InvariantCulture);
                }
                return false;
            });

            matchingFeatures.AddRange(relatedToObject);
        }

        // Apply WHERE clause filtering if provided
        if (!string.IsNullOrEmpty(query.Where))
        {
            matchingFeatures = ApplyWhereFilter(matchingFeatures, query.Where).ToList();
        }

        // Apply field filtering if specified
        if (query.OutFields.HasValue && query.OutFields.Value.Length > 0)
        {
            matchingFeatures = matchingFeatures.Select(f => FilterFields(f, query.OutFields.Value)).ToList();
        }

        // Apply limit if specified
        if (query.Limit.HasValue)
        {
            matchingFeatures = matchingFeatures.Take(query.Limit.Value).ToList();
        }

        var totalCount = matchingFeatures.Count;
        var hasMoreResults = false;

        return Task.FromResult(QueryResult<Feature>.Create(
            totalCount,
            matchingFeatures.ToImmutableArray(),
            hasMoreResults));
    }

    /// <summary>
    /// Applies WHERE clause filtering to related features
    /// </summary>
    private static IEnumerable<Feature> ApplyWhereFilter(IEnumerable<Feature> features, string whereClause)
    {
        // Simple WHERE clause parsing for testing - matches the base implementation pattern
        return whereClause.ToLowerInvariant() switch
        {
            "name='related feature 1'" => features.Where(f => f.Attributes.TryGetValue("name", out var nameValue) &&
                                                                nameValue?.ToString()?.Equals("Related Feature 1", StringComparison.OrdinalIgnoreCase) == true),
            var clause when clause.Contains("drop", StringComparison.OrdinalIgnoreCase) || clause.Contains(';') || clause.Contains("--", StringComparison.OrdinalIgnoreCase) =>
                throw new ArgumentException("WHERE clause contains dangerous pattern: " + clause.Split(' ').First(w => new[] { "drop", ";", "--" }.Contains(w.ToLower(System.Globalization.CultureInfo.InvariantCulture))), nameof(whereClause)),
            "invalid syntax here" =>
                throw new ArgumentException("WHERE clause format not supported. Use simple comparisons like: name = 'value' or age > 18", nameof(whereClause)),
            _ => features
        };
    }

    /// <summary>
    /// Filters feature fields based on outFields specification
    /// </summary>
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
    /// Creates a WKB point geometry for testing (same as base implementation)
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
}
