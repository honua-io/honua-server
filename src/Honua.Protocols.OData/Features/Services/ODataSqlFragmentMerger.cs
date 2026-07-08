// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text.RegularExpressions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Queries.Filters;

namespace Honua.Protocols.OData.Services;

/// <summary>
/// Shared helper for combining an additional <see cref="SqlFragment"/> into a
/// <see cref="FeatureQuery"/>'s existing <see cref="FeatureQuery.SqlFilter"/>. Centralizes the
/// <c>@p</c> parameter re-indexing and AND-merge logic so the $search and $apply paths cannot
/// diverge and introduce a parameter-collision bug in one path but not the other.
/// </summary>
internal static partial class ODataSqlFragmentMerger
{
    /// <summary>
    /// Merges <paramref name="sqlFragment"/> into <paramref name="query"/>'s existing SQL filter
    /// (AND-combined), re-indexing the fragment's <c>@p</c> placeholders so they do not collide with
    /// the query's existing parameters. When the query has no existing SQL filter the fragment is
    /// applied as-is. A <c>null</c> fragment leaves the query unchanged.
    /// </summary>
    public static FeatureQuery Merge(FeatureQuery query, SqlFragment? sqlFragment)
    {
        if (sqlFragment == null)
        {
            return query;
        }

        if (query.SqlFilter == null)
        {
            return query with { SqlFilter = sqlFragment, Where = null };
        }

        var offset = query.SqlFilter.Parameters.Count;
        var reindexedSql = ReindexSqlParameters(sqlFragment.Sql, offset);
        var combinedSql = $"({query.SqlFilter.Sql}) AND ({reindexedSql})";
        var combinedParameters = query.SqlFilter.Parameters.Concat(sqlFragment.Parameters).ToArray();
        return query with
        {
            SqlFilter = new SqlFragment(combinedSql, combinedParameters),
            Where = null
        };
    }

    /// <summary>
    /// Shifts every <c>@p&lt;n&gt;</c> placeholder in <paramref name="sql"/> by
    /// <paramref name="offset"/> so two parameter sets can be concatenated without collisions.
    /// </summary>
    public static string ReindexSqlParameters(string sql, int offset)
    {
        if (offset == 0)
        {
            return sql;
        }

        return ParameterPlaceholderRegex().Replace(
            sql,
            match =>
            {
                var index = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
                return $"@p{index + offset}";
            });
    }

    [GeneratedRegex(@"@p(\d+)")]
    private static partial Regex ParameterPlaceholderRegex();
}
