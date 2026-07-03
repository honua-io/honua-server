// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text.RegularExpressions;

namespace Honua.Core.Queries.Filters;

/// <summary>
/// Helpers for combining parameterized <see cref="SqlFragment"/> instances produced by
/// independent protocol filter adapters (WFS, STAC, OGC API Features).
/// </summary>
public static class SqlFragmentHelpers
{
    /// <summary>
    /// Combines two SQL fragments with a logical <c>AND</c>, renumbering the right fragment's
    /// <c>@pN</c> parameter placeholders so they follow the left fragment's parameters without
    /// collision. Returns the non-null fragment when the other side is <see langword="null"/>,
    /// or <see langword="null"/> when both are <see langword="null"/>.
    /// </summary>
    /// <param name="left">The left-hand SQL fragment, or <see langword="null"/>.</param>
    /// <param name="right">The right-hand SQL fragment, or <see langword="null"/>.</param>
    /// <returns>The combined SQL fragment, or <see langword="null"/> when both inputs are null.</returns>
    public static SqlFragment? CombineSqlFilters(SqlFragment? left, SqlFragment? right)
    {
        if (left is null)
        {
            return right;
        }

        if (right is null)
        {
            return left;
        }

        var rightSql = RenumberSqlFragmentParameters(right.Sql, left.Parameters.Count);
        return new SqlFragment(
            $"({left.Sql}) AND ({rightSql})",
            left.Parameters.Concat(right.Parameters).ToArray());
    }

    /// <summary>
    /// Shifts every <c>@pN</c> parameter placeholder in <paramref name="sql"/> by
    /// <paramref name="offset"/> so it can be concatenated after an existing parameter list.
    /// </summary>
    /// <param name="sql">The SQL text whose placeholders should be renumbered.</param>
    /// <param name="offset">The number of leading parameters to skip past.</param>
    /// <returns>The SQL text with renumbered placeholders.</returns>
    public static string RenumberSqlFragmentParameters(string sql, int offset)
        => Regex.Replace(
            sql,
            @"@p(\d+)",
            match => "@p" + (int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) + offset).ToString(CultureInfo.InvariantCulture),
            RegexOptions.CultureInvariant);
}
