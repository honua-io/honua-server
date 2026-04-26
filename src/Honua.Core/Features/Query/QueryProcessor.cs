// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Shared.Models;
using Honua.Core.Queries.Filters;
using Microsoft.Extensions.Logging;

namespace Honua.Core.Features.Query;

/// <summary>
/// Default implementation of unified query processor with optimization and validation.
/// </summary>
public sealed class QueryProcessor : IQueryProcessor
{
    private readonly IFilterExpressionTranslator _filterTranslator;
    private readonly IFeatureReader _featureReader;
    private readonly ILogger<QueryProcessor> _logger;

    private const int StreamingThreshold = 200;
    private const int MaxReasonableLimit = 50000;
    private const int MaxReasonableOffset = 1000000;

    public QueryProcessor(
        IFilterExpressionTranslator filterTranslator,
        IFeatureReader featureReader,
        ILogger<QueryProcessor> logger)
    {
        _filterTranslator = filterTranslator ?? throw new ArgumentNullException(nameof(filterTranslator));
        _featureReader = featureReader ?? throw new ArgumentNullException(nameof(featureReader));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public QueryValidationResult ValidateQuery(UnifiedQuery query, LayerDefinition layer)
    {
        var warnings = new List<string>();

        // Validate pagination
        if (query.Offset.HasValue && query.Offset.Value < 0)
        {
            return QueryValidationResult.Failure("Offset cannot be negative.");
        }

        if (query.Limit.HasValue && query.Limit.Value <= 0)
        {
            return QueryValidationResult.Failure("Limit must be greater than zero.");
        }

        if (query.Offset.HasValue && query.Offset.Value > MaxReasonableOffset)
        {
            return QueryValidationResult.Failure($"Offset too large (maximum: {MaxReasonableOffset}).");
        }

        if (query.Limit.HasValue && query.Limit.Value > MaxReasonableLimit)
        {
            warnings.Add($"Large limit requested ({query.Limit.Value}). Consider pagination.");
        }

        // Validate field selection
        if (query.OutFields?.IsDefaultOrEmpty == false)
        {
            var availableFields = layer.AttributeFields
                .ToDictionary(f => f.Name, StringComparer.OrdinalIgnoreCase);

            var invalidFields = query.OutFields.Value
                .Where(field => !availableFields.ContainsKey(field))
                .ToList();

            if (invalidFields.Count > 0)
            {
                return QueryValidationResult.Failure(
                    $"Unknown fields: {FormatFieldNamesForError(invalidFields)}");
            }
        }

        // Validate ordering fields
        if (query.OrderBy?.IsDefaultOrEmpty == false)
        {
            var availableFields = layer.AttributeFields
                .ToDictionary(f => f.Name, StringComparer.OrdinalIgnoreCase);

            var invalidOrderFields = query.OrderBy.Value
                .Where(order => !availableFields.ContainsKey(order.Field) &&
                               !IsSystemField(order.Field))
                .Select(order => order.Field)
                .ToList();

            if (invalidOrderFields.Count > 0)
            {
                return QueryValidationResult.Failure(
                    $"Unknown order by fields: {FormatFieldNamesForError(invalidOrderFields)}");
            }
        }

        // Validate spatial filter
        if (query.SpatialFilter.HasValue)
        {
            if (layer.GeometryType == GeometryType.None)
            {
                return QueryValidationResult.Failure("Spatial queries not supported on non-spatial layers.");
            }

            var spatialFilter = query.SpatialFilter.Value;
            if (spatialFilter.Distance.HasValue && spatialFilter.Distance.Value < 0)
            {
                return QueryValidationResult.Failure("Spatial filter distance cannot be negative.");
            }

            if (spatialFilter.NearestCount.HasValue && spatialFilter.NearestCount.Value <= 0)
            {
                return QueryValidationResult.Failure("Nearest neighbor count must be positive.");
            }
        }

        // Validate aggregation
        if (query.RequiresAggregation)
        {
            var aggregation = query.Aggregation!.Value;

            if (aggregation.GroupByFields?.IsDefaultOrEmpty == false)
            {
                var availableFields = layer.AttributeFields
                    .ToDictionary(f => f.Name, StringComparer.OrdinalIgnoreCase);

                var invalidGroupFields = aggregation.GroupByFields.Value
                    .Where(field => !availableFields.ContainsKey(field))
                    .ToList();

                if (invalidGroupFields.Count > 0)
                {
                    return QueryValidationResult.Failure(
                        $"Unknown group by fields: {FormatFieldNamesForError(invalidGroupFields)}");
                }
            }

            if (aggregation.Statistics?.IsDefaultOrEmpty == false)
            {
                var availableFields = layer.AttributeFields
                    .ToDictionary(f => f.Name, StringComparer.OrdinalIgnoreCase);

                foreach (var stat in aggregation.Statistics.Value)
                {
                    if (!string.IsNullOrEmpty(stat.OnStatisticField) &&
                        !availableFields.ContainsKey(stat.OnStatisticField))
                    {
                        return QueryValidationResult.Failure(
                            $"Unknown statistics field: {FormatFieldNameForError(stat.OnStatisticField)}");
                    }
                }
            }
        }

        return QueryValidationResult.Success(warnings.Count > 0 ? warnings : null);
    }

    private static string FormatFieldNamesForError(IEnumerable<string> fieldNames)
        => string.Join(", ", fieldNames.Select(FormatFieldNameForError));

    private static string FormatFieldNameForError(string fieldName)
    {
        if (string.IsNullOrWhiteSpace(fieldName))
        {
            return "unnamed_field";
        }

        return IsSafeFieldIdentifier(fieldName)
            ? fieldName
            : fieldName.SanitizeFieldName();
    }

    private static bool IsSafeFieldIdentifier(string fieldName)
    {
        if (fieldName.Length > 63 || fieldName[0] is >= '0' and <= '9')
        {
            return false;
        }

        foreach (var character in fieldName)
        {
            var isAsciiLetter =
                (character is >= 'a' and <= 'z') ||
                (character is >= 'A' and <= 'Z');
            var isAsciiDigit = character is >= '0' and <= '9';

            if (!isAsciiLetter && !isAsciiDigit && character != '_')
            {
                return false;
            }
        }

        return true;
    }

    /// <inheritdoc />
    public UnifiedQuery OptimizeQuery(UnifiedQuery query, LayerDefinition layer)
    {
        // Apply default ordering if none specified
        var orderBy = query.OrderBy;
        if (orderBy?.IsDefaultOrEmpty != false)
        {
            orderBy = ImmutableArray.Create(new OrderByClause(
                FieldNames.ObjectId,
                ascending: true,
                FieldType.BigInteger));
        }

        // Optimize field selection
        var outFields = query.OutFields;
        if (outFields?.IsDefaultOrEmpty == false)
        {
            // Remove duplicate fields (case-insensitive)
            var uniqueFields = outFields.Value
                .GroupBy(field => field, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToImmutableArray();

            if (uniqueFields.Length != outFields.Value.Length)
            {
                outFields = uniqueFields;
            }
        }

        // Optimize limits
        var limit = query.Limit;
        if (limit.HasValue && limit.Value > MaxReasonableLimit)
        {
            QueryLog.LargeLimitOptimized(_logger, limit.Value, MaxReasonableLimit);
            limit = MaxReasonableLimit;
        }

        // Apply optimizations if any changes were made
        if (!Nullable.Equals(orderBy, query.OrderBy) ||
            !Nullable.Equals(outFields, query.OutFields) ||
            limit != query.Limit)
        {
            return query with
            {
                OrderBy = orderBy,
                OutFields = outFields,
                Limit = limit
            };
        }

        return query;
    }

    /// <inheritdoc />
    public FeatureQuery ToFeatureQuery(UnifiedQuery query, LayerDefinition layer)
    {
        // Convert filter
        SqlFragment? sqlFilter = null;
        if (query.Filter?.GetFilterExpression() is { } filterExpression)
        {
            try
            {
                sqlFilter = _filterTranslator.Translate(filterExpression, layer);
            }
            catch (Exception ex)
            {
                QueryLog.FilterTranslationFailed(_logger, ex);
                sqlFilter = query.Filter?.GetSqlFragment();
            }
        }
        else if (query.Filter?.GetSqlFragment() is { } sqlFragment)
        {
            sqlFilter = sqlFragment;
        }

        // Convert output CRS
        var outputSrid = query.OutputCrs?.Srid;
        var outputAxisOrder = query.OutputCrs?.AxisOrder;

        // Convert to FeatureQuery
        var featureQuery = new FeatureQuery
        {
            SqlFilter = sqlFilter,
            ObjectIds = query.ObjectIds,
            OutFields = query.OutFields,
            Offset = query.Offset,
            Limit = query.Limit,
            OrderBy = query.OrderBy,
            SpatialReferenceSrid = layer.SpatialReference.ToSrid(),
            GeometryType = layer.GeometryType,
            PublicIdAttributeName = ResolvePublicIdAttributeName(layer),
            OutputSrid = outputSrid,
            OutputAxisOrder = outputAxisOrder,
            SpatialFilter = query.SpatialFilter,
            TemporalFilter = query.TemporalFilter,
            Distinct = query.Aggregation?.Distinct ?? false
        };

        if (query.Extensions?.TryGetValue("includeNullGeometry", out var includeNullGeometryValue) == true &&
            includeNullGeometryValue is bool includeNullGeometry)
        {
            featureQuery = featureQuery with { IncludeNullGeometry = includeNullGeometry };
        }

        // Handle aggregation
        if (query.RequiresAggregation)
        {
            var aggregation = query.Aggregation!.Value;
            featureQuery = featureQuery with
            {
                OutStatistics = aggregation.Statistics,
                GroupByFields = aggregation.GroupByFields
            };
        }

        return featureQuery;
    }

    private static string? ResolvePublicIdAttributeName(LayerDefinition layer)
    {
        var objectIdFieldName = layer.ObjectIdFieldName;
        return layer.AttributeFields.Any(field => field.Name.Equals(objectIdFieldName, StringComparison.OrdinalIgnoreCase))
            ? objectIdFieldName
            : null;
    }

    /// <inheritdoc />
    public string BuildCacheKey(UnifiedQuery query, LayerDefinition layer, string protocol)
    {
        // Create a deterministic hash of the query for caching
        var keyBuilder = new StringBuilder();
        AppendKeyPart(keyBuilder, nameof(protocol), protocol);
        AppendKeyPart(keyBuilder, nameof(layer.Id), layer.Id);
        AppendKeyPart(keyBuilder, "Filter", query.Filter?.GetFilterExpression()?.ToString() ?? query.Filter?.GetSqlFragment()?.Sql);
        AppendSequence(keyBuilder, "FilterParams", query.Filter?.GetSqlFragment()?.Parameters);
        AppendSpatialFilter(keyBuilder, query.SpatialFilter);
        AppendTemporalFilter(keyBuilder, query.TemporalFilter);
        AppendImmutableArray(keyBuilder, nameof(query.ObjectIds), query.ObjectIds);
        AppendImmutableArray(keyBuilder, nameof(query.OutFields), query.OutFields);
        AppendOrderBy(keyBuilder, query.OrderBy);
        AppendQueryCrs(keyBuilder, query.OutputCrs);
        AppendAggregation(keyBuilder, query.Aggregation);
        AppendHints(keyBuilder, query.Hints);
        AppendDictionary(keyBuilder, nameof(query.Extensions), query.Extensions);
        AppendKeyPart(keyBuilder, nameof(query.Offset), query.Offset);
        AppendKeyPart(keyBuilder, nameof(query.Limit), query.Limit);

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(keyBuilder.ToString()));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static void AppendKeyPart(StringBuilder builder, string name, object? value)
    {
        builder.Append(name);
        builder.Append('=');
        AppendKeyValue(builder, value);
        builder.Append(';');
    }

    private static void AppendSequence<T>(StringBuilder builder, string name, IEnumerable<T>? values)
    {
        builder.Append(name);
        builder.Append("=[");
        if (values != null)
        {
            var first = true;
            foreach (var value in values)
            {
                if (!first)
                {
                    builder.Append(',');
                }

                AppendKeyValue(builder, value);
                first = false;
            }
        }

        builder.Append("];");
    }

    private static void AppendImmutableArray<T>(StringBuilder builder, string name, ImmutableArray<T>? values)
    {
        if (!values.HasValue || values.Value.IsDefault)
        {
            AppendKeyPart(builder, name, null);
            return;
        }

        AppendSequence(builder, name, values.Value);
    }

    private static void AppendSpatialFilter(StringBuilder builder, SpatialFilter? spatialFilter)
    {
        builder.Append("SpatialFilter=");
        if (!spatialFilter.HasValue)
        {
            builder.Append("<null>;");
            return;
        }

        var value = spatialFilter.Value;
        builder.Append(value.SpatialRelationship);
        builder.Append('|');
        builder.Append(value.Srid?.ToString(CultureInfo.InvariantCulture) ?? "<null>");
        builder.Append('|');
        builder.Append(value.Distance?.ToString("R", CultureInfo.InvariantCulture) ?? "<null>");
        builder.Append('|');
        builder.Append(value.DistanceUnit);
        builder.Append('|');
        builder.Append(value.NearestCount?.ToString(CultureInfo.InvariantCulture) ?? "<null>");
        builder.Append('|');
        builder.Append(value.ReturnDistance);
        builder.Append('|');
        builder.Append(value.Geometry == null ? "<null>" : Convert.ToHexString(value.Geometry));
        builder.Append(';');
    }

    private static void AppendTemporalFilter(StringBuilder builder, TemporalFilter? temporalFilter)
    {
        builder.Append("TemporalFilter=");
        if (!temporalFilter.HasValue)
        {
            builder.Append("<null>;");
            return;
        }

        var value = temporalFilter.Value;
        builder.Append(value.PropertyName);
        builder.Append('|');
        builder.Append(value.PropertyType);
        builder.Append('|');
        builder.Append(value.Start?.ToString("O", CultureInfo.InvariantCulture) ?? "<null>");
        builder.Append('|');
        builder.Append(value.End?.ToString("O", CultureInfo.InvariantCulture) ?? "<null>");
        builder.Append(';');
    }

    private static void AppendOrderBy(StringBuilder builder, ImmutableArray<OrderByClause>? orderBy)
    {
        builder.Append("OrderBy=[");
        if (orderBy.HasValue && !orderBy.Value.IsDefault)
        {
            var first = true;
            foreach (var clause in orderBy.Value)
            {
                if (!first)
                {
                    builder.Append(',');
                }

                builder.Append(clause.Field);
                builder.Append(':');
                builder.Append(clause.Ascending);
                builder.Append(':');
                builder.Append(clause.FieldType?.ToString() ?? "<null>");
                first = false;
            }
        }

        builder.Append("];");
    }

    private static void AppendQueryCrs(StringBuilder builder, QueryCrs? outputCrs)
    {
        builder.Append("OutputCrs=");
        if (!outputCrs.HasValue)
        {
            builder.Append("<null>;");
            return;
        }

        var value = outputCrs.Value;
        builder.Append(value.Srid?.ToString(CultureInfo.InvariantCulture) ?? "<null>");
        builder.Append('|');
        builder.Append(value.AxisOrder?.ToString() ?? "<null>");
        builder.Append('|');
        builder.Append(value.Uri ?? "<null>");
        builder.Append(';');
    }

    private static void AppendAggregation(StringBuilder builder, QueryAggregation? aggregation)
    {
        builder.Append("Aggregation=");
        if (!aggregation.HasValue)
        {
            builder.Append("<null>;");
            return;
        }

        var value = aggregation.Value;
        builder.Append("Distinct:");
        builder.Append(value.Distinct);
        builder.Append("|Stats:[");
        if (value.Statistics.HasValue && !value.Statistics.Value.IsDefault)
        {
            var first = true;
            foreach (var statistic in value.Statistics.Value)
            {
                if (!first)
                {
                    builder.Append(',');
                }

                builder.Append(statistic.StatisticType);
                builder.Append(':');
                builder.Append(statistic.OnStatisticField);
                builder.Append(':');
                builder.Append(statistic.OutStatisticFieldName);
                builder.Append(':');
                builder.Append(statistic.FieldType?.ToString() ?? "<null>");
                first = false;
            }
        }

        builder.Append("]|GroupBy:");
        AppendImmutableArray(builder, nameof(value.GroupByFields), value.GroupByFields);
        builder.Append("Having:");
        builder.Append(value.HavingFilter?.GetFilterExpression()?.ToString() ?? value.HavingFilter?.GetSqlFragment()?.Sql ?? "<null>");
        builder.Append(';');
    }

    private static void AppendHints(StringBuilder builder, QueryHints? hints)
    {
        builder.Append("Hints=");
        if (!hints.HasValue)
        {
            builder.Append("<null>;");
            return;
        }

        var value = hints.Value;
        builder.Append("PreferStreaming:");
        AppendKeyValue(builder, value.PreferStreaming);
        builder.Append("|EnableCaching:");
        AppendKeyValue(builder, value.EnableCaching);
        builder.Append("|EstimatedResultSize:");
        AppendKeyValue(builder, value.EstimatedResultSize);
        builder.Append("|RequireExactCount:");
        AppendKeyValue(builder, value.RequireExactCount);
        builder.Append("|TimeoutMs:");
        AppendKeyValue(builder, value.TimeoutMs);
        builder.Append('|');
        AppendDictionary(builder, nameof(value.OptimizationHints), value.OptimizationHints);
    }

    private static void AppendDictionary(StringBuilder builder, string name, IReadOnlyDictionary<string, object>? values)
    {
        builder.Append(name);
        builder.Append("={");
        if (values != null)
        {
            var first = true;
            foreach (var pair in values.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                if (!first)
                {
                    builder.Append(',');
                }

                AppendKeyValue(builder, pair.Key);
                builder.Append(':');
                AppendKeyValue(builder, pair.Value);
                first = false;
            }
        }

        builder.Append("};");
    }

    private static void AppendKeyValue(StringBuilder builder, object? value)
    {
        var formatted = FormatKeyValue(value);
        builder.Append(formatted.Length.ToString(CultureInfo.InvariantCulture));
        builder.Append(':');
        builder.Append(formatted);
    }

    private static string FormatKeyValue(object? value)
    {
        return value switch
        {
            null => "<null>",
            byte[] bytes => Convert.ToHexString(bytes),
            DateTime dateTime => dateTime.ToString("O", CultureInfo.InvariantCulture),
            DateTimeOffset dateTimeOffset => dateTimeOffset.ToString("O", CultureInfo.InvariantCulture),
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture) ?? string.Empty,
            _ => value.ToString() ?? string.Empty
        };
    }

    /// <inheritdoc />
    public bool ShouldUseStreaming(UnifiedQuery query, LayerDefinition layer, string outputFormat)
    {
        // Check if streaming is hinted
        if (query.Hints?.PreferStreaming == true)
        {
            return true;
        }

        // Don't stream for aggregated queries
        if (query.RequiresAggregation)
        {
            return false;
        }

        // Don't stream if exact count is required and would be expensive
        if (query.Hints?.RequireExactCount == true && query.HasFilters)
        {
            return false;
        }

        // Don't stream for small result sets
        var effectiveLimit = query.GetEffectiveLimit(1000);
        if (effectiveLimit <= StreamingThreshold)
        {
            return false;
        }

        // Check if format supports streaming
        var streamableFormats = new[] { "application/geo+json", "application/json", "application/gml+xml" };
        if (!streamableFormats.Any(format =>
            string.Equals(format, outputFormat, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        return true;
    }

    /// <inheritdoc />
    public async Task<long> EstimateResultCountAsync(
        UnifiedQuery query,
        LayerDefinition layer,
        CancellationToken cancellationToken)
    {
        try
        {
            var featureQuery = ToFeatureQuery(query, layer);
            return await _featureReader.CountAsync(layer.Id, featureQuery, cancellationToken);
        }
        catch (Exception ex)
        {
            QueryLog.EstimateResultCountFailed(_logger, layer.Id, ex);

            // Return a conservative estimate based on query characteristics
            if (query.ObjectIds?.IsDefaultOrEmpty == false)
            {
                return query.ObjectIds.Value.Length;
            }

            if (query.Limit.HasValue)
            {
                return Math.Min(query.Limit.Value, 1000);
            }

            return 1000; // Default estimate
        }
    }

    private static bool IsSystemField(string fieldName)
    {
        var systemFields = new[]
        {
            FieldNames.ObjectId,
            "object_id",
            "id",
            "created_at",
            "updated_at",
            "geometry"
        };

        return systemFields.Contains(fieldName, StringComparer.OrdinalIgnoreCase);
    }
}
