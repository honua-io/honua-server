// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Text.RegularExpressions;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.TestKit.Infrastructure;

/// <summary>
/// Test implementation of IFeatureStore with comprehensive test data
/// designed for OData and cross-protocol integration tests.
/// Contains 15 features with varied attribute types for testing:
/// - Numeric fields (integer, double)
/// - Boolean fields
/// - String fields
/// - Nullable fields
/// - Date fields
/// </summary>
public sealed class ODataTestFeatureStore : IFeatureStore
{
    private readonly Dictionary<int, List<Feature>> _layerFeatures = new();

    /// <summary>
    /// Gets the test features for verification in tests.
    /// </summary>
    public IReadOnlyList<Feature> TestFeatures => _layerFeatures.TryGetValue(0, out var features)
        ? features.AsReadOnly()
        : Array.Empty<Feature>();

    public ODataTestFeatureStore()
    {
        // Initialize with 15 test features with varied attribute types
        // Designed for cross-protocol parity testing (OData, OGC API Features, GeoServices REST)
        _layerFeatures[0] = new List<Feature>
        {
            // Features 1-5: Cities in California with varied attributes
            Feature.Create(1, CreatePointWkb(-122.4194, 37.7749), ImmutableDictionary<string, object?>.Empty
                .Add("objectid", 1L)
                .Add("name", "San Francisco")
                .Add("population", 874961L)
                .Add("area_sq_km", 121.4)
                .Add("is_capital", false)
                .Add("state", "California")
                .Add("country", "USA")
                .Add("founded_year", 1850L)
                .Add("rating", 4.8)
                .Add("notes", (string?)null)),

            Feature.Create(2, CreatePointWkb(-118.2437, 34.0522), ImmutableDictionary<string, object?>.Empty
                .Add("objectid", 2L)
                .Add("name", "Los Angeles")
                .Add("population", 3979576L)
                .Add("area_sq_km", 1213.9)
                .Add("is_capital", false)
                .Add("state", "California")
                .Add("country", "USA")
                .Add("founded_year", 1781L)
                .Add("rating", 4.2)
                .Add("notes", "Largest city in California")),

            Feature.Create(3, CreatePointWkb(-121.4944, 38.5816), ImmutableDictionary<string, object?>.Empty
                .Add("objectid", 3L)
                .Add("name", "Sacramento")
                .Add("population", 524943L)
                .Add("area_sq_km", 259.3)
                .Add("is_capital", true)
                .Add("state", "California")
                .Add("country", "USA")
                .Add("founded_year", 1850L)
                .Add("rating", 3.9)
                .Add("notes", "State capital of California")),

            Feature.Create(4, CreatePointWkb(-117.1611, 32.7157), ImmutableDictionary<string, object?>.Empty
                .Add("objectid", 4L)
                .Add("name", "San Diego")
                .Add("population", 1423851L)
                .Add("area_sq_km", 964.5)
                .Add("is_capital", false)
                .Add("state", "California")
                .Add("country", "USA")
                .Add("founded_year", 1769L)
                .Add("rating", 4.5)
                .Add("notes", (string?)null)),

            Feature.Create(5, CreatePointWkb(-121.8863, 37.3382), ImmutableDictionary<string, object?>.Empty
                .Add("objectid", 5L)
                .Add("name", "San Jose")
                .Add("population", 1021795L)
                .Add("area_sq_km", 469.7)
                .Add("is_capital", false)
                .Add("state", "California")
                .Add("country", "USA")
                .Add("founded_year", 1777L)
                .Add("rating", 4.1)
                .Add("notes", "Heart of Silicon Valley")),

            // Features 6-10: Cities in other states
            Feature.Create(6, CreatePointWkb(-122.3321, 47.6062), ImmutableDictionary<string, object?>.Empty
                .Add("objectid", 6L)
                .Add("name", "Seattle")
                .Add("population", 749256L)
                .Add("area_sq_km", 217.0)
                .Add("is_capital", false)
                .Add("state", "Washington")
                .Add("country", "USA")
                .Add("founded_year", 1851L)
                .Add("rating", 4.4)
                .Add("notes", "Emerald City")),

            Feature.Create(7, CreatePointWkb(-122.6765, 45.5231), ImmutableDictionary<string, object?>.Empty
                .Add("objectid", 7L)
                .Add("name", "Portland")
                .Add("population", 654741L)
                .Add("area_sq_km", 376.5)
                .Add("is_capital", false)
                .Add("state", "Oregon")
                .Add("country", "USA")
                .Add("founded_year", 1845L)
                .Add("rating", 4.3)
                .Add("notes", "City of Roses")),

            Feature.Create(8, CreatePointWkb(-111.891, 40.7608), ImmutableDictionary<string, object?>.Empty
                .Add("objectid", 8L)
                .Add("name", "Salt Lake City")
                .Add("population", 200591L)
                .Add("area_sq_km", 286.5)
                .Add("is_capital", true)
                .Add("state", "Utah")
                .Add("country", "USA")
                .Add("founded_year", 1847L)
                .Add("rating", 3.8)
                .Add("notes", (string?)null)),

            Feature.Create(9, CreatePointWkb(-104.9903, 39.7392), ImmutableDictionary<string, object?>.Empty
                .Add("objectid", 9L)
                .Add("name", "Denver")
                .Add("population", 727211L)
                .Add("area_sq_km", 401.0)
                .Add("is_capital", true)
                .Add("state", "Colorado")
                .Add("country", "USA")
                .Add("founded_year", 1858L)
                .Add("rating", 4.6)
                .Add("notes", "Mile High City")),

            Feature.Create(10, CreatePointWkb(-112.074, 33.4484), ImmutableDictionary<string, object?>.Empty
                .Add("objectid", 10L)
                .Add("name", "Phoenix")
                .Add("population", 1680992L)
                .Add("area_sq_km", 1341.0)
                .Add("is_capital", true)
                .Add("state", "Arizona")
                .Add("country", "USA")
                .Add("founded_year", 1881L)
                .Add("rating", 4.0)
                .Add("notes", "Valley of the Sun")),

            // Features 11-15: Edge cases and special values
            Feature.Create(11, CreatePointWkb(-115.1398, 36.1699), ImmutableDictionary<string, object?>.Empty
                .Add("objectid", 11L)
                .Add("name", "Las Vegas")
                .Add("population", 641903L)
                .Add("area_sq_km", 352.0)
                .Add("is_capital", false)
                .Add("state", "Nevada")
                .Add("country", "USA")
                .Add("founded_year", 1905L)
                .Add("rating", 4.7)
                .Add("notes", "Entertainment Capital")),

            Feature.Create(12, CreatePointWkb(-110.9265, 32.2226), ImmutableDictionary<string, object?>.Empty
                .Add("objectid", 12L)
                .Add("name", "Tucson")
                .Add("population", 548073L)
                .Add("area_sq_km", 588.0)
                .Add("is_capital", false)
                .Add("state", "Arizona")
                .Add("country", "USA")
                .Add("founded_year", 1775L)
                .Add("rating", 3.7)
                .Add("notes", (string?)null)),

            // Feature with null geometry for edge case testing
            Feature.Create(13, null, ImmutableDictionary<string, object?>.Empty
                .Add("objectid", 13L)
                .Add("name", "Virtual City")
                .Add("population", 0L)
                .Add("area_sq_km", 0.0)
                .Add("is_capital", false)
                .Add("state", (string?)null)
                .Add("country", "USA")
                .Add("founded_year", 2020L)
                .Add("rating", (double?)null)
                .Add("notes", "Test city with no geometry")),

            Feature.Create(14, CreatePointWkb(-106.6504, 35.0844), ImmutableDictionary<string, object?>.Empty
                .Add("objectid", 14L)
                .Add("name", "Albuquerque")
                .Add("population", 564559L)
                .Add("area_sq_km", 490.9)
                .Add("is_capital", false)
                .Add("state", "New Mexico")
                .Add("country", "USA")
                .Add("founded_year", 1706L)
                .Add("rating", 3.9)
                .Add("notes", "Duke City")),

            Feature.Create(15, CreatePointWkb(-116.2023, 43.6150), ImmutableDictionary<string, object?>.Empty
                .Add("objectid", 15L)
                .Add("name", "Boise")
                .Add("population", 235684L)
                .Add("area_sq_km", 223.6)
                .Add("is_capital", true)
                .Add("state", "Idaho")
                .Add("country", "USA")
                .Add("founded_year", 1863L)
                .Add("rating", 4.2)
                .Add("notes", "City of Trees"))
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
        var hasMoreResults = query.Limit.HasValue && afterOffsetCount > query.Limit.Value;

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
        // Return extent covering Western US cities
        var extent = new FeatureExtent
        {
            MinX = -122.7,
            MinY = 32.2,
            MaxX = -104.9,
            MaxY = 47.7,
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
        var createResults = new List<EditOperationResult>();
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

        var hasErrors = createResults.Any(r => !r.IsSuccess);

        if (hasErrors && editBatch.RollbackOnFailure)
        {
            return FeatureEditResult.Rollback(createResults.ToImmutableArray());
        }

        return FeatureEditResult.Success(
            createdCount: hasErrors ? createResults.Count(r => r.IsSuccess) : createdCount,
            updatedCount: 0,
            deletedCount: 0,
            createdIds: createdIds.ToImmutableArray(),
            createResults: createResults.ToImmutableArray());
    }

    private static IEnumerable<Feature> ApplyWhereFilter(IEnumerable<Feature> features, string whereClause)
    {
        // Comprehensive WHERE clause parsing for OData-style filters converted to SQL
        var normalized = whereClause.Trim();

        // Handle AND logical operator (split and apply both conditions)
        if (normalized.Contains(" AND ", StringComparison.OrdinalIgnoreCase))
        {
            var parts = Regex.Split(normalized, @"\s+AND\s+", RegexOptions.IgnoreCase);
            var result = features;
            foreach (var part in parts)
            {
                result = ApplyWhereFilter(result, part.Trim());
            }
            return result;
        }

        // Handle OR logical operator (union of both conditions)
        if (normalized.Contains(" OR ", StringComparison.OrdinalIgnoreCase))
        {
            var parts = Regex.Split(normalized, @"\s+OR\s+", RegexOptions.IgnoreCase);
            var featuresList = features.ToList();
            var resultSet = new HashSet<long>();
            var resultFeatures = new List<Feature>();

            foreach (var part in parts)
            {
                var partResult = ApplyWhereFilter(featuresList, part.Trim());
                foreach (var f in partResult)
                {
                    if (resultSet.Add(f.Id))
                    {
                        resultFeatures.Add(f);
                    }
                }
            }
            return resultFeatures;
        }

        // Handle objectid comparisons (from OData ObjectId filters)
        // Pattern: objectid = 1, objectid > 10, etc.
        var objectIdMatch = Regex.Match(normalized, @"objectid\s*(=|<>|>|<|>=|<=)\s*'?(\d+)'?", RegexOptions.IgnoreCase);
        if (objectIdMatch.Success)
        {
            var op = objectIdMatch.Groups[1].Value;
            var targetId = long.Parse(objectIdMatch.Groups[2].Value);

            return features.Where(f => op switch
            {
                "=" => f.Id == targetId,
                "<>" => f.Id != targetId,
                ">" => f.Id > targetId,
                "<" => f.Id < targetId,
                ">=" => f.Id >= targetId,
                "<=" => f.Id <= targetId,
                _ => false
            });
        }

        // Handle LIKE patterns (from OData contains/startswith/endswith)
        var likeMatch = Regex.Match(normalized, @"attributes->>'(\w+)'\s+LIKE\s+'([^']*)'", RegexOptions.IgnoreCase);
        if (likeMatch.Success)
        {
            var field = likeMatch.Groups[1].Value;
            var pattern = likeMatch.Groups[2].Value;

            return features.Where(f =>
            {
                if (!f.Attributes.TryGetValue(field, out var value) || value == null)
                    return false;

                var strValue = value.ToString() ?? "";

                // Handle LIKE patterns
                if (pattern.StartsWith('%') && pattern.EndsWith('%'))
                {
                    // Contains: %value%
                    var searchValue = pattern.Trim('%');
                    return strValue.Contains(searchValue, StringComparison.OrdinalIgnoreCase);
                }
                else if (pattern.EndsWith('%'))
                {
                    // StartsWith: value%
                    var searchValue = pattern.TrimEnd('%');
                    return strValue.StartsWith(searchValue, StringComparison.OrdinalIgnoreCase);
                }
                else if (pattern.StartsWith('%'))
                {
                    // EndsWith: %value
                    var searchValue = pattern.TrimStart('%');
                    return strValue.EndsWith(searchValue, StringComparison.OrdinalIgnoreCase);
                }

                return strValue.Equals(pattern, StringComparison.OrdinalIgnoreCase);
            });
        }

        // Handle comparison operators on JSONB attributes
        var attrMatch = Regex.Match(normalized, @"attributes->>'(\w+)'\s*(=|<>|>|<|>=|<=)\s*'?([^']+)'?", RegexOptions.IgnoreCase);
        if (attrMatch.Success)
        {
            var field = attrMatch.Groups[1].Value;
            var op = attrMatch.Groups[2].Value;
            var rawValue = attrMatch.Groups[3].Value.Trim('\'');

            return features.Where(f =>
            {
                if (!f.Attributes.TryGetValue(field, out var attrValue))
                    return false;

                if (attrValue == null)
                    return false;

                // Try numeric comparison first
                if (double.TryParse(rawValue, out var numericTarget) &&
                    double.TryParse(attrValue.ToString(), out var numericValue))
                {
                    return op switch
                    {
                        "=" => Math.Abs(numericValue - numericTarget) < 0.0001,
                        "<>" => Math.Abs(numericValue - numericTarget) >= 0.0001,
                        ">" => numericValue > numericTarget,
                        "<" => numericValue < numericTarget,
                        ">=" => numericValue >= numericTarget,
                        "<=" => numericValue <= numericTarget,
                        _ => false
                    };
                }

                // String comparison
                var strValue = attrValue.ToString() ?? "";
                return op switch
                {
                    "=" => strValue.Equals(rawValue, StringComparison.OrdinalIgnoreCase),
                    "<>" => !strValue.Equals(rawValue, StringComparison.OrdinalIgnoreCase),
                    _ => false
                };
            });
        }

        // Handle simple field comparisons (name = 'value')
        var simpleMatch = Regex.Match(normalized, @"(\w+)\s*=\s*'([^']*)'", RegexOptions.IgnoreCase);
        if (simpleMatch.Success)
        {
            var field = simpleMatch.Groups[1].Value;
            var value = simpleMatch.Groups[2].Value;

            return features.Where(f =>
                f.Attributes.TryGetValue(field, out var attrValue) &&
                attrValue?.ToString()?.Equals(value, StringComparison.OrdinalIgnoreCase) == true);
        }

        // Handle is_capital = true/false
        var boolMatch = Regex.Match(normalized, @"is_capital\s*=\s*'?(true|false)'?", RegexOptions.IgnoreCase);
        if (boolMatch.Success)
        {
            var expectedValue = boolMatch.Groups[1].Value.Equals("true", StringComparison.OrdinalIgnoreCase);
            return features.Where(f =>
                f.Attributes.TryGetValue("is_capital", out var attrValue) &&
                attrValue is bool boolValue &&
                boolValue == expectedValue);
        }

        return features;
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

    private static byte[] CreatePointWkb(double x, double y)
    {
        var wkbBytes = new byte[21];
        wkbBytes[0] = 1; // Little-endian
        BitConverter.GetBytes((uint)1).CopyTo(wkbBytes, 1); // POINT type
        BitConverter.GetBytes(x).CopyTo(wkbBytes, 5);
        BitConverter.GetBytes(y).CopyTo(wkbBytes, 13);
        return wkbBytes;
    }

    private static IEnumerable<Feature> ApplySpatialFilter(IEnumerable<Feature> features, SpatialFilter spatialFilter)
    {
        return features.Where(feature =>
        {
            if (feature.Geometry == null)
                return false;

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

    private static (double x, double y)? ParsePointFromWkb(byte[] wkb)
    {
        if (wkb.Length < 21)
            return null;

        var x = BitConverter.ToDouble(wkb, 5);
        var y = BitConverter.ToDouble(wkb, 13);
        return (x, y);
    }

    private static List<(double x, double y)>? ParsePolygonFromWkb(byte[] wkb)
    {
        // For test purposes - covers Western US
        return new List<(double x, double y)>
        {
            (-125.0, 30.0),
            (-100.0, 30.0),
            (-100.0, 50.0),
            (-125.0, 50.0),
            (-125.0, 30.0)
        };
    }

    public Task<QueryResult<Feature>> QueryRelatedAsync(int layerId, RelatedQuery query, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(QueryResult<Feature>.Empty());
    }

    public Task<byte[]?> GetMvtTileAsync(int layerId, int x, int y, int z, FeatureQuery? query = null, CancellationToken cancellationToken = default)
    {
        if (!_layerFeatures.TryGetValue(layerId, out var features) || features.Count == 0)
        {
            return Task.FromResult<byte[]?>(null);
        }

        var mockMvt = new byte[]
        {
            0x1A, 0x04, 0x6C, 0x61, 0x79, 0x65, 0x72,
            0x12, 0x02, 0x08, 0x01,
            0x18, 0x03, 0x22, 0x02, 0x08, 0x01
        };

        return Task.FromResult<byte[]?>(mockMvt);
    }

    public Task<byte[]?> GetMvtTileAsync(int layerId, int x, int y, int z, FeatureQuery? query, Honua.Core.Features.Tiles.TileOptions tileOptions, CancellationToken cancellationToken = default)
    {
        return GetMvtTileAsync(layerId, x, y, z, query, cancellationToken);
    }
}
