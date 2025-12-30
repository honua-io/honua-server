// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Queries.Filters;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;

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

        _layerFeatures[1] = new List<Feature>
        {
            Feature.Create(1, null, ImmutableDictionary<string, object?>.Empty
                .Add("objectid", 1L)
                .Add("city_id", 1L)
                .Add("name", "Golden Gate Bridge")
                .Add("category", "Bridge")
                .Add("established_year", 1937L)),

            Feature.Create(2, null, ImmutableDictionary<string, object?>.Empty
                .Add("objectid", 2L)
                .Add("city_id", 1L)
                .Add("name", "Coit Tower")
                .Add("category", "Tower")
                .Add("established_year", 1933L)),

            Feature.Create(3, null, ImmutableDictionary<string, object?>.Empty
                .Add("objectid", 3L)
                .Add("city_id", 2L)
                .Add("name", "Griffith Observatory")
                .Add("category", "Observatory")
                .Add("established_year", 1935L)),

            Feature.Create(4, null, ImmutableDictionary<string, object?>.Empty
                .Add("objectid", 4L)
                .Add("city_id", 2L)
                .Add("name", "Hollywood Sign")
                .Add("category", "Landmark")
                .Add("established_year", 1923L)),

            Feature.Create(5, null, ImmutableDictionary<string, object?>.Empty
                .Add("objectid", 5L)
                .Add("city_id", 3L)
                .Add("name", "California State Capitol")
                .Add("category", "Government")
                .Add("established_year", 1874L)),

            Feature.Create(6, null, ImmutableDictionary<string, object?>.Empty
                .Add("objectid", 6L)
                .Add("city_id", 6L)
                .Add("name", "Space Needle")
                .Add("category", "Tower")
                .Add("established_year", 1962L))
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

        var whereClause = query.SqlFilter is not null
            ? ConvertSqlFragmentToWhereClause(query.SqlFilter)
            : query.Where;

        if (!string.IsNullOrEmpty(whereClause))
        {
            filteredFeatures = ApplyWhereFilter(filteredFeatures, whereClause);
        }

        // Apply spatial filtering
        if (query.SpatialFilter != null)
        {
            filteredFeatures = ApplySpatialFilter(filteredFeatures, query.SpatialFilter.Value);
        }

        if (query.OrderBy.HasValue && query.OrderBy.Value.Length > 0)
        {
            filteredFeatures = ApplyOrdering(filteredFeatures, query.OrderBy.Value);
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

        var whereClause = query.SqlFilter is not null
            ? ConvertSqlFragmentToWhereClause(query.SqlFilter)
            : query.Where;

        if (!string.IsNullOrEmpty(whereClause))
        {
            filteredFeatures = ApplyWhereFilter(filteredFeatures, whereClause);
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

        if (TryApplySearchCondition(features, normalized, out var searchFiltered))
        {
            return searchFiltered;
        }

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
            var targetId = long.Parse(objectIdMatch.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);

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

        // Handle null comparisons (field = null, field <> null)
        var nullMatch = Regex.Match(normalized, @"(?<field>\w+)\s*(?<op>=|<>)\s*null", RegexOptions.IgnoreCase);
        if (nullMatch.Success)
        {
            var field = nullMatch.Groups["field"].Value;
            var op = nullMatch.Groups["op"].Value;

            return features.Where(f =>
            {
                var hasValue = f.Attributes.TryGetValue(field, out var attrValue);
                var isNull = !hasValue || attrValue == null;
                return op == "=" ? isNull : !isNull;
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

    private static bool TryApplySearchCondition(
        IEnumerable<Feature> features,
        string normalized,
        out IEnumerable<Feature> filtered)
    {
        filtered = Array.Empty<Feature>();

        if (!normalized.Contains("ILIKE", StringComparison.OrdinalIgnoreCase) ||
            !normalized.Contains("COALESCE(", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var orGroups = SplitTopLevel(normalized, " OR ")
            .Select(part => part.Trim())
            .Where(part => part.Length > 0)
            .ToArray();

        if (orGroups.Length == 0)
        {
            return false;
        }

        var parsedGroups = new List<List<(string term, bool isNegated)>>();

        foreach (var group in orGroups)
        {
            var conditions = SplitTopLevel(group, " AND ")
                .Select(part => part.Trim())
                .Where(part => part.Length > 0)
                .ToArray();

            if (conditions.Length == 0)
            {
                continue;
            }

            var parsedConditions = new List<(string term, bool isNegated)>();

            foreach (var condition in conditions)
            {
                var trimmed = condition.Trim();
                var isNegated = trimmed.StartsWith("NOT ", StringComparison.OrdinalIgnoreCase);
                if (isNegated)
                {
                    trimmed = trimmed[4..].Trim();
                }

                trimmed = TrimOuterParentheses(trimmed);

                var match = Regex.Match(trimmed, @"ILIKE\s+'%([^']*)%'", RegexOptions.IgnoreCase);
                if (!match.Success)
                {
                    return false;
                }

                var term = match.Groups[1].Value
                    .Replace("\\%", "%", StringComparison.Ordinal)
                    .Replace("\\_", "_", StringComparison.Ordinal)
                    .Replace("''", "'", StringComparison.Ordinal);

                parsedConditions.Add((term, isNegated));
            }

            if (parsedConditions.Count > 0)
            {
                parsedGroups.Add(parsedConditions);
            }
        }

        if (parsedGroups.Count == 0)
        {
            return false;
        }

        filtered = features.Where(feature =>
        {
            var stringValues = feature.Attributes.Values
                .Select(value => value switch
                {
                    string text => text,
                    JsonElement element when element.ValueKind == JsonValueKind.String => element.GetString() ?? string.Empty,
                    _ => string.Empty
                })
                .Where(text => !string.IsNullOrWhiteSpace(text))
                .ToArray();

            foreach (var group in parsedGroups)
            {
                var groupMatches = true;

                foreach (var (term, isNegated) in group)
                {
                    var matches = stringValues.Any(text => text.Contains(term, StringComparison.OrdinalIgnoreCase));
                    if (isNegated)
                    {
                        if (matches)
                        {
                            groupMatches = false;
                            break;
                        }
                    }
                    else if (!matches)
                    {
                        groupMatches = false;
                        break;
                    }
                }

                if (groupMatches)
                {
                    return true;
                }
            }

            return false;
        });

        return true;
    }

    private static List<string> SplitTopLevel(string input, string separator)
    {
        var results = new List<string>();
        var depth = 0;
        var start = 0;

        for (var i = 0; i <= input.Length - separator.Length; i++)
        {
            var c = input[i];
            if (c == '(')
            {
                depth++;
            }
            else if (c == ')')
            {
                depth = Math.Max(0, depth - 1);
            }

            if (depth == 0 && input.AsSpan(i, separator.Length).Equals(separator, StringComparison.OrdinalIgnoreCase))
            {
                results.Add(input[start..i]);
                i += separator.Length - 1;
                start = i + 1;
            }
        }

        if (start <= input.Length)
        {
            results.Add(input[start..]);
        }

        return results;
    }

    private static string TrimOuterParentheses(string value)
    {
        var trimmed = value.Trim();

        while (trimmed.StartsWith('(') && trimmed.EndsWith(')'))
        {
            var depth = 0;
            var isBalanced = true;

            for (var i = 0; i < trimmed.Length; i++)
            {
                if (trimmed[i] == '(')
                {
                    depth++;
                }
                else if (trimmed[i] == ')')
                {
                    depth--;
                    if (depth == 0 && i < trimmed.Length - 1)
                    {
                        isBalanced = false;
                        break;
                    }
                }
            }

            if (!isBalanced || depth != 0)
            {
                break;
            }

            trimmed = trimmed[1..^1].Trim();
        }

        return trimmed;
    }

    private static IEnumerable<Feature> ApplyOrdering(IEnumerable<Feature> features, ImmutableArray<OrderByClause> orderBy)
    {
        IOrderedEnumerable<Feature>? ordered = null;

        foreach (var clause in orderBy)
        {
            var comparer = CreateOrderByComparer(clause.FieldType);
            Func<Feature, object?> keySelector = feature => GetOrderByValue(feature, clause.Field);

            ordered = ordered == null
                ? (clause.Ascending ? features.OrderBy(keySelector, comparer) : features.OrderByDescending(keySelector, comparer))
                : (clause.Ascending ? ordered.ThenBy(keySelector, comparer) : ordered.ThenByDescending(keySelector, comparer));
        }

        return ordered ?? features;
    }

    private static object? GetOrderByValue(Feature feature, string field)
    {
        var normalized = field.Trim();
        var fieldLower = normalized.ToLowerInvariant();

        if (fieldLower == "objectid")
        {
            return feature.Id;
        }

        if (fieldLower is "layerid" or "layer_id")
        {
            return 0;
        }

        if (feature.Attributes.TryGetValue(normalized, out var value))
        {
            return value;
        }

        foreach (var kvp in feature.Attributes)
        {
            if (kvp.Key.Equals(normalized, StringComparison.OrdinalIgnoreCase))
            {
                return kvp.Value;
            }
        }

        return null;
    }

    private static Comparer<object?> CreateOrderByComparer(FieldType? fieldType)
    {
        return Comparer<object?>.Create((left, right) => CompareOrderByValues(left, right, fieldType));
    }

    private static int CompareOrderByValues(object? left, object? right, FieldType? fieldType)
    {
        if (ReferenceEquals(left, right))
        {
            return 0;
        }

        if (left is null)
        {
            return 1;
        }

        if (right is null)
        {
            return -1;
        }

        try
        {
            switch (fieldType)
            {
                case FieldType.Integer:
                case FieldType.BigInteger:
                    return Convert.ToInt64(left, CultureInfo.InvariantCulture)
                        .CompareTo(Convert.ToInt64(right, CultureInfo.InvariantCulture));
                case FieldType.Float:
                case FieldType.Double:
                    return Convert.ToDouble(left, CultureInfo.InvariantCulture)
                        .CompareTo(Convert.ToDouble(right, CultureInfo.InvariantCulture));
                case FieldType.Boolean:
                    return Convert.ToBoolean(left, CultureInfo.InvariantCulture)
                        .CompareTo(Convert.ToBoolean(right, CultureInfo.InvariantCulture));
                case FieldType.Date:
                case FieldType.DateTime:
                    return Convert.ToDateTime(left, CultureInfo.InvariantCulture)
                        .CompareTo(Convert.ToDateTime(right, CultureInfo.InvariantCulture));
            }
        }
        catch (FormatException)
        {
        }
        catch (InvalidCastException)
        {
        }

        if (left is IComparable leftComparable && left.GetType() == right.GetType())
        {
            return leftComparable.CompareTo(right);
        }

        if (left is IConvertible && right is IConvertible)
        {
            try
            {
                var leftNumber = Convert.ToDouble(left, CultureInfo.InvariantCulture);
                var rightNumber = Convert.ToDouble(right, CultureInfo.InvariantCulture);
                return leftNumber.CompareTo(rightNumber);
            }
            catch (FormatException)
            {
            }
            catch (InvalidCastException)
            {
            }
        }

        return string.Compare(left.ToString(), right.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    private static string? ConvertSqlFragmentToWhereClause(SqlFragment sqlFragment)
    {
        if (string.IsNullOrWhiteSpace(sqlFragment.Sql))
        {
            return null;
        }

        var sql = sqlFragment.Sql;
        for (var i = sqlFragment.Parameters.Count - 1; i >= 0; i--)
        {
            var literal = FormatSqlLiteral(sqlFragment.Parameters[i]);
            sql = sql.Replace($"@p{i}", literal, StringComparison.Ordinal);
        }

        return sql;
    }

    private static string FormatSqlLiteral(object? value)
    {
        if (value == null)
        {
            return "NULL";
        }

        if (value is JsonElement element)
        {
            return FormatSqlLiteral(ConvertJsonElement(element));
        }

        return value switch
        {
            string strValue => $"'{strValue.Replace("'", "''")}'",
            bool boolValue => boolValue ? "true" : "false",
            DateTime dateTime => $"'{dateTime.ToString("O", CultureInfo.InvariantCulture)}'",
            DateTimeOffset dateTimeOffset => $"'{dateTimeOffset.ToString("O", CultureInfo.InvariantCulture)}'",
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture) ?? "NULL",
            _ => $"'{value.ToString()?.Replace("'", "''")}'"
        };
    }

    private static object? ConvertJsonElement(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.TryGetInt64(out var longValue) ? longValue :
                                    element.TryGetDouble(out var doubleValue) ? doubleValue :
                                    element.GetDecimal(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            _ => element.ToString()
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
        if (!TryReadGeometry(spatialFilter.Geometry, out var filterGeometry))
        {
            return Enumerable.Empty<Feature>();
        }

        return features.Where(feature =>
        {
            if (feature.Geometry == null)
            {
                return false;
            }

            if (!TryReadGeometry(feature.Geometry, out var featureGeometry))
            {
                return false;
            }

            return spatialFilter.SpatialRelationship switch
            {
                SpatialRelationship.Intersects => featureGeometry.Intersects(filterGeometry),
                SpatialRelationship.Contains => featureGeometry.Contains(filterGeometry),
                SpatialRelationship.Within => featureGeometry.Within(filterGeometry),
                SpatialRelationship.EnvelopeIntersects => featureGeometry.EnvelopeInternal.Intersects(filterGeometry.EnvelopeInternal),
                SpatialRelationship.Crosses => featureGeometry.Crosses(filterGeometry),
                SpatialRelationship.Touches => featureGeometry.Touches(filterGeometry),
                SpatialRelationship.Overlaps => featureGeometry.Overlaps(filterGeometry),
                SpatialRelationship.Disjoint => featureGeometry.Disjoint(filterGeometry),
                SpatialRelationship.Equals => featureGeometry.Equals(filterGeometry),
                SpatialRelationship.WithinDistance or SpatialRelationship.BeyondDistance => MatchDistanceFilter(featureGeometry, filterGeometry, spatialFilter),
                _ => false
            };
        });
    }

    private static bool TryReadGeometry(byte[] wkb, out Geometry geometry)
    {
        geometry = null!;

        try
        {
            var reader = new WKBReader();
            geometry = reader.Read(wkb);
            return geometry != null;
        }
        catch
        {
            return false;
        }
    }

    private static bool MatchDistanceFilter(Geometry featureGeometry, Geometry filterGeometry, SpatialFilter spatialFilter)
    {
        if (!spatialFilter.Distance.HasValue)
        {
            return false;
        }

        var distanceMeters = TryCalculateDistanceMeters(featureGeometry, filterGeometry);
        if (!distanceMeters.HasValue)
        {
            return false;
        }

        var thresholdMeters = ConvertDistanceToMeters(spatialFilter.Distance.Value, spatialFilter.DistanceUnit);
        return spatialFilter.SpatialRelationship == SpatialRelationship.WithinDistance
            ? distanceMeters.Value <= thresholdMeters
            : distanceMeters.Value > thresholdMeters;
    }

    private static double? TryCalculateDistanceMeters(Geometry featureGeometry, Geometry filterGeometry)
    {
        if (featureGeometry is Point featurePoint && filterGeometry is Point filterPoint)
        {
            return HaversineMeters(
                featurePoint.Y,
                featurePoint.X,
                filterPoint.Y,
                filterPoint.X);
        }

        return null;
    }

    private static double ConvertDistanceToMeters(double distance, DistanceUnit unit)
    {
        return unit switch
        {
            DistanceUnit.Meters => distance,
            DistanceUnit.Kilometers => distance * 1000.0,
            DistanceUnit.Miles => distance * 1609.344,
            DistanceUnit.Feet => distance * 0.3048,
            _ => distance
        };
    }

    private static double HaversineMeters(double lat1, double lon1, double lat2, double lon2)
    {
        const double earthRadiusMeters = 6371000.0;

        var dLat = DegreesToRadians(lat2 - lat1);
        var dLon = DegreesToRadians(lon2 - lon1);
        var lat1Rad = DegreesToRadians(lat1);
        var lat2Rad = DegreesToRadians(lat2);

        var a = Math.Pow(Math.Sin(dLat / 2.0), 2.0) +
                Math.Cos(lat1Rad) * Math.Cos(lat2Rad) *
                Math.Pow(Math.Sin(dLon / 2.0), 2.0);

        var c = 2.0 * Math.Asin(Math.Sqrt(a));
        return earthRadiusMeters * c;
    }

    private static double DegreesToRadians(double degrees) => degrees * (Math.PI / 180.0);

    public Task<QueryResult<Feature>> QueryRelatedAsync(int layerId, RelatedQuery query, CancellationToken cancellationToken = default)
    {
        if (!_layerFeatures.TryGetValue(query.Relationship.RelatedLayerId, out var relatedFeatures))
        {
            return Task.FromResult(QueryResult<Feature>.Empty());
        }

        var originIds = query.ObjectIds.ToHashSet();
        var destinationField = query.Relationship.DestinationForeignKeyField;

        var matched = relatedFeatures
            .Select(feature =>
            {
                var attributes = feature.Attributes as ImmutableDictionary<string, object?>
                    ?? feature.Attributes.ToImmutableDictionary(kvp => kvp.Key, kvp => kvp.Value);

                if (TryGetLongAttributeValue(attributes, destinationField, out var originId) &&
                    originIds.Contains(originId))
                {
                    return (feature, originId, isMatch: true);
                }

                return (feature, originId: 0L, isMatch: false);
            })
            .Where(item => item.isMatch)
            .ToList();

        IEnumerable<(Feature feature, long originId)> filtered = matched.Select(item => (item.feature, item.originId));

        if (!string.IsNullOrWhiteSpace(query.Where))
        {
            filtered = ApplyWhereFilter(filtered.Select(item => item.feature), query.Where)
                .Select(feature =>
                {
                    var attributes = feature.Attributes as ImmutableDictionary<string, object?>
                        ?? feature.Attributes.ToImmutableDictionary(kvp => kvp.Key, kvp => kvp.Value);

                    TryGetLongAttributeValue(attributes, destinationField, out var originId);
                    return (feature, originId);
                });
        }

        var filteredList = filtered.ToList();
        var totalCount = filteredList.Count;

        var limitedFeatures = new List<(Feature feature, long originId)>();
        var hasMoreResults = false;

        if (query.Limit.HasValue)
        {
            var limit = query.Limit.Value;
            foreach (var group in filteredList.GroupBy(item => item.originId))
            {
                var groupList = group.ToList();
                if (groupList.Count > limit)
                {
                    hasMoreResults = true;
                }
                limitedFeatures.AddRange(groupList.Take(limit));
            }
        }
        else
        {
            limitedFeatures = filteredList;
        }

        var resultFeatures = limitedFeatures.Select(item => item.feature).ToList();

        if (query.OutFields?.Length > 0)
        {
            resultFeatures = resultFeatures.Select(feature => FilterFields(feature, query.OutFields.Value)).ToList();
        }

        return Task.FromResult(QueryResult<Feature>.Create(
            totalCount,
            resultFeatures.ToImmutableArray(),
            hasMoreResults));
    }

    private static bool TryGetLongAttributeValue(ImmutableDictionary<string, object?> attributes, string field, out long value)
    {
        value = default;

        if (!attributes.TryGetValue(field, out var rawValue) || rawValue == null)
        {
            return false;
        }

        return rawValue switch
        {
            long longValue => AssignValue(longValue, out value),
            int intValue => AssignValue(intValue, out value),
            string stringValue when long.TryParse(stringValue, out var parsed) => AssignValue(parsed, out value),
            JsonElement element when element.ValueKind == JsonValueKind.Number => AssignValue(element.GetInt64(), out value),
            _ => false
        };
    }

    private static bool AssignValue(long newValue, out long value)
    {
        value = newValue;
        return true;
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
