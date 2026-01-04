// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.RegularExpressions;
using Honua.Core.Exceptions;
using Honua.Core.Features.Catalog.Abstractions;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Queries.Filters;
using Honua.Server.Features.OData.Models;

namespace Honua.Server.Features.OData.Services;

/// <summary>
/// Log category for OData search operations.
/// </summary>
internal sealed class ODataSearchLog;

/// <summary>
/// Service for handling OData search and aggregation operations.
/// Supports $search full-text search and $apply aggregation transformations.
/// </summary>
internal sealed partial class ODataSearchService
{
    private readonly ILayerCatalog _layerCatalog;
    private readonly IFeatureStore _featureStore;
    private readonly ODataQueryService _queryService;

    /// <summary>
    /// Initializes a new instance of the <see cref="ODataSearchService"/> class.
    /// </summary>
    public ODataSearchService(
        ILayerCatalog layerCatalog,
        IFeatureStore featureStore,
        ODataQueryService queryService)
    {
        _layerCatalog = layerCatalog;
        _featureStore = featureStore;
        _queryService = queryService;
    }

    /// <summary>
    /// Handles OData $search full-text search operations with PostgreSQL text search.
    /// </summary>
    public async Task<ODataSearchResult> HandleSearchAsync(
        int layerId,
        string searchExpression,
        string baseUrl,
        int? top = null,
        int? skip = null,
        bool? count = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(searchExpression))
        {
            throw new ArgumentException("$search parameter is required.");
        }

        // Verify layer exists
        var layer = await _layerCatalog.GetLayerAsync(layerId, cancellationToken)
            ?? throw new ResourceNotFoundException($"Layer {layerId} not found");

        // Build a text search query using PostgreSQL full-text search
        var searchTerms = ParseSearchExpression(searchExpression);
        var textSearchCondition = BuildTextSearchCondition(searchTerms, layer);

        var query = new FeatureQuery
        {
            SqlFilter = new SqlFragment(textSearchCondition, Array.Empty<object?>()),
            Limit = top ?? 1000,
            Offset = skip
        };

        var result = await _featureStore.QueryAsync(layerId, query, cancellationToken);

        // Convert features to OData format
        var featuresData = result.Items.Select(f => new Dictionary<string, object?>
        {
            ["ObjectId"] = f.Id,
            ["LayerId"] = layerId,
            ["Geometry"] = f.Geometry != null ? Convert.ToBase64String(f.Geometry) : null,
            ["Attributes"] = SerializeAttributes(f.Attributes)
        }).ToArray();

        return new ODataSearchResult
        {
            Context = $"{baseUrl}/odata/$metadata#Features",
            Count = count == true ? result.TotalCount : null,
            Value = featuresData.Cast<object>().ToArray()
        };
    }

    /// <summary>
    /// Handles OData $apply aggregation operations with support for various transformations.
    /// </summary>
    public async Task<ODataAggregationResult> HandleApplyAsync(
        int layerId,
        string applyExpression,
        string? filter,
        string baseUrl,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(applyExpression))
        {
            throw new ArgumentException("$apply parameter is required.");
        }

        // Verify layer exists
        var layer = await _layerCatalog.GetLayerAsync(layerId, cancellationToken)
            ?? throw new ResourceNotFoundException($"Layer {layerId} not found");

        // Use existing aggregation handler for processing
        var handler = new ODataAggregationHandler(_featureStore, _queryService);
        return await handler.ProcessAggregationAsync(layerId, applyExpression, filter, baseUrl, cancellationToken);
    }

    /// <summary>
    /// Parses an OData $search expression into structured search terms.
    /// Supports: simple terms, quoted phrases, AND, OR, NOT operators.
    /// </summary>
    private static List<List<(string term, bool isNegated, bool isPhrase)>> ParseSearchExpression(string search)
    {
        var termGroups = new List<List<(string term, bool isNegated, bool isPhrase)>>();
        var currentGroup = new List<(string term, bool isNegated, bool isPhrase)>();
        var negate = false;

        var tokenMatches = SearchTokenRegex().Matches(search);

        foreach (Match match in tokenMatches)
        {
            var token = match.Value;

            if (token.Equals("OR", StringComparison.OrdinalIgnoreCase))
            {
                if (currentGroup.Count > 0)
                {
                    termGroups.Add(currentGroup);
                    currentGroup = new List<(string term, bool isNegated, bool isPhrase)>();
                }
                negate = false;
                continue;
            }

            if (token.Equals("AND", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (token.Equals("NOT", StringComparison.OrdinalIgnoreCase))
            {
                negate = true;
                continue;
            }

            var isPhrase = token.Length >= 2 && token.StartsWith('"') && token.EndsWith('"');
            var term = isPhrase ? token[1..^1] : token.Trim('(', ')');

            if (!string.IsNullOrWhiteSpace(term))
            {
                currentGroup.Add((term, negate, isPhrase));
                negate = false;
            }
        }

        if (currentGroup.Count > 0)
        {
            termGroups.Add(currentGroup);
        }

        return termGroups;
    }

    /// <summary>
    /// Builds a PostgreSQL text search condition from parsed search terms.
    /// Uses ILIKE for case-insensitive pattern matching across text fields.
    /// </summary>
    private static string BuildTextSearchCondition(
        List<List<(string term, bool isNegated, bool isPhrase)>> terms,
        LayerDefinition layer)
    {
        if (terms.Count == 0)
        {
            return "1=1"; // No search terms, match all
        }

        // Get text-searchable fields from the layer
        var textFields = layer.AttributeFields
            .Where(f => f.Type == FieldType.String)
            .Select(f => f.Name)
            .ToList();

        if (textFields.Count == 0)
        {
            return "1=0"; // No text fields to search
        }

        var groupConditions = new List<string>();

        foreach (var group in terms)
        {
            if (group.Count == 0)
            {
                continue;
            }

            var groupParts = new List<string>();

            foreach (var (term, isNegated, isPhrase) in group)
            {
                // Escape the term for SQL ILIKE
                var escapedTerm = term
                    .Replace("'", "''")
                    .Replace("%", "\\%")
                    .Replace("_", "\\_");

                var fieldConditions = textFields
                    .Select(f => $"COALESCE(attributes->>'{f}', '') ILIKE '%{escapedTerm}%'")
                    .ToList();

                var condition = $"({string.Join(" OR ", fieldConditions)})";

                if (isNegated)
                {
                    condition = $"NOT {condition}";
                }

                groupParts.Add(condition);
            }

            if (groupParts.Count == 0)
            {
                continue;
            }

            var groupCondition = groupParts.Count == 1
                ? groupParts[0]
                : $"({string.Join(" AND ", groupParts)})";

            groupConditions.Add(groupCondition);
        }

        if (groupConditions.Count == 0)
        {
            return "1=1";
        }

        return string.Join(" OR ", groupConditions);
    }

    /// <summary>
    /// Processes $expand to fetch related entities for features.
    /// Handles relationships and foreign key mappings.
    /// </summary>
    public async Task<Dictionary<long, Dictionary<string, object?[]>>> ProcessExpandAsync(
        string expand,
        LayerDefinition layer,
        long[] objectIds,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<long, Dictionary<string, object?[]>>();

        if (objectIds.Length == 0)
        {
            return result;
        }

        // Parse $expand expression - comma-separated list of relationship names
        var relationshipNames = expand
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Find matching relationships
        foreach (var relationship in layer.LayerRelationships)
        {
            if (!relationshipNames.Contains(relationship.Name))
            {
                continue;
            }

            // Query related features
            var relatedQuery = RelatedQuery.ForObjects(objectIds, relationship);
            var relatedResult = await _featureStore.QueryRelatedAsync(layer.Id, relatedQuery, cancellationToken);

            // Group related features by origin object ID
            foreach (var feature in relatedResult.Items)
            {
                // Try to get the origin key from the related feature's attributes
                if (!feature.Attributes.TryGetValue(relationship.DestinationForeignKeyField, out var originKeyValue))
                {
                    continue;
                }

                // Convert the origin key to long if possible
                long? originId = originKeyValue switch
                {
                    long l => l,
                    int i => i,
                    string s when long.TryParse(s, out var parsed) => parsed,
                    System.Text.Json.JsonElement je when je.ValueKind == System.Text.Json.JsonValueKind.Number => je.GetInt64(),
                    _ => null
                };

                if (!originId.HasValue)
                {
                    continue;
                }

                if (!result.TryGetValue(originId.Value, out var relationsDict))
                {
                    relationsDict = new Dictionary<string, object?[]>();
                    result[originId.Value] = relationsDict;
                }

                var relatedFeatureDict = new Dictionary<string, object?>
                {
                    ["ObjectId"] = feature.Id,
                    ["Attributes"] = SerializeAttributes(feature.Attributes)
                };

                if (relationsDict.TryGetValue(relationship.Name, out var existingRelations))
                {
                    var newRelations = new object?[existingRelations.Length + 1];
                    Array.Copy(existingRelations, newRelations, existingRelations.Length);
                    newRelations[existingRelations.Length] = relatedFeatureDict;
                    relationsDict[relationship.Name] = newRelations;
                }
                else
                {
                    relationsDict[relationship.Name] = new object?[] { relatedFeatureDict };
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Serializes feature attributes to JSON string format.
    /// </summary>
    private static string SerializeAttributes(IReadOnlyDictionary<string, object?> attributes)
    {
        var normalized = attributes.ToDictionary(kvp => kvp.Key, kvp => NormalizeODataValue(kvp.Value));
        return System.Text.Json.JsonSerializer.Serialize(normalized, ODataJsonContext.Default.DictionaryStringObject);
    }

    /// <summary>
    /// Normalizes values for OData serialization, handling JSON elements and collections.
    /// </summary>
    private static object? NormalizeODataValue(object? value)
    {
        if (value is null)
        {
            return null;
        }

        if (value is System.Text.Json.JsonElement element)
        {
            return ConvertJsonElement(element);
        }

        if (value is IReadOnlyDictionary<string, object?> readOnlyDict)
        {
            return readOnlyDict.ToDictionary(kvp => kvp.Key, kvp => NormalizeODataValue(kvp.Value));
        }

        if (value is IDictionary<string, object?> dict)
        {
            return dict.ToDictionary(kvp => kvp.Key, kvp => NormalizeODataValue(kvp.Value));
        }

        if (value is System.Collections.IEnumerable enumerable && value is not string)
        {
            var list = new List<object?>();
            foreach (var item in enumerable)
            {
                list.Add(NormalizeODataValue(item));
            }

            return list.ToArray();
        }

        return value;
    }

    /// <summary>
    /// Converts JsonElement to appropriate .NET type for serialization.
    /// </summary>
    private static object? ConvertJsonElement(System.Text.Json.JsonElement element)
    {
        return element.ValueKind switch
        {
            System.Text.Json.JsonValueKind.String => element.GetString(),
            System.Text.Json.JsonValueKind.Number => element.TryGetInt64(out var longVal) ? longVal :
                element.TryGetDouble(out var doubleVal) ? doubleVal :
                element.GetDecimal(),
            System.Text.Json.JsonValueKind.True => true,
            System.Text.Json.JsonValueKind.False => false,
            System.Text.Json.JsonValueKind.Null => null,
            System.Text.Json.JsonValueKind.Object => element.EnumerateObject()
                .ToDictionary(prop => prop.Name, prop => ConvertJsonElement(prop.Value)),
            System.Text.Json.JsonValueKind.Array => element.EnumerateArray().Select(ConvertJsonElement).ToArray(),
            _ => element.GetRawText()
        };
    }

    /// <summary>
    /// Regex for parsing search tokens including quoted phrases and operators.
    /// </summary>
    [GeneratedRegex(@"""[^""]+""|\S+", RegexOptions.CultureInvariant)]
    private static partial Regex SearchTokenRegex();

    /// <summary>
    /// Logging methods for OData search operations.
    /// </summary>
    private static partial class Log
    {
        /// <summary>
        /// Logs when an OData $search operation fails.
        /// </summary>
        [LoggerMessage(EventId = 3014, Level = LogLevel.Error, Message = "OData $search failed for layer {LayerId}.")]
        public static partial void SearchFailed(ILogger logger, int layerId, Exception exception);

        /// <summary>
        /// Logs when an invalid OData $apply expression is received.
        /// </summary>
        [LoggerMessage(EventId = 3012, Level = LogLevel.Warning, Message = "Invalid OData $apply expression for layer {LayerId}.")]
        public static partial void InvalidApplyExpression(ILogger logger, int layerId, Exception exception);

        /// <summary>
        /// Logs when an OData $apply aggregation operation fails.
        /// </summary>
        [LoggerMessage(EventId = 3013, Level = LogLevel.Error, Message = "OData $apply aggregation failed for layer {LayerId}.")]
        public static partial void ApplyFailed(ILogger logger, int layerId, Exception exception);
    }
}
