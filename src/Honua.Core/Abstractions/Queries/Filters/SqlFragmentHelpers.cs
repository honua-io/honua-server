// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using System.Text.RegularExpressions;

namespace Honua.Core.Queries.Filters;

public static class SqlFragmentHelpers
{
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

    public static string RenumberSqlFragmentParameters(string sql, int offset)
    {
        var namedSql = Regex.Replace(
            sql,
            @"@p(\d+)",
            match => "@p" + (int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) + offset).ToString(CultureInfo.InvariantCulture),
            RegexOptions.CultureInvariant);

        return Regex.Replace(
            namedSql,
            @"\$(\d+)",
            match => "$" + (int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) + offset).ToString(CultureInfo.InvariantCulture),
            RegexOptions.CultureInvariant);
    }
}
