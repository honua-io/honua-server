// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Shared.Models;
using Honua.Core.Queries.Filters;
using Honua.Infrastructure.Helpers;
using Honua.Protocols.OData.Models;

namespace Honua.Protocols.OData.Services;

/// <summary>
/// Handles OData v4 $apply aggregation operations.
/// Supports aggregate, groupby, filter, and compute transformations.
/// </summary>
internal sealed class ODataAggregationHandler
{
    private readonly IFeatureReader _featureReader;
    private readonly IStreamingFeatureStore _streamingFeatureStore;
    private readonly ODataQueryService _queryService;

    /// <summary>
    /// Initializes a new instance of the <see cref="ODataAggregationHandler"/> class.
    /// </summary>
    public ODataAggregationHandler(
        IFeatureReader featureReader,
        IStreamingFeatureStore streamingFeatureStore,
        ODataQueryService queryService)
    {
        _featureReader = featureReader;
        _streamingFeatureStore = streamingFeatureStore;
        _queryService = queryService;
    }

    /// <summary>
    /// Processes an aggregation query using $apply.
    /// </summary>
    public async Task<ODataAggregationResult> ProcessAggregationAsync(
        int layerId,
        MetadataV2Resource resource,
        string applyExpression,
        string? filter,
        string baseUrl,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(resource);

        // $apply is a transformation pipeline: segments separated by a top-level '/' are applied
        // left to right (e.g. filter(...)/groupby(...)). Leading filter(...) transforms refine the
        // underlying query before the terminal aggregate/groupby/compute runs. Previously only a
        // single transform was parsed, so on a pipeline the filter() regex greedily swallowed the
        // remaining segments and produced a broken filter — 400ing extent-scoped aggregation such
        // as filter(geo.intersects(...))/groupby(...) (#1636).
        var segments = SplitApplyTransforms(applyExpression);

        // Build the query, starting from the $filter query option (composes with $apply).
        var query = new FeatureQuery();
        query = ApplyODataFilter(query, filter, resource);

        // The terminal (last) transform decides whether the result is an aggregation or features.
        var terminal = ParseApplyExpression(segments[^1]);
        foreach (var parsed in (segments.Select(ParseApplyExpression)).Where(parsed => parsed.Type == AggregationType.Filter && !string.IsNullOrWhiteSpace(parsed.FilterExpression)))
        {
            // filter() narrows rows before aggregation; apply every filter segment to the query
            // (a trailing filter-only pipeline then simply returns the filtered features).
            query = ApplyODataFilter(query, parsed.FilterExpression, resource);
        }

        object[] aggregatedValues;
        if (terminal.Type is AggregationType.Aggregate or AggregationType.GroupBy)
        {
            var stream = _streamingFeatureStore.StreamFeaturesAsync(layerId, query, cancellationToken);
            aggregatedValues = await ApplyAggregationStreamingAsync(stream, terminal);
        }
        else
        {
            // Fallback to in-memory aggregation for non-aggregate/groupby operations
            var result = await _featureReader.QueryAsync(layerId, query, cancellationToken);
            aggregatedValues = ApplyAggregation(result.Items, terminal);
        }

        return new ODataAggregationResult
        {
            Context = $"{baseUrl}/odata/$metadata#Features",
            Value = aggregatedValues
        };
    }

    /// <summary>
    /// Splits an OData <c>$apply</c> expression into its pipeline transformation segments on each
    /// top-level <c>/</c> separator. Slashes inside parentheses or single-quoted literals (e.g. a
    /// <c>geography'…'</c> WKT value) are preserved, so a segment is never split mid-expression.
    /// </summary>
    /// <param name="applyExpression">The raw <c>$apply</c> expression.</param>
    /// <returns>The trimmed, non-empty pipeline segments in evaluation order (always at least one).</returns>
    public static IReadOnlyList<string> SplitApplyTransforms(string applyExpression)
    {
        ArgumentNullException.ThrowIfNull(applyExpression);

        var segments = new List<string>();
        var depth = 0;
        var inQuote = false;
        var start = 0;
        for (var i = 0; i < applyExpression.Length; i++)
        {
            var c = applyExpression[i];
            if (c == '\'')
            {
                inQuote = !inQuote;
            }
            else if (!inQuote && c == '(')
            {
                depth++;
            }
            else if (!inQuote && c == ')')
            {
                if (depth > 0)
                {
                    depth--;
                }
            }
            else if (!inQuote && c == '/' && depth == 0)
            {
                segments.Add(applyExpression[start..i]);
                start = i + 1;
            }
        }

        segments.Add(applyExpression[start..]);

        var trimmed = segments
            .Select(s => s.Trim())
            .Where(s => s.Length > 0)
            .ToList();

        // A blank or all-slash expression still yields one (empty-after-trim) segment for the
        // parser to reject with a clear "Unsupported $apply expression" rather than crashing here.
        return trimmed.Count > 0 ? trimmed : new List<string> { applyExpression.Trim() };
    }

    /// <summary>
    /// Parses an OData $apply expression.
    /// Supports: aggregate, groupby, filter, compute
    /// </summary>
    public static ParsedAggregation ParseApplyExpression(string expression)
    {
        var trimmed = expression.Trim();

        // Handle aggregate(...) pattern
        // Example: aggregate(population with sum as TotalPop, area with avg as AvgArea)
        var aggregateMatch = Regex.Match(trimmed, @"^aggregate\((.+)\)$", RegexOptions.IgnoreCase);
        if (aggregateMatch.Success)
        {
            var aggregates = ParseAggregateExpressions(aggregateMatch.Groups[1].Value);
            return new ParsedAggregation
            {
                Type = AggregationType.Aggregate,
                Aggregates = aggregates.ToImmutableArray()
            };
        }

        // Handle groupby((field1, field2), aggregate(...)) pattern
        // Example: groupby((state, county), aggregate(population with sum as TotalPop))
        var groupbyMatch = Regex.Match(trimmed, @"^groupby\s*\(\s*\(([^)]+)\)\s*(?:,\s*aggregate\((.+)\))?\s*\)$", RegexOptions.IgnoreCase);
        if (groupbyMatch.Success)
        {
            var rawGroupByFields = groupbyMatch.Groups[1].Value;
            if (ODataParsingUtilities.HasEmptyCommaSeparatedToken(rawGroupByFields))
            {
                throw new ArgumentException("groupby contains an empty field expression.");
            }

            var groupByFields = rawGroupByFields
                .Split(',', StringSplitOptions.TrimEntries)
                .ToImmutableArray();

            ImmutableArray<AggregateExpression>? aggregates = null;
            if (groupbyMatch.Groups[2].Success && !string.IsNullOrWhiteSpace(groupbyMatch.Groups[2].Value))
            {
                aggregates = ParseAggregateExpressions(groupbyMatch.Groups[2].Value).ToImmutableArray();
            }

            return new ParsedAggregation
            {
                Type = AggregationType.GroupBy,
                GroupByFields = groupByFields,
                Aggregates = aggregates
            };
        }

        // Handle filter(...) pattern
        // Example: filter(population gt 1000)
        var filterMatch = Regex.Match(trimmed, @"^filter\((.+)\)$", RegexOptions.IgnoreCase);
        if (filterMatch.Success)
        {
            return new ParsedAggregation
            {
                Type = AggregationType.Filter,
                FilterExpression = filterMatch.Groups[1].Value
            };
        }

        // Handle compute(...) pattern
        // Example: compute(price mul qty as total)
        var computeMatch = Regex.Match(trimmed, @"^compute\((.+)\)$", RegexOptions.IgnoreCase);
        if (computeMatch.Success)
        {
            return new ParsedAggregation
            {
                Type = AggregationType.Compute,
                FilterExpression = computeMatch.Groups[1].Value // Store the compute expression
            };
        }

        throw new ArgumentException($"Unsupported $apply expression: {expression}");
    }

    private static List<AggregateExpression> ParseAggregateExpressions(string aggregatesString)
    {
        var aggregates = new List<AggregateExpression>();

        // Split by comma, but be careful about nested parentheses
        var parts = SplitAggregateExpressions(aggregatesString);

        foreach (var part in parts)
        {
            var trimmed = part.Trim();
            if (trimmed.Length == 0)
            {
                throw new ArgumentException("aggregate contains an empty expression segment.");
            }

            // Pattern: field with function as alias
            // Example: population with sum as TotalPop
            var withAsMatch = Regex.Match(trimmed, @"^(\w+)\s+with\s+(\w+)\s+as\s+(\w+)$", RegexOptions.IgnoreCase);
            if (withAsMatch.Success)
            {
                aggregates.Add(new AggregateExpression
                {
                    Field = withAsMatch.Groups[1].Value,
                    Function = withAsMatch.Groups[2].Value.ToLowerInvariant(),
                    Alias = withAsMatch.Groups[3].Value
                });
                continue;
            }

            // Pattern: $count as alias
            // Example: $count as TotalCount
            var countMatch = Regex.Match(trimmed, @"^\$count\s+as\s+(\w+)$", RegexOptions.IgnoreCase);
            if (countMatch.Success)
            {
                aggregates.Add(new AggregateExpression
                {
                    Field = "*",
                    Function = "count",
                    Alias = countMatch.Groups[1].Value
                });
                continue;
            }

            throw new ArgumentException($"Unsupported aggregate expression: {part}");
        }

        return aggregates;
    }

    private static List<string> SplitAggregateExpressions(string input)
    {
        var result = new List<string>();
        var current = new System.Text.StringBuilder();
        var depth = 0;
        var trimmedInput = input.Trim();

        foreach (var c in input)
        {
            if (c == '(')
            {
                depth++;
                current.Append(c);
            }
            else if (c == ')')
            {
                depth--;
                current.Append(c);
            }
            else if (c == ',' && depth == 0)
            {
                result.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }

        if (current.Length > 0)
        {
            result.Add(current.ToString());
        }

        if (trimmedInput.EndsWith(','))
        {
            result.Add(string.Empty);
        }

        return result;
    }

    private static object[] ApplyAggregation(IEnumerable<Feature> features, ParsedAggregation aggregation)
    {
        var featureList = features.ToList();

        switch (aggregation.Type)
        {
            case AggregationType.Aggregate:
                return ApplySimpleAggregation(featureList, aggregation.Aggregates ?? ImmutableArray<AggregateExpression>.Empty);

            case AggregationType.GroupBy:
                return ApplyGroupByAggregation(
                    featureList,
                    aggregation.GroupByFields ?? ImmutableArray<string>.Empty,
                    aggregation.Aggregates ?? ImmutableArray<AggregateExpression>.Empty);

            case AggregationType.Filter:
                // Filter is handled at query level, just return features
                return featureList.Select(f => FeatureToDictionary(f)).ToArray();

            case AggregationType.Compute:
                return ApplyCompute(featureList, aggregation.FilterExpression ?? "");

            default:
                throw new ArgumentException($"Unsupported aggregation type: {aggregation.Type}");
        }
    }

    private static async Task<object[]> ApplyAggregationStreamingAsync(
        IAsyncEnumerable<Feature> features,
        ParsedAggregation aggregation)
    {
        return aggregation.Type switch
        {
            AggregationType.Aggregate => await ApplySimpleAggregationStreamingAsync(
                features,
                aggregation.Aggregates ?? ImmutableArray<AggregateExpression>.Empty),
            AggregationType.GroupBy => await ApplyGroupByAggregationStreamingAsync(
                features,
                aggregation.GroupByFields ?? ImmutableArray<string>.Empty,
                aggregation.Aggregates ?? ImmutableArray<AggregateExpression>.Empty),
            _ => throw new ArgumentException($"Unsupported aggregation type: {aggregation.Type}")
        };
    }

    private static async Task<object[]> ApplySimpleAggregationStreamingAsync(
        IAsyncEnumerable<Feature> features,
        ImmutableArray<AggregateExpression> aggregates)
    {
        var accumulators = CreateAccumulators(aggregates);

        await foreach (var feature in features)
        {
            UpdateAccumulators(accumulators, feature);
        }

        var result = new Dictionary<string, object?>();
        foreach (var accumulator in accumulators)
        {
            result[accumulator.Alias] = accumulator.GetResult();
        }

        return new object[] { result };
    }

    private static async Task<object[]> ApplyGroupByAggregationStreamingAsync(
        IAsyncEnumerable<Feature> features,
        ImmutableArray<string> groupByFields,
        ImmutableArray<AggregateExpression> aggregates)
    {
        var groups = new Dictionary<string, GroupAggregationState>();

        await foreach (var feature in features)
        {
            var key = GetGroupKey(feature, groupByFields);
            if (!groups.TryGetValue(key, out var state))
            {
                state = new GroupAggregationState(groupByFields, aggregates, feature);
                groups[key] = state;
            }

            state.Update(feature);
        }

        var results = new List<object>(groups.Count);
        foreach (var state in groups.Values)
        {
            results.Add(state.ToResult());
        }

        return results.ToArray();
    }

    private static object[] ApplySimpleAggregation(List<Feature> features, ImmutableArray<AggregateExpression> aggregates)
    {
        var result = new Dictionary<string, object?>();

        foreach (var agg in aggregates)
        {
            var values = GetFieldValues(features, agg.Field);
            result[agg.Alias] = CalculateAggregate(values, agg.Function);
        }

        return new object[] { result };
    }

    private static object[] ApplyGroupByAggregation(
        List<Feature> features,
        ImmutableArray<string> groupByFields,
        ImmutableArray<AggregateExpression> aggregates)
    {
        var groups = features.GroupBy(f => GetGroupKey(f, groupByFields));
        var results = new List<object>();

        foreach (var group in groups)
        {
            var result = new Dictionary<string, object?>();

            // Add group key values
            var firstFeature = group.First();
            foreach (var field in groupByFields)
            {
                result[field] = GetFieldValue(firstFeature, field);
            }

            // Add aggregates
            var groupFeatures = group.ToList();
            foreach (var agg in aggregates)
            {
                var values = GetFieldValues(groupFeatures, agg.Field);
                result[agg.Alias] = CalculateAggregate(values, agg.Function);
            }

            results.Add(result);
        }

        return results.ToArray();
    }

    private static List<AggregateAccumulator> CreateAccumulators(ImmutableArray<AggregateExpression> aggregates)
    {
        var accumulators = new List<AggregateAccumulator>(aggregates.Length);
        foreach (var aggregate in aggregates)
        {
            accumulators.Add(new AggregateAccumulator(aggregate));
        }

        return accumulators;
    }

    private static void UpdateAccumulators(List<AggregateAccumulator> accumulators, Feature feature)
    {
        foreach (var candidate in accumulators
                     .Select(accumulator => (
                         Accumulator: accumulator,
                         Value: TryGetNumericValue(feature, accumulator.Field, out var value)
                             ? (double?)value
                             : null))
                     .Where(candidate => candidate.Value.HasValue))
        {
            candidate.Accumulator.AddValue(candidate.Value.GetValueOrDefault());
        }
    }

    private static object[] ApplyCompute(List<Feature> features, string computeExpression)
    {
        // Delegate to the shared compute grammar so $apply=compute(...) and the $compute
        // query option behave identically (including floor()/ceiling()/round() and the
        // mod operator used for histogram binning). On an unparseable expression we fall
        // back to returning the raw features, preserving the previous lenient behavior.
        if (!ODataComputeService.TryParse(computeExpression, out var expressions, out _) || expressions.IsDefaultOrEmpty)
        {
            return features.Select(f => (object)FeatureToDictionary(f)).ToArray();
        }

        return features.Select(f =>
        {
            var dict = FeatureToDictionary(f);
            ODataComputeService.ApplyCompute(dict, expressions);
            return (object)dict;
        }).ToArray();
    }

    private static string GetGroupKey(Feature feature, ImmutableArray<string> fields)
    {
        var builder = new StringBuilder(fields.Length * 16);
        foreach (var field in fields)
        {
            AppendKeyPart(builder, GetFieldValue(feature, field));
        }

        return builder.ToString();
    }

    private static object? GetFieldValue(Feature feature, string field)
    {
        if (field.Equals(FieldNames.ObjectId, StringComparison.OrdinalIgnoreCase))
        {
            return feature.Id;
        }

        return feature.Attributes.TryGetValue(field, out var value) ? value : null;
    }

    private static bool TryGetNumericValue(Feature feature, string field, out double value)
    {
        if (field == "*")
        {
            value = 1d;
            return true;
        }

        var fieldValue = GetFieldValue(feature, field);
        if (fieldValue == null)
        {
            value = 0d;
            return false;
        }

        value = GetNumericValue(fieldValue);
        return true;
    }

    private static List<double> GetFieldValues(List<Feature> features, string field)
    {
        var values = new List<double>();

        if (field == "*")
        {
            values.AddRange(Enumerable.Repeat(1d, features.Count));
            return values;
        }

        values.AddRange(features
            .Select(f => GetFieldValue(f, field))
            .Where(value => value != null)
            .Select(GetNumericValue));

        return values;
    }

    private static double GetNumericValue(object? value)
    {
        return value switch
        {
            int i => i,
            long l => l,
            float f => f,
            double d => d,
            decimal dec => (double)dec,
            string s when double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) => parsed,
            System.Text.Json.JsonElement je when je.ValueKind == System.Text.Json.JsonValueKind.Number => je.GetDouble(),
            _ => 0
        };
    }

    private static void AppendKeyPart(StringBuilder builder, object? value)
    {
        if (value == null)
        {
            AppendKey(builder, "null", "");
            return;
        }

        switch (value)
        {
            case string s:
                AppendKey(builder, "s", s);
                return;
            case int i:
                AppendKey(builder, "i", i.ToString(CultureInfo.InvariantCulture));
                return;
            case long l:
                AppendKey(builder, "l", l.ToString(CultureInfo.InvariantCulture));
                return;
            case float f:
                AppendKey(builder, "f", f.ToString("R", CultureInfo.InvariantCulture));
                return;
            case double d:
                AppendKey(builder, "d", d.ToString("R", CultureInfo.InvariantCulture));
                return;
            case decimal dec:
                AppendKey(builder, "m", dec.ToString(CultureInfo.InvariantCulture));
                return;
            case bool b:
                AppendKey(builder, "b", b ? "1" : "0");
                return;
            case DateTime dt:
                AppendKey(builder, "dt", dt.ToString("O", CultureInfo.InvariantCulture));
                return;
            case DateTimeOffset dto:
                AppendKey(builder, "dto", dto.ToString("O", CultureInfo.InvariantCulture));
                return;
            case System.Text.Json.JsonElement je:
                AppendKey(builder, "j", je.GetRawText());
                return;
            default:
                AppendKey(builder, "o", value.ToString() ?? "");
                return;
        }
    }

    private static void AppendKey(StringBuilder builder, string type, string value)
    {
        builder.Append(type);
        builder.Append(':');
        builder.Append(value.Length.ToString(CultureInfo.InvariantCulture));
        builder.Append(':');
        builder.Append(value);
        builder.Append('|');
    }

    private static object? CalculateAggregate(List<double> values, string function)
    {
        if (values.Count == 0)
        {
            return function == "count" ? 0 : null;
        }

        return function switch
        {
            "sum" => values.Sum(),
            "avg" or "average" => values.Average(),
            "min" => values.Min(),
            "max" => values.Max(),
            "count" => values.Count,
            "countdistinct" => values.Distinct().Count(),
            _ => throw new ArgumentException($"Unsupported aggregate function: {function}")
        };
    }

    private sealed class AggregateAccumulator
    {
        public AggregateAccumulator(AggregateExpression expression)
        {
            Field = expression.Field;
            Alias = expression.Alias;
            Function = expression.Function.ToLowerInvariant();
        }

        public string Field { get; }
        public string Alias { get; }
        public string Function { get; }

        private double _sum;
        private long _count;
        private double? _min;
        private double? _max;
        private HashSet<double>? _distinct;

        public void AddValue(double value)
        {
            switch (Function)
            {
                case "sum":
                    _sum += value;
                    _count++;
                    break;
                case "avg":
                case "average":
                    _sum += value;
                    _count++;
                    break;
                case "min":
                    _min = _min.HasValue ? Math.Min(_min.Value, value) : value;
                    _count++;
                    break;
                case "max":
                    _max = _max.HasValue ? Math.Max(_max.Value, value) : value;
                    _count++;
                    break;
                case "count":
                    _count++;
                    break;
                case "countdistinct":
                    _distinct ??= new HashSet<double>();
                    _distinct.Add(value);
                    break;
                default:
                    throw new ArgumentException($"Unsupported aggregate function: {Function}");
            }
        }

        [SuppressMessage("Performance", "CA1859:Use concrete types when possible")]
        public object? GetResult()
        {
            return Function switch
            {
                "sum" => _count == 0 ? null : _sum,
                "avg" or "average" => _count == 0 ? null : _sum / _count,
                "min" => _count == 0 ? null : _min,
                "max" => _count == 0 ? null : _max,
                "count" => _count,
                "countdistinct" => _distinct?.Count ?? 0,
                _ => throw new ArgumentException($"Unsupported aggregate function: {Function}")
            };
        }
    }

    private sealed class GroupAggregationState
    {
        private readonly Dictionary<string, object?> _values;
        private readonly List<AggregateAccumulator> _accumulators;

        public GroupAggregationState(
            ImmutableArray<string> groupByFields,
            ImmutableArray<AggregateExpression> aggregates,
            Feature firstFeature)
        {
            _values = new Dictionary<string, object?>();
            foreach (var field in groupByFields)
            {
                _values[field] = GetFieldValue(firstFeature, field);
            }

            _accumulators = CreateAccumulators(aggregates);
        }

        public void Update(Feature feature) => UpdateAccumulators(_accumulators, feature);

        public Dictionary<string, object?> ToResult()
        {
            foreach (var accumulator in _accumulators)
            {
                _values[accumulator.Alias] = accumulator.GetResult();
            }

            return _values;
        }
    }

    private static Dictionary<string, object?> FeatureToDictionary(Feature feature)
    {
        var dict = new Dictionary<string, object?>
        {
            ["ObjectId"] = feature.Id
        };

        foreach (var kvp in feature.Attributes)
        {
            if (FeatureAttributeVisibility.IsInternalAttribute(kvp.Key))
            {
                continue;
            }

            dict[kvp.Key] = kvp.Value;
        }

        return dict;
    }

    private FeatureQuery ApplyODataFilter(FeatureQuery query, string? filterExpression, MetadataV2Resource resource)
    {
        if (string.IsNullOrWhiteSpace(filterExpression))
        {
            return query;
        }

        var sqlFragment = _queryService.ConvertODataFilterToSqlFragment(filterExpression, resource);
        return ODataSqlFragmentMergeHelper.Merge(query, sqlFragment);
    }
}
