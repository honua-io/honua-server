// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text.RegularExpressions;
using Honua.Core.Exceptions;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Geometry.Abstractions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Shared.Models;
using Honua.Core.Features.Validation.Abstractions;
using Honua.Core.Queries.Filters;
using Honua.Server.Features.OData.Models;

namespace Honua.Server.Features.OData.Services;

/// <summary>
/// Service for handling OData search and aggregation operations.
/// Supports $search full-text search and $apply aggregation transformations.
/// </summary>
internal sealed partial class ODataSearchService
{
    private readonly IResourceValidator _resourceValidator;
    private readonly IFeatureReader _featureReader;
    private readonly IRelationshipStore _relationshipStore;
    private readonly IStreamingFeatureStore _streamingFeatureStore;
    private readonly IGeometryService _geometryService;
    private readonly ICrsRegistry _crsRegistry;
    private readonly ODataQueryService _queryService;

    /// <summary>
    /// Initializes a new instance of the <see cref="ODataSearchService"/> class.
    /// </summary>
    public ODataSearchService(
        IResourceValidator resourceValidator,
        IFeatureReader featureReader,
        IRelationshipStore relationshipStore,
        IStreamingFeatureStore streamingFeatureStore,
        IGeometryService geometryService,
        ICrsRegistry crsRegistry,
        ODataQueryService queryService)
    {
        _resourceValidator = resourceValidator;
        _featureReader = featureReader;
        _relationshipStore = relationshipStore;
        _streamingFeatureStore = streamingFeatureStore;
        _geometryService = geometryService;
        _crsRegistry = crsRegistry ?? throw new ArgumentNullException(nameof(crsRegistry));
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
        var layerResult = await _resourceValidator.ValidateLayerAsync(layerId, cancellationToken);
        if (!layerResult.IsValid)
        {
            var message = layerResult.ErrorMessage ?? $"Layer {layerId} not found";
            if (layerResult.ErrorCode == ResourceValidationError.InvalidIdentifier)
            {
                throw new ArgumentException(message);
            }

            throw new ResourceNotFoundException(message);
        }

        var layer = layerResult.Resource!;

        // Build a text search query using PostgreSQL full-text search
        var searchTerms = ParseSearchExpression(searchExpression);
        var textSearchFilter = BuildTextSearchCondition(searchTerms, layer);

        var query = new FeatureQuery
        {
            SqlFilter = textSearchFilter,
            Limit = top ?? 1000,
            Offset = skip
        };

        var result = await _featureReader.QueryAsync(layerId, query, cancellationToken);
        var axisOrder = await ODataCrsUtilities.ResolveAxisOrderAsync(
            _crsRegistry,
            layer.SpatialReference.ToSrid(),
            cancellationToken);

        // Convert features to OData format
        var featuresData = result.Items.Select(feature =>
        {
            var dict = new Dictionary<string, object?>
            {
                ["ObjectId"] = feature.Id,
                ["LayerId"] = layerId,
                ["Geometry"] = ODataGeometryConverter.ConvertWkbToGeometry(
                    _geometryService,
                    feature.Geometry,
                    layer.SpatialReference.ToSrid(),
                    axisOrder)
            };

            var attributes = ODataAttributeSerializer.Serialize(feature.Attributes);
            foreach (var (key, value) in attributes)
            {
                if (ODataUtilityService.IsReservedFeatureProperty(key))
                {
                    continue;
                }

                dict[key] = value;
            }

            return dict;
        }).ToArray();

        return new ODataSearchResult
        {
            Context = ODataUtilityService.BuildContextUrl(baseUrl, "Features"),
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
        var layerResult = await _resourceValidator.ValidateLayerAsync(layerId, cancellationToken);
        if (!layerResult.IsValid)
        {
            var message = layerResult.ErrorMessage ?? $"Layer {layerId} not found";
            if (layerResult.ErrorCode == ResourceValidationError.InvalidIdentifier)
            {
                throw new ArgumentException(message);
            }

            throw new ResourceNotFoundException(message);
        }

        var layer = layerResult.Resource!;
        var aggregation = ODataAggregationHandler.ParseApplyExpression(applyExpression);
        ValidateAggregationFields(aggregation, layer);

        // Use existing aggregation handler for processing
        var handler = new ODataAggregationHandler(_featureReader, _streamingFeatureStore, _queryService);
        return await handler.ProcessAggregationAsync(layerId, layer, applyExpression, filter, baseUrl, cancellationToken);
    }

    private static void ValidateAggregationFields(ParsedAggregation aggregation, LayerDefinition layer)
    {
        if (aggregation.Type is AggregationType.Aggregate or AggregationType.GroupBy)
        {
            if (aggregation.Aggregates.HasValue)
            {
                foreach (var aggregate in aggregation.Aggregates.Value)
                {
                    if (aggregate.Field == "*")
                    {
                        continue;
                    }

                    if (!FieldExists(layer, aggregate.Field))
                    {
                        throw new ArgumentException($"Unknown field '{aggregate.Field}' in $apply.");
                    }
                }
            }

            if (aggregation.Type == AggregationType.GroupBy && aggregation.GroupByFields.HasValue)
            {
                foreach (var field in aggregation.GroupByFields.Value)
                {
                    if (!FieldExists(layer, field))
                    {
                        throw new ArgumentException($"Unknown field '{field}' in $apply.");
                    }
                }
            }
        }
    }

    private static bool FieldExists(LayerDefinition layer, string field)
    {
        if (field.Equals(FieldNames.ObjectId, StringComparison.OrdinalIgnoreCase) ||
            field.Equals("layerid", StringComparison.OrdinalIgnoreCase) ||
            field.Equals("geometry", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return layer.Fields.Any(f => f.Name.Equals(field, StringComparison.OrdinalIgnoreCase));
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
    /// Builds a parameterized PostgreSQL text search condition from parsed search terms.
    /// Uses ILIKE with an explicit ESCAPE clause for safe pattern matching across text fields.
    /// </summary>
    private static SqlFragment BuildTextSearchCondition(
        List<List<(string term, bool isNegated, bool isPhrase)>> terms,
        LayerDefinition layer)
    {
        if (terms.Count == 0)
        {
            return new SqlFragment("1=1", Array.Empty<object?>()); // No search terms, match all
        }

        // Get text-searchable fields from the layer
        var textFields = layer.AttributeFields
            .Where(f => f.Type == FieldType.String)
            .Select(f => f.Name)
            .ToList();

        if (textFields.Count == 0)
        {
            return new SqlFragment("1=0", Array.Empty<object?>()); // No text fields to search
        }

        var groupConditions = new List<string>();
        var parameters = new List<object?>();
        var paramIndex = 0;

        foreach (var group in terms)
        {
            if (group.Count == 0)
            {
                continue;
            }

            var groupParts = new List<string>();

            foreach (var (term, isNegated, isPhrase) in group)
            {
                var escapedTerm = EscapeLikeTerm(term);
                var likePattern = $"%{escapedTerm}%";
                var termParameter = $"@p{paramIndex++}";
                parameters.Add(likePattern);

                var fieldConditions = textFields
                    .Select(f =>
                    {
                        var escapedField = f.Replace("'", "''", StringComparison.Ordinal);
                        return $"COALESCE(attributes->>'{escapedField}', '') ILIKE {termParameter} ESCAPE '\\'";
                    })
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
            return new SqlFragment("1=1", parameters);
        }

        return new SqlFragment(string.Join(" OR ", groupConditions), parameters);
    }

    private static string EscapeLikeTerm(string term)
    {
        return term
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);
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

        // Parse $expand expression and accept expand options, e.g. Rel($select=Name).
        var relationshipNames = ParseExpandRelationshipNames(expand);

        // Find matching relationships
        foreach (var relationship in layer.LayerRelationships)
        {
            var sanitizedName = ODataUtilityService.SanitizeIdentifier(relationship.Name);
            if (!relationshipNames.Contains(relationship.Name) &&
                !relationshipNames.Contains(sanitizedName))
            {
                continue;
            }

            var relatedLayerResult = await _resourceValidator.ValidateLayerAsync(
                relationship.RelatedLayerId,
                cancellationToken);
            if (!relatedLayerResult.IsValid || relatedLayerResult.Resource == null)
            {
                continue;
            }

            var relatedLayer = relatedLayerResult.Resource;
            var relatedAxisOrder = await ODataCrsUtilities.ResolveAxisOrderAsync(
                _crsRegistry,
                relatedLayer.SpatialReference.ToSrid(),
                cancellationToken);

            // Query related features
            var relatedQuery = RelatedQuery.ForObjects(objectIds, relationship);
            var relatedResult = await _relationshipStore.QueryRelatedAsync(layer.Id, relatedQuery, cancellationToken);

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
                    string s when long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
                    System.Text.Json.JsonElement je when je.ValueKind == System.Text.Json.JsonValueKind.Number => je.GetInt64(),
                    _ => (long?)null
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

                var relatedGeometry = ODataGeometryConverter.ConvertWkbToGeometry(
                    _geometryService,
                    feature.Geometry,
                    relatedLayer.SpatialReference.ToSrid(),
                    relatedAxisOrder);

                var relatedAttributes = ODataAttributeSerializer.Serialize(feature.Attributes);
                var relatedFeatureDict = ODataUtilityService.BuildFeaturePayload(
                    relatedLayer.Id,
                    feature,
                    relatedGeometry,
                    relatedAttributes);

                var outputName = relationshipNames.Contains(relationship.Name) ? relationship.Name : sanitizedName;
                if (relationsDict.TryGetValue(outputName, out var existingRelations))
                {
                    var newRelations = new object?[existingRelations.Length + 1];
                    Array.Copy(existingRelations, newRelations, existingRelations.Length);
                    newRelations[existingRelations.Length] = relatedFeatureDict;
                    relationsDict[outputName] = newRelations;
                }
                else
                {
                    relationsDict[outputName] = new object?[] { relatedFeatureDict };
                }
            }
        }

        return result;
    }

    private static HashSet<string> ParseExpandRelationshipNames(string expand)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(expand))
        {
            return names;
        }

        var segments = SplitTopLevel(expand);
        foreach (var segment in segments)
        {
            var trimmed = segment.Trim();
            if (string.IsNullOrWhiteSpace(trimmed))
            {
                continue;
            }

            var slashIndex = trimmed.IndexOf('/');
            if (slashIndex >= 0)
            {
                trimmed = trimmed[..slashIndex];
            }

            var optionIndex = trimmed.IndexOf('(');
            if (optionIndex >= 0)
            {
                trimmed = trimmed[..optionIndex];
            }

            trimmed = trimmed.Trim();
            if (!string.IsNullOrWhiteSpace(trimmed))
            {
                names.Add(trimmed);
            }
        }

        return names;
    }

    private static List<string> SplitTopLevel(string expression)
    {
        var result = new List<string>();
        var depth = 0;
        var start = 0;

        for (var i = 0; i < expression.Length; i++)
        {
            var c = expression[i];
            if (c == '(')
            {
                depth++;
            }
            else if (c == ')')
            {
                depth = Math.Max(0, depth - 1);
            }
            else if (c == ',' && depth == 0)
            {
                result.Add(expression[start..i]);
                start = i + 1;
            }
        }

        if (start < expression.Length)
        {
            result.Add(expression[start..]);
        }

        return result;
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
