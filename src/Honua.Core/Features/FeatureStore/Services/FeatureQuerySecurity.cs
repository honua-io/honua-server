// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Text.RegularExpressions;
using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.Core.Features.FeatureStore.Services;

/// <summary>
/// Applies the field-level security contract to query expressions that can otherwise
/// consume a masked value without returning it in the normal feature projection.
/// Providers call this after resolving <see cref="FeatureQuery.EnforcedMaskedFields"/>
/// and before building any SQL for the query.
/// </summary>
public static class FeatureQuerySecurity
{
    public static void Validate(FeatureQuery query)
    {
        if (query.EnforcedMaskedFields is not { IsDefaultOrEmpty: false } maskedFields)
        {
            return;
        }

        var masked = maskedFields.ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (query.OutStatistics is { IsDefaultOrEmpty: false } statistics)
        {
            foreach (var statistic in statistics)
            {
                ThrowIfMasked(masked, statistic.OnStatisticField, "outStatistics");
            }
        }

        if (query.GroupByFields is { IsDefaultOrEmpty: false } groupByFields)
        {
            foreach (var field in groupByFields)
            {
                ThrowIfMasked(masked, field, "groupByFieldsForStatistics");
            }
        }

        if (query.Having is { IsDefaultOrEmpty: false } having)
        {
            foreach (var condition in having)
            {
                ThrowIfMasked(masked, condition.OnStatisticField, "having");
            }
        }

        if (query.OrderBy is { IsDefaultOrEmpty: false } orderBy)
        {
            foreach (var clause in orderBy)
            {
                ThrowIfMasked(masked, clause.Field, "orderByFields");
            }

            // Aggregate order-by clauses may use an alias rather than the source field.
            // Resolve those aliases back to their statistic/group declaration here.
            if (query.OutStatistics is { IsDefaultOrEmpty: false } orderedStatistics)
            {
                foreach (var clause in orderBy)
                {
                    var statistic = orderedStatistics.FirstOrDefault(candidate =>
                        candidate.OutStatisticFieldName.Equals(clause.Field, StringComparison.OrdinalIgnoreCase));
                    if (!string.IsNullOrEmpty(statistic.OnStatisticField))
                    {
                        ThrowIfMasked(masked, statistic.OnStatisticField, "orderByFields");
                    }

                    if (query.GroupByFields is { IsDefaultOrEmpty: false } orderedGroups &&
                        orderedGroups.Any(field => field.Equals(clause.Field, StringComparison.OrdinalIgnoreCase)))
                    {
                        ThrowIfMasked(masked, clause.Field, "orderByFields");
                    }
                }
            }
        }

        if (query.TopFilter is { } topFilter)
        {
            foreach (var field in topFilter.GroupByFields)
            {
                ThrowIfMasked(masked, field, "queryTopFeatures groupByFields");
            }

            foreach (var clause in topFilter.OrderByFields)
            {
                ThrowIfMasked(masked, clause.Field, "queryTopFeatures orderByFields");
            }
        }

        if (!string.IsNullOrWhiteSpace(query.Where))
        {
            ThrowIfFilterReferencesMaskedField(masked, query.Where, "where");
        }

        if (query.TextSearch is { } textSearch)
        {
            foreach (var field in textSearch.Fields)
            {
                ThrowIfMasked(masked, field, "text search");
            }
        }

        if (query.SqlFilter is { } sqlFilter)
        {
            ThrowIfFilterReferencesMaskedField(masked, sqlFilter.Sql, "sqlFilter");
        }

        if (query.TemporalFilter is { } temporalFilter)
        {
            ThrowIfMasked(masked, temporalFilter.PropertyName, "temporal filter");
            if (!string.IsNullOrWhiteSpace(temporalFilter.EndPropertyName))
            {
                ThrowIfMasked(masked, temporalFilter.EndPropertyName, "temporal filter");
            }
        }

        if (!string.IsNullOrWhiteSpace(query.PublicIdAttributeName))
        {
            ThrowIfMasked(masked, query.PublicIdAttributeName, "public feature id");
        }
    }

    public static void ValidateH3(FeatureQuery query, H3AggregationQuery h3Query)
    {
        Validate(query);
        if (query.EnforcedMaskedFields is not { IsDefaultOrEmpty: false } maskedFields)
        {
            return;
        }

        var masked = maskedFields.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (h3Query.OutStatistics is { IsDefaultOrEmpty: false } statistics)
        {
            foreach (var statistic in statistics)
            {
                ThrowIfMasked(masked, statistic.OnStatisticField, "queryH3 outStatistics");
            }
        }

        if (h3Query.SummaryDefinitions is { IsDefaultOrEmpty: false } summaries)
        {
            foreach (var summary in summaries)
            {
                if (!string.IsNullOrWhiteSpace(summary.Field))
                {
                    ThrowIfMasked(masked, summary.Field, "queryH3 summary");
                }

                if (!string.IsNullOrWhiteSpace(summary.CategoryOrderBy))
                {
                    ThrowIfFilterReferencesMaskedField(masked, summary.CategoryOrderBy, "queryH3 category order");
                }
            }
        }
    }

    public static void ValidateBins(FeatureQuery query, BinDefinition binDefinition)
    {
        Validate(query);
        ValidateBinFields(query.EnforcedMaskedFields, binDefinition.Field, binDefinition.OutStatistics, "queryBins");
        if (binDefinition.DateBin is { } dateBin)
        {
            ValidateBinFields(query.EnforcedMaskedFields, dateBin.BinField, dateBin.OutStatistics, "queryBins date bin");
        }
    }

    public static void ValidateDateBins(FeatureQuery query, DateBinDefinition dateBin)
    {
        Validate(query);
        ValidateBinFields(query.EnforcedMaskedFields, dateBin.BinField, dateBin.OutStatistics, "queryDateBins");
    }

    private static void ValidateBinFields(
        ImmutableArray<string>? enforcedMaskedFields,
        string binField,
        ImmutableArray<StatisticDefinition>? statistics,
        string surface)
    {
        if (enforcedMaskedFields is not { IsDefaultOrEmpty: false } maskedFields)
        {
            return;
        }

        var masked = maskedFields.ToHashSet(StringComparer.OrdinalIgnoreCase);
        ThrowIfMasked(masked, binField, $"{surface} bin field");
        if (statistics is { IsDefaultOrEmpty: false })
        {
            foreach (var statistic in statistics)
            {
                ThrowIfMasked(masked, statistic.OnStatisticField, $"{surface} outStatistics");
            }
        }
    }

    private static void ThrowIfMasked(HashSet<string> masked, string field, string surface)
    {
        if (masked.Contains(field))
        {
            throw new ArgumentException(
                $"Field '{field}' is masked and cannot be used by {surface}.",
                nameof(field));
        }
    }

    private static void ThrowIfFilterReferencesMaskedField(HashSet<string> masked, string expression, string surface)
    {
        foreach (var field in masked.Where(field =>
                     FieldComparisonRegex(field).IsMatch(expression) ||
                     FieldSortRegex(field).IsMatch(expression) ||
                     AttributeAccessorRegex(field).IsMatch(expression)))
        {
            throw new ArgumentException(
                $"Field '{field}' is masked and cannot be used by {surface}.",
                nameof(expression));
        }
    }

    private static Regex AttributeAccessorRegex(string field)
    {
        var escapedField = field.Replace("'", "''", StringComparison.Ordinal);
        return new(
            $@"(?:->>|->)\s*'{Regex.Escape(escapedField)}'",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static Regex FieldComparisonRegex(string field)
        => new(
            $@"(?<![A-Za-z0-9_]){Regex.Escape(field)}(?![A-Za-z0-9_])\s*(?:=|<>|!=|<=|>=|<|>|LIKE\b|ILIKE\b|IS\b|IN\b)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static Regex FieldSortRegex(string field)
        => new(
            $@"(?:^|,)\s*{Regex.Escape(field)}\s+(?:ASC|DESC)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
}
