// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Text.RegularExpressions;
using Honua.Protocols.OData.Models;

namespace Honua.Protocols.OData.Services;

internal sealed partial class ODataAggregationHandler
{
    private const int MaxApplyExpressionLength = 4096;
    private const int RegexTimeoutMilliseconds = 100;

    private static ParsedAggregation? TryParseAggregateOrGroupBy(string trimmed)
    {
        var aggregateMatch = AggregateExpressionRegex().Match(trimmed);
        if (aggregateMatch.Success)
        {
            return new ParsedAggregation
            {
                Type = AggregationType.Aggregate,
                Aggregates = ParseAggregateExpressions(aggregateMatch.Groups[1].Value).ToImmutableArray()
            };
        }

        var groupbyMatch = GroupByExpressionRegex().Match(trimmed);
        if (!groupbyMatch.Success)
        {
            return null;
        }

        var rawGroupByFields = groupbyMatch.Groups[1].Value;
        if (ODataParsingUtilities.HasEmptyCommaSeparatedToken(rawGroupByFields))
        {
            throw new ArgumentException("groupby contains an empty field expression.");
        }

        var groupByFields = rawGroupByFields.Split(',', StringSplitOptions.TrimEntries).ToImmutableArray();
        ImmutableArray<AggregateExpression>? aggregates = groupbyMatch.Groups[2].Success && !string.IsNullOrWhiteSpace(groupbyMatch.Groups[2].Value)
            ? ParseAggregateExpressions(groupbyMatch.Groups[2].Value).ToImmutableArray()
            : null;
        return new ParsedAggregation
        {
            Type = AggregationType.GroupBy,
            GroupByFields = groupByFields,
            Aggregates = aggregates
        };
    }

    private static ParsedAggregation? TryParseFilterOrCompute(string trimmed)
    {
        var filterMatch = FilterExpressionRegex().Match(trimmed);
        if (filterMatch.Success)
        {
            return new ParsedAggregation
            {
                Type = AggregationType.Filter,
                FilterExpression = filterMatch.Groups[1].Value
            };
        }

        var computeMatch = ComputeExpressionRegex().Match(trimmed);
        if (computeMatch.Success)
        {
            return new ParsedAggregation
            {
                Type = AggregationType.Compute,
                FilterExpression = computeMatch.Groups[1].Value
            };
        }

        return null;
    }

    private static void ValidateNoHiddenFields(ParsedAggregation aggregation, IReadOnlySet<string> hiddenFields)
    {
        if (hiddenFields.Count == 0)
        {
            return;
        }

        if (aggregation.Aggregates.HasValue)
        {
            foreach (var aggregate in aggregation.Aggregates.Value)
            {
                if (aggregate.Field != "*" && hiddenFields.Contains(aggregate.Field))
                {
                    throw new ArgumentException($"Unknown field '{aggregate.Field}' in $apply.");
                }
            }
        }

        if (aggregation.GroupByFields.HasValue)
        {
            var hiddenGroupField = aggregation.GroupByFields.Value.FirstOrDefault(hiddenFields.Contains);
            if (hiddenGroupField is not null)
            {
                throw new ArgumentException($"Unknown field '{hiddenGroupField}' in $apply.");
            }
        }
    }

    [GeneratedRegex(@"^aggregate\((.+)\)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: RegexTimeoutMilliseconds)]
    private static partial Regex AggregateExpressionRegex();

    [GeneratedRegex(@"^groupby\s*\(\s*\(([^)]+)\)\s*(?:,\s*aggregate\((.+)\))?\s*\)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: RegexTimeoutMilliseconds)]
    private static partial Regex GroupByExpressionRegex();

    [GeneratedRegex(@"^filter\((.+)\)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: RegexTimeoutMilliseconds)]
    private static partial Regex FilterExpressionRegex();

    [GeneratedRegex(@"^compute\((.+)\)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: RegexTimeoutMilliseconds)]
    private static partial Regex ComputeExpressionRegex();

    [GeneratedRegex(@"^(\w+)\s+with\s+(\w+)\s+as\s+(\w+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: RegexTimeoutMilliseconds)]
    private static partial Regex AggregateWithAsRegex();

    [GeneratedRegex(@"^\$count\s+as\s+(\w+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: RegexTimeoutMilliseconds)]
    private static partial Regex AggregateCountAsRegex();
}
