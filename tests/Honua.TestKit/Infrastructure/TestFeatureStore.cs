// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Queries.Filters;

namespace Honua.TestKit.Infrastructure;

/// <summary>
/// Test implementation of feature store interfaces for unit and integration tests
/// </summary>
public sealed class TestFeatureStore : IFeatureReader, IFeatureWriter, ITileProvider, IRelationshipStore, IStreamingFeatureStore, IDisposable
{
    private const string UnsupportedWhereClauseMessage =
        "WHERE clause format not supported. Use simple comparisons like: name = 'value' or age > 18";

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
                .Add("category", "test")
                .Add("timestamp", new DateTimeOffset(2023, 01, 02, 0, 0, 0, TimeSpan.Zero))),
            Feature.Create(2, CreatePointWkb(-122.7, 37.7), ImmutableDictionary<string, object?>.Empty
                .Add("objectid", 2)
                .Add("name", "Another Feature")
                .Add("description", "Another test feature")
                .Add("category", "sample")
                .Add("timestamp", new DateTimeOffset(2023, 01, 05, 12, 0, 0, TimeSpan.Zero))),
            Feature.Create(3, null, ImmutableDictionary<string, object?>.Empty
                .Add("objectid", 3)
                .Add("name", "Third Feature")
                .Add("description", "Third test feature")
                .Add("category", "test")
                .Add("timestamp", new DateTimeOffset(2023, 02, 10, 0, 0, 0, TimeSpan.Zero))),
            Feature.Create(4, CreatePointWkb(-121.9, 37.3), ImmutableDictionary<string, object?>.Empty
                .Add("objectid", 4)
                .Add("name", "Fourth Feature")
                .Add("description", "Fourth test feature")
                .Add("category", "sample")
                .Add("timestamp", new DateTimeOffset(2022, 12, 31, 23, 0, 0, TimeSpan.Zero))),
            Feature.Create(5, CreatePointWkb(-122.3, 37.8), ImmutableDictionary<string, object?>.Empty
                .Add("objectid", 5)
                .Add("name", "Fifth Feature")
                .Add("description", "Fifth test feature")
                .Add("category", "test")
                .Add("timestamp", new DateTimeOffset(2023, 01, 20, 0, 0, 0, TimeSpan.Zero)))
        };
    }

    public static Feature CreateTestFeature(long id, double x, double y, IDictionary<string, object?>? attributes = null)
    {
        var attributeMap = attributes is null
            ? ImmutableDictionary<string, object?>.Empty
            : attributes.ToImmutableDictionary();

        return Feature.Create(id, CreatePointWkb(x, y), attributeMap);
    }

    public Task<Feature?> GetAsync(int layerId, long featureId, CancellationToken cancellationToken = default)
    {
        if (!_layerFeatures.TryGetValue(layerId, out var features))
            return Task.FromResult<Feature?>(null);

        var index = features.FindIndex(f => f.Id == featureId);
        if (index == -1)
            return Task.FromResult<Feature?>(null);

        return Task.FromResult<Feature?>(features[index]);
    }

    public Task<QueryResult<Feature>> QueryAsync(int layerId, FeatureQuery query, CancellationToken cancellationToken = default)
    {
        if (!_layerFeatures.TryGetValue(layerId, out var features))
        {
            return Task.FromResult(QueryResult<Feature>.Empty());
        }

        var filteredFeatures = features.AsEnumerable();

        // Apply objectIds filtering (takes precedence over WHERE)
        if (query.ObjectIds.HasValue && query.ObjectIds.Value.Length > 0)
        {
            var objectIdSet = query.ObjectIds.Value.ToHashSet();
            filteredFeatures = filteredFeatures.Where(feature => objectIdSet.Contains(feature.Id));
        }
        else
        {
            var whereClause = !string.IsNullOrEmpty(query.Where)
                ? query.Where
                : query.SqlFilter is not null
                    ? ConvertSqlFragmentToWhereClause(query.SqlFilter)
                    : null;

            if (!string.IsNullOrEmpty(whereClause))
            {
                filteredFeatures = ApplyWhereFilter(filteredFeatures, whereClause);
            }
        }

        // Apply spatial filtering
        if (query.SpatialFilter != null)
        {
            filteredFeatures = ApplySpatialFilter(filteredFeatures, query.SpatialFilter.Value);
        }

        // Apply temporal filtering
        if (query.TemporalFilter.HasValue)
        {
            filteredFeatures = ApplyTemporalFilter(filteredFeatures, query.TemporalFilter.Value);
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

    public async IAsyncEnumerable<Feature> StreamFeaturesAsync(
        int layerId,
        FeatureQuery query,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var result = await QueryAsync(layerId, query, cancellationToken);
        foreach (var feature in result.Items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return feature;
        }
    }

    public async IAsyncEnumerable<IReadOnlyList<Feature>> StreamFeatureBatchesAsync(
        int layerId,
        FeatureQuery query,
        int batchSize = 1000,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(batchSize);

        var result = await QueryAsync(layerId, query, cancellationToken);
        var features = result.Items;

        for (var i = 0; i < features.Length; i += batchSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = Math.Min(batchSize, features.Length - i);
            var batch = features.Skip(i).Take(count).ToArray();
            yield return batch;
        }
    }

    public async IAsyncEnumerable<GmlFeature> StreamGmlFeaturesAsync(
        int layerId,
        FeatureQuery query,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var result = await QueryAsync(layerId, query, cancellationToken);
        foreach (var feature in result.Items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return GmlFeature.Create(feature.Id, geometryGml: null, feature.Attributes);
        }
    }

    public Task<long> CountAsync(int layerId, FeatureQuery query, CancellationToken cancellationToken = default)
    {
        if (!_layerFeatures.TryGetValue(layerId, out var features))
            return Task.FromResult(0L);

        var filteredFeatures = features.AsEnumerable();

        if (query.ObjectIds.HasValue && query.ObjectIds.Value.Length > 0)
        {
            var objectIdSet = query.ObjectIds.Value.ToHashSet();
            filteredFeatures = filteredFeatures.Where(feature => objectIdSet.Contains(feature.Id));
        }
        else
        {
            var whereClause = !string.IsNullOrEmpty(query.Where)
                ? query.Where
                : query.SqlFilter is not null
                    ? ConvertSqlFragmentToWhereClause(query.SqlFilter)
                    : null;

            if (!string.IsNullOrEmpty(whereClause))
            {
                filteredFeatures = ApplyWhereFilter(filteredFeatures, whereClause);
            }
        }

        if (query.TemporalFilter.HasValue)
        {
            filteredFeatures = ApplyTemporalFilter(filteredFeatures, query.TemporalFilter.Value);
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
        List<Feature>? snapshot = null;
        if (editBatch.RollbackOnFailure && _layerFeatures.TryGetValue(layerId, out var existingFeatures))
        {
            snapshot = existingFeatures.ToList();
        }

        var createdIds = new List<long>();
        var createResults = new List<EditOperationResult>();
        var updateResults = new List<EditOperationResult>();
        var deleteResults = new List<EditOperationResult>();

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

        // Process updates
        var updatedCount = 0;
        foreach (var feature in editBatch.Updates)
        {
            try
            {
                var updated = await UpdateAsync(layerId, feature, cancellationToken);
                updatedCount++;
                updateResults.Add(EditOperationResult.Success(updated.Id, feature.Attributes.GetValueOrDefault("globalId")?.ToString()));
            }
            catch (Exception ex)
            {
                updateResults.Add(EditOperationResult.Failure($"Failed to update feature {feature.Id}: {ex.Message}", objectId: feature.Id));
            }
        }

        // Process deletes
        var deletedCount = 0;
        foreach (var featureId in editBatch.Deletes)
        {
            try
            {
                var deleted = await DeleteAsync(layerId, featureId, cancellationToken);
                if (deleted)
                {
                    deletedCount++;
                    deleteResults.Add(EditOperationResult.Success(featureId));
                }
                else
                {
                    deleteResults.Add(EditOperationResult.Failure($"Feature {featureId} not found", objectId: featureId));
                }
            }
            catch (Exception ex)
            {
                deleteResults.Add(EditOperationResult.Failure($"Failed to delete feature {featureId}: {ex.Message}", objectId: featureId));
            }
        }

        // Check if any operations failed
        var hasErrors = createResults.Any(r => !r.IsSuccess) ||
                        updateResults.Any(r => !r.IsSuccess) ||
                        deleteResults.Any(r => !r.IsSuccess);

        if (hasErrors && editBatch.RollbackOnFailure)
        {
            // Rollback all operations
            if (snapshot != null)
            {
                _layerFeatures[layerId] = snapshot;
            }

            return FeatureEditResult.Rollback(
                createResults.ToImmutableArray(),
                updateResults.ToImmutableArray(),
                deleteResults.ToImmutableArray());
        }

        return FeatureEditResult.Success(
            createdCount: hasErrors ? createResults.Count(r => r.IsSuccess) : createdCount,
            updatedCount: hasErrors ? updateResults.Count(r => r.IsSuccess) : updatedCount,
            deletedCount: hasErrors ? deleteResults.Count(r => r.IsSuccess) : deletedCount,
            createdIds: createdIds.ToImmutableArray(),
            createResults: createResults.ToImmutableArray(),
            updateResults: updateResults.ToImmutableArray(),
            deleteResults: deleteResults.ToImmutableArray());
    }

    public void Dispose()
    {
        _layerFeatures.Clear();
        GC.SuppressFinalize(this);
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

        sql = sql.Replace("\"", string.Empty, StringComparison.Ordinal);

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

    private static IEnumerable<Feature> ApplyWhereFilter(IEnumerable<Feature> features, string whereClause)
    {
        var normalized = whereClause.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return features;
        }

        if (normalized.Contains("drop", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains(';') ||
            normalized.Contains("--", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("WHERE clause contains dangerous pattern: " + normalized.Split(' ').First(), nameof(whereClause));
        }

        var clauses = Regex.Split(normalized, @"\s+AND\s+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var filtered = features;

        foreach (var clause in clauses)
        {
            var trimmedClause = clause.Trim();
            if (string.IsNullOrEmpty(trimmedClause))
            {
                continue;
            }

            if (IsTrueLiteral(trimmedClause))
            {
                continue;
            }

            if (TryParseInClause(trimmedClause, out var inField, out var inValues))
            {
                filtered = filtered.Where(feature =>
                    TryGetAttributeValue(feature, inField, out var value) &&
                    MatchesInValues(value, inValues));
                continue;
            }

            if (TryParseEqualityClause(trimmedClause, out var eqField, out var eqValue))
            {
                filtered = filtered.Where(feature =>
                    TryGetAttributeValue(feature, eqField, out var value) &&
                    ValuesEqual(value, eqValue));
                continue;
            }

            throw new ArgumentException(UnsupportedWhereClauseMessage, nameof(whereClause));
        }

        return filtered;
    }

    private static bool IsTrueLiteral(string clause)
    {
        return string.Equals(clause, "TRUE", StringComparison.OrdinalIgnoreCase) ||
               Regex.IsMatch(clause, @"^1\s*=\s*1$", RegexOptions.CultureInvariant);
    }

    private static bool TryGetAttributeValue(Feature feature, string fieldName, out object? value)
    {
        if (feature.Attributes.TryGetValue(fieldName, out value))
        {
            return true;
        }

        if (string.Equals(fieldName, "objectid", StringComparison.OrdinalIgnoreCase))
        {
            value = feature.Id;
            return true;
        }

        foreach (var (key, attributeValue) in feature.Attributes)
        {
            if (string.Equals(key, fieldName, StringComparison.OrdinalIgnoreCase))
            {
                value = attributeValue;
                return true;
            }
        }

        value = null;
        return false;
    }

    private static bool TryParseEqualityClause(string clause, out string fieldName, out object value)
    {
        var match = Regex.Match(
            clause,
            @"^(?<field>[a-zA-Z_][a-zA-Z0-9_]*)\s*=\s*(?<value>.+)$",
            RegexOptions.CultureInvariant);

        if (!match.Success)
        {
            fieldName = string.Empty;
            value = string.Empty;
            return false;
        }

        fieldName = match.Groups["field"].Value;
        var rawValue = match.Groups["value"].Value.Trim();

        if (rawValue.StartsWith('\'') && rawValue.EndsWith('\'') && rawValue.Length >= 2)
        {
            value = rawValue[1..^1].Replace("''", "'", StringComparison.Ordinal);
            return true;
        }

        if (long.TryParse(rawValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var longValue))
        {
            value = longValue;
            return true;
        }

        if (double.TryParse(rawValue, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var doubleValue))
        {
            value = doubleValue;
            return true;
        }

        value = rawValue;
        return true;
    }

    private static bool TryParseInClause(string clause, out string fieldName, out List<object> values)
    {
        var match = Regex.Match(
            clause,
            @"^(?<field>[a-zA-Z_][a-zA-Z0-9_]*)\s+IN\s*\((?<values>.+)\)$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        if (!match.Success)
        {
            fieldName = string.Empty;
            values = new List<object>();
            return false;
        }

        fieldName = match.Groups["field"].Value;
        var valuesRaw = match.Groups["values"].Value;

        values = valuesRaw
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(ParseValueLiteral)
            .ToList();

        return true;
    }

    private static object ParseValueLiteral(string rawValue)
    {
        if (rawValue.StartsWith('\'') && rawValue.EndsWith('\'') && rawValue.Length >= 2)
        {
            return rawValue[1..^1].Replace("''", "'", StringComparison.Ordinal);
        }

        if (long.TryParse(rawValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var longValue))
        {
            return longValue;
        }

        if (double.TryParse(rawValue, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var doubleValue))
        {
            return doubleValue;
        }

        return rawValue;
    }

    private static bool MatchesInValues(object? value, IReadOnlyList<object> candidates)
    {
        return candidates.Any(candidate => ValuesEqual(value, candidate));
    }

    private static bool ValuesEqual(object? left, object right)
    {
        if (left == null)
        {
            return false;
        }

        if (left is DateTimeOffset leftOffset)
        {
            if (right is DateTimeOffset rightOffset)
            {
                return leftOffset.Equals(rightOffset);
            }

            if (right is DateTime rightDateTime)
            {
                return leftOffset.UtcDateTime.Equals(rightDateTime.ToUniversalTime());
            }

            if (right is string rightText &&
                DateTimeOffset.TryParse(rightText, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsedOffset))
            {
                return leftOffset.Equals(parsedOffset);
            }
        }

        if (left is DateTime leftDateTime)
        {
            if (right is DateTime rightDateTime)
            {
                return leftDateTime.Equals(rightDateTime);
            }

            if (right is string rightText &&
                DateTime.TryParse(rightText, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsedDateTime))
            {
                return leftDateTime.Equals(parsedDateTime);
            }
        }

        if (left is string leftText)
        {
            return right is string rightText &&
                   string.Equals(leftText, rightText, StringComparison.OrdinalIgnoreCase);
        }

        if (left is IConvertible)
        {
            if (right is IConvertible)
            {
                try
                {
                    var leftValue = Convert.ToDouble(left, CultureInfo.InvariantCulture);
                    var rightValue = Convert.ToDouble(right, CultureInfo.InvariantCulture);
                    return Math.Abs(leftValue - rightValue) < double.Epsilon;
                }
                catch (FormatException)
                {
                    return false;
                }
            }
        }

        return left.Equals(right);
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
        // Handle KNN queries - sort by distance and take K nearest
        if (spatialFilter.SpatialRelationship == SpatialRelationship.NearestNeighbor)
        {
            var filterPoint = ParsePointFromWkb(spatialFilter.Geometry);
            if (filterPoint == null)
                return Enumerable.Empty<Feature>();

            var featuresWithDistance = features
                .Where(f => f.Geometry != null)
                .Select(f =>
                {
                    var point = ParsePointFromWkb(f.Geometry!);
                    var distance = point.HasValue ? CalculateDistance(filterPoint.Value, point.Value) : double.MaxValue;
                    return (Feature: f, Distance: distance);
                })
                .OrderBy(x => x.Distance);

            var limit = spatialFilter.NearestCount ?? 10;
            var result = featuresWithDistance.Take(limit);

            // If ReturnDistance is true, add distance to attributes
            if (spatialFilter.ReturnDistance)
            {
                return result.Select(x =>
                {
                    var attributesWithDistance = x.Feature.Attributes.SetItem("distance", x.Distance);
                    return Feature.Create(x.Feature.Id, x.Feature.Geometry, attributesWithDistance);
                });
            }

            return result.Select(x => x.Feature);
        }

        // Handle distance-based queries
        if (spatialFilter.SpatialRelationship == SpatialRelationship.WithinDistance ||
            spatialFilter.SpatialRelationship == SpatialRelationship.BeyondDistance)
        {
            var filterPoint = ParsePointFromWkb(spatialFilter.Geometry);
            if (filterPoint == null)
                return Enumerable.Empty<Feature>();

            var distanceMeters = ConvertDistanceToMeters(spatialFilter.Distance ?? 0, spatialFilter.DistanceUnit);

            return features.Where(feature =>
            {
                if (feature.Geometry == null)
                    return false;

                var point = ParsePointFromWkb(feature.Geometry);
                if (point == null)
                    return false;

                var actualDistance = CalculateDistance(filterPoint.Value, point.Value);

                return spatialFilter.SpatialRelationship == SpatialRelationship.WithinDistance
                    ? actualDistance <= distanceMeters
                    : actualDistance > distanceMeters;
            });
        }

        // Handle polygon-based spatial relationships
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
                SpatialRelationship.EnvelopeIntersects => IsPointInPolygon(point.Value, polygon),
                _ => false
            };
        });
    }

    /// <summary>
    /// Converts a distance value to meters based on the specified unit
    /// </summary>
    private static double ConvertDistanceToMeters(double distance, DistanceUnit unit)
    {
        return unit switch
        {
            DistanceUnit.Meters => distance,
            DistanceUnit.Feet => distance * 0.3048,
            DistanceUnit.Kilometers => distance * 1000,
            DistanceUnit.Miles => distance * 1609.344,
            _ => distance
        };
    }

    /// <summary>
    /// Calculates approximate distance in meters between two geographic points using Haversine formula
    /// </summary>
    private static double CalculateDistance((double x, double y) point1, (double x, double y) point2)
    {
        const double earthRadiusMeters = 6371000; // Earth's radius in meters

        var lat1 = point1.y * Math.PI / 180;
        var lat2 = point2.y * Math.PI / 180;
        var deltaLat = (point2.y - point1.y) * Math.PI / 180;
        var deltaLon = (point2.x - point1.x) * Math.PI / 180;

        var a = Math.Sin(deltaLat / 2) * Math.Sin(deltaLat / 2) +
                Math.Cos(lat1) * Math.Cos(lat2) *
                Math.Sin(deltaLon / 2) * Math.Sin(deltaLon / 2);
        var c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));

        return earthRadiusMeters * c;
    }

    /// <summary>
    /// Applies temporal filtering for test scenarios
    /// </summary>
    private static IEnumerable<Feature> ApplyTemporalFilter(IEnumerable<Feature> features, TemporalFilter temporalFilter)
    {
        return features.Where(feature =>
        {
            if (!feature.Attributes.TryGetValue(temporalFilter.PropertyName, out var rawValue) || rawValue == null)
            {
                return false;
            }

            if (temporalFilter.PropertyType == TemporalPropertyType.Date)
            {
                if (!TryGetDate(rawValue, out var dateValue))
                {
                    return false;
                }

                var startDate = temporalFilter.Start?.Date;
                var endDate = temporalFilter.End?.Date;

                if (startDate.HasValue && dateValue < startDate.Value)
                {
                    return false;
                }

                if (endDate.HasValue && dateValue > endDate.Value)
                {
                    return false;
                }

                return true;
            }

            if (!TryGetDateTimeOffset(rawValue, out var instant))
            {
                return false;
            }

            if (temporalFilter.Start.HasValue && instant < temporalFilter.Start.Value)
            {
                return false;
            }

            if (temporalFilter.End.HasValue && instant > temporalFilter.End.Value)
            {
                return false;
            }

            return true;
        });
    }

    private static bool TryGetDateTimeOffset(object value, out DateTimeOffset parsed)
    {
        switch (value)
        {
            case DateTimeOffset dto:
                parsed = dto;
                return true;
            case DateTime dt:
            {
                var normalized = dt.Kind == DateTimeKind.Unspecified
                    ? DateTime.SpecifyKind(dt, DateTimeKind.Utc)
                    : dt.ToUniversalTime();
                parsed = new DateTimeOffset(normalized);
                return true;
            }
            case string text:
                return DateTimeOffset.TryParse(
                    text,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out parsed);
            default:
                parsed = default;
                return false;
        }
    }

    private static bool TryGetDate(object value, out DateTime date)
    {
        switch (value)
        {
            case DateTimeOffset dto:
                date = dto.Date;
                return true;
            case DateTime dt:
                date = dt.Date;
                return true;
            case string text when DateTimeOffset.TryParse(
                    text,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out var parsed):
                date = parsed.Date;
                return true;
            default:
                date = default;
                return false;
        }
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
        // Basic implementation returns empty result for related records.
        return Task.FromResult(QueryResult<Feature>.Empty());
    }

    public Task<byte[]?> GetMvtTileAsync(int layerId, int x, int y, int z, FeatureQuery? query = null, CancellationToken cancellationToken = default)
    {
        // Return a simple mock MVT tile for testing
        if (!_layerFeatures.TryGetValue(layerId, out var features) || features.Count == 0)
        {
            return Task.FromResult<byte[]?>(null); // Empty tile
        }

        // Return a mock MVT tile with basic header
        var mockMvt = new byte[]
        {
            0x1A, 0x04, 0x6C, 0x61, 0x79, 0x65, 0x72, // Basic MVT header
            0x12, 0x02, 0x08, 0x01, // Mock feature data
            0x18, 0x03, 0x22, 0x02, 0x08, 0x01
        };

        return Task.FromResult<byte[]?>(mockMvt);
    }

    public Task<byte[]?> GetMvtTileAsync(int layerId, int x, int y, int z, FeatureQuery? query, Honua.Core.Features.Tiles.TileOptions tileOptions, CancellationToken cancellationToken = default)
    {
        // Call the existing method - tile options are used by the real PostgreSQL implementation but not needed for test mocks
        return GetMvtTileAsync(layerId, x, y, z, query, cancellationToken);
    }
}
