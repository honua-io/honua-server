// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.Protocols.GeoServices.FeatureServer.Services;

/// <summary>
/// Resolves <c>orderByFields</c> for a GeoServices statistics query (<c>outStatistics</c>).
///
/// <para>
/// Per the Esri REST spec the result columns of a statistics query are the declared
/// <c>outStatisticFieldName</c> aliases plus the <c>groupByFieldsForStatistics</c> columns —
/// the layer's source fields are <em>not</em> in the aggregate result set. Validating
/// <c>orderByFields</c> against the layer schema (as the non-aggregate path does) therefore uses
/// the wrong denominator and rejects the only names that can legally order aggregate output,
/// which is what honua-server#3372 reported.
/// </para>
///
/// <para>
/// The accepted set stays tightly bounded: every term must resolve, case-insensitively, to a
/// statistic alias or group-by field that the <em>same request</em> already declared and that the
/// statistics parser already validated against the layer schema. The clause carries the declared
/// spelling, never the caller's, so nothing derived from raw request text reaches the SQL builder;
/// unknown names are still rejected.
/// </para>
/// </summary>
internal static class GeoServicesStatisticsOrderBy
{
    /// <summary>
    /// Resolves the <c>orderByFields</c> terms of a statistics query against the statistic aliases
    /// and group-by fields declared by the same request.
    /// </summary>
    /// <returns><see langword="true"/> when every term resolved; otherwise <see langword="false"/> with <paramref name="error"/> set.</returns>
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

    /// <summary>
    /// Maps a caller-supplied order-by name onto the declaration it names, returning the
    /// <em>declared</em> spelling so the caller's text never propagates. Statistic aliases win over
    /// group-by fields, matching the aggregate select list where the alias shadows the source column.
    /// </summary>
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
