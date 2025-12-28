// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Text.RegularExpressions;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Server.Features.OData.Models;

namespace Honua.Server.Features.OData.Services;

/// <summary>
/// Handles OData v4 $apply aggregation operations.
/// Supports aggregate, groupby, filter, and compute transformations.
/// </summary>
internal sealed class ODataAggregationHandler
{
    private readonly IFeatureStore _featureStore;

    /// <summary>
    /// Initializes a new instance of the <see cref="ODataAggregationHandler"/> class.
    /// </summary>
    public ODataAggregationHandler(IFeatureStore featureStore)
    {
        _featureStore = featureStore;
    }

    /// <summary>
    /// Processes an aggregation query using $apply.
    /// </summary>
    public async Task<ODataAggregationResult> ProcessAggregationAsync(
        int layerId,
        string applyExpression,
        string? filter,
        string baseUrl,
        CancellationToken cancellationToken)
    {
        // Parse the $apply expression
        var aggregation = ParseApplyExpression(applyExpression);

        // Build the query
        var query = new FeatureQuery();

        if (!string.IsNullOrWhiteSpace(filter))
        {
            query = query with { Where = filter };
        }

        // Get all features (we'll aggregate in memory for now)
        // TODO: Push aggregation to database for better performance
        var result = await _featureStore.QueryAsync(layerId, query, cancellationToken);

        // Apply aggregation
        var aggregatedValues = ApplyAggregation(result.Items, aggregation);

        return new ODataAggregationResult
        {
            Context = $"{baseUrl}/odata/$metadata#Features",
            Value = aggregatedValues
        };
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
            var groupByFields = groupbyMatch.Groups[1].Value
                .Split(',')
                .Select(f => f.Trim())
                .Where(f => !string.IsNullOrEmpty(f))
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

        return result;
    }

    private object[] ApplyAggregation(IEnumerable<Feature> features, ParsedAggregation aggregation)
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

    private static object[] ApplyCompute(List<Feature> features, string computeExpression)
    {
        // Parse compute expression: field mul/add/sub/div value as alias
        var computeMatch = Regex.Match(computeExpression.Trim(), @"^(\w+)\s+(mul|add|sub|div)\s+(\w+)\s+as\s+(\w+)$", RegexOptions.IgnoreCase);

        if (!computeMatch.Success)
        {
            return features.Select(f => FeatureToDictionary(f)).ToArray();
        }

        var field1 = computeMatch.Groups[1].Value;
        var operation = computeMatch.Groups[2].Value.ToLowerInvariant();
        var field2 = computeMatch.Groups[3].Value;
        var alias = computeMatch.Groups[4].Value;

        return features.Select(f =>
        {
            var dict = FeatureToDictionary(f);

            var value1 = GetNumericValue(GetFieldValue(f, field1));
            var value2 = double.TryParse(field2, out var constVal) ? constVal : GetNumericValue(GetFieldValue(f, field2));

            dict[alias] = operation switch
            {
                "mul" => value1 * value2,
                "add" => value1 + value2,
                "sub" => value1 - value2,
                "div" => value2 != 0 ? value1 / value2 : null,
                _ => null
            };

            return dict;
        }).ToArray();
    }

    private static string GetGroupKey(Feature feature, ImmutableArray<string> fields)
    {
        return string.Join("|", fields.Select(f => GetFieldValue(feature, f)?.ToString() ?? "null"));
    }

    private static object? GetFieldValue(Feature feature, string field)
    {
        if (field.Equals("objectid", StringComparison.OrdinalIgnoreCase))
        {
            return feature.Id;
        }

        return feature.Attributes.TryGetValue(field, out var value) ? value : null;
    }

    private static List<double> GetFieldValues(List<Feature> features, string field)
    {
        var values = new List<double>();

        foreach (var feature in features)
        {
            var value = GetFieldValue(feature, field);
            if (value != null)
            {
                values.Add(GetNumericValue(value));
            }
        }

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
            string s when double.TryParse(s, out var parsed) => parsed,
            System.Text.Json.JsonElement je when je.ValueKind == System.Text.Json.JsonValueKind.Number => je.GetDouble(),
            _ => 0
        };
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

    private static Dictionary<string, object?> FeatureToDictionary(Feature feature)
    {
        var dict = new Dictionary<string, object?>
        {
            ["ObjectId"] = feature.Id
        };

        foreach (var kvp in feature.Attributes)
        {
            dict[kvp.Key] = kvp.Value;
        }

        return dict;
    }
}
