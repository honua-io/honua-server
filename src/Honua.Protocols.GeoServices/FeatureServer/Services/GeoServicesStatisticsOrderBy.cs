// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.Protocols.GeoServices.FeatureServer.Services;

// A statistics query orders by the declared statistic aliases and group-by fields, not the layer schema, and the clause uses the declared spelling so raw request text never reaches SQL.
internal static class GeoServicesStatisticsOrderBy
{
    internal static bool TryResolve(
        string? orderByFields,
        ImmutableArray<StatisticDefinition> statistics,
        ImmutableArray<string>? groupByFields,
        out ImmutableArray<OrderByClause> orderBy,
        out string? error)
    {
        orderBy = default;
        error = null;

        if (string.IsNullOrWhiteSpace(orderByFields))
        {
            return true;
        }

        var clauses = new List<OrderByClause>();
        foreach (var term in orderByFields.Split(',', StringSplitOptions.RemoveEmptyEntries)
                     .Select(static raw => raw.Trim())
                     .Where(static trimmed => trimmed.Length > 0))
        {
            var parts = term.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length is 0 or > 2)
            {
                error = $"Invalid orderByFields value: {term}";
                return false;
            }

            var ascending = true;
            if (parts.Length == 2)
            {
                if (parts[1].Equals("DESC", StringComparison.OrdinalIgnoreCase))
                {
                    ascending = false;
                }
                else if (!parts[1].Equals("ASC", StringComparison.OrdinalIgnoreCase))
                {
                    error = $"Invalid orderByFields direction: {parts[1]}";
                    return false;
                }
            }

            var resolved = ResolveDeclaredName(parts[0], statistics, groupByFields);
            if (resolved == null)
            {
                error = $"Unknown orderByFields value: {parts[0]}. When outStatistics is present, "
                    + "orderByFields may only reference an outStatisticFieldName alias or a "
                    + "groupByFieldsForStatistics field.";
                return false;
            }

            clauses.Add(new OrderByClause(resolved, ascending));
        }

        orderBy = clauses.Count == 0 ? default : clauses.ToImmutableArray();
        return true;
    }

    private static string? ResolveDeclaredName(
        string field,
        ImmutableArray<StatisticDefinition> statistics,
        ImmutableArray<string>? groupByFields)
    {
        if (!statistics.IsDefaultOrEmpty)
        {
            foreach (var statistic in statistics)
            {
                if (statistic.OutStatisticFieldName.Equals(field, StringComparison.OrdinalIgnoreCase))
                {
                    return statistic.OutStatisticFieldName;
                }
            }
        }

        if (groupByFields is { } declaredGroupBy && !declaredGroupBy.IsDefaultOrEmpty)
        {
            foreach (var groupByField in declaredGroupBy)
            {
                if (groupByField.Equals(field, StringComparison.OrdinalIgnoreCase))
                {
                    return groupByField;
                }
            }
        }

        return null;
    }
}
