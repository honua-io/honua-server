// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.MySql.Features.Infrastructure;

namespace Honua.MySql.Features.FeatureStore.Services;

internal sealed partial class MySqlFeatureQueryBuilder
{
    [GeneratedRegex(@"@p(\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex NamedParameterRegex();

    [GeneratedRegex(
        @"^(?<field>[a-zA-Z_][a-zA-Z0-9_]*)\s*(?<op>NOT\s+LIKE|LIKE|>=|<=|!=|<>|=|>|<)\s*(?<value>'(?:''|[^'])*'|-?\d+(?:\.\d+)?)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ComparisonRegex();

    [GeneratedRegex(
        @"^(?<field>[a-zA-Z_][a-zA-Z0-9_]*)\s+IS\s+(?<not>NOT\s+)?NULL$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NullCheckRegex();

    private static void AppendWhereClause(
        StringBuilder sb,
        MySqlLayerMapping mapping,
        FeatureQuery query,
        ref int paramIndex,
        List<object> parameters)
    {
        if (query.SqlFilter is { } sqlFilter)
        {
            // Pre-translated SQL — re-number the embedded `@pN` placeholders so they
            // do not collide with the local sequence and append the filter parameters.
            var renumbered = ConvertNamedParameters(sqlFilter.Sql, ref paramIndex);
            sb.Append(CultureInfo.InvariantCulture, $" AND ({renumbered})");

            foreach (var p in sqlFilter.Parameters)
            {
                parameters.Add(p ?? DBNull.Value);
            }
        }
        else if (!string.IsNullOrWhiteSpace(query.Where))
        {
            var parameterized = ParseAndParameterizeWhereClause(query.Where.Trim(), ref paramIndex, parameters);
            sb.Append(CultureInfo.InvariantCulture, $" AND ({parameterized})");
        }

        if (query.ObjectIds is { IsDefaultOrEmpty: false } objectIds)
        {
            var placeholders = new string[objectIds.Length];
            for (var i = 0; i < objectIds.Length; i++)
            {
                placeholders[i] = $"@p{paramIndex++}";
                parameters.Add(objectIds[i]);
            }

            sb.Append(CultureInfo.InvariantCulture,
                $" AND {mapping.QuotedPrimaryKeyColumn} IN ({string.Join(", ", placeholders)})");
        }
    }

    private static void AppendOrderByClause(StringBuilder sb, MySqlLayerMapping mapping, FeatureQuery query)
    {
        if (query.OrderBy is { IsDefaultOrEmpty: false } orderBy)
        {
            var clauses = new List<string>(orderBy.Length);
            foreach (var clause in orderBy)
            {
                MySqlIdentifier.ValidateFieldName(clause.Field);
                var direction = clause.Ascending ? "ASC" : "DESC";
                clauses.Add($"{MySqlIdentifier.Quote(clause.Field)} {direction}");
            }

            sb.Append(CultureInfo.InvariantCulture, $" ORDER BY {string.Join(", ", clauses)}");
            return;
        }

        // No caller-supplied OrderBy. SQL row order is unspecified without ORDER BY, so any
        // LIMIT/OFFSET pagination is non-deterministic — pages can repeat or skip rows. The
        // streaming path (StreamFeatureBatchesAsync) and the paged read paths (QueryPageAsync,
        // BuildObjectIdsQuery) all flow through this builder, so injecting an ascending sort
        // on the configured primary key column once here covers every caller. No ORDER BY is
        // emitted for unpaginated queries — that path is fully materialised and stable order
        // there is the caller's responsibility.
        if (query.Limit.HasValue || query.Offset is > 0)
        {
            sb.Append(CultureInfo.InvariantCulture,
                $" ORDER BY {mapping.QuotedPrimaryKeyColumn} ASC");
        }
    }

    private static void AppendPagination(
        StringBuilder sb,
        FeatureQuery query,
        ref int paramIndex,
        List<object> parameters)
    {
        if (query.Limit.HasValue)
        {
            sb.Append(CultureInfo.InvariantCulture, $" LIMIT @p{paramIndex++}");
            parameters.Add(query.Limit.Value);

            if (query.Offset is > 0)
            {
                sb.Append(CultureInfo.InvariantCulture, $" OFFSET @p{paramIndex++}");
                parameters.Add(query.Offset.Value);
            }

            return;
        }

        if (query.Offset is > 0)
        {
            // MySQL pagination syntax requires LIMIT before OFFSET; standalone OFFSET is a
            // syntax error. OData $skip can map to Offset without $top → Limit, so this path
            // must still emit a valid statement. Per the MySQL reference ("To retrieve all
            // rows from a certain offset up to the end of the result set, you can use some
            // large number for the second parameter"), we emit the BIGINT UNSIGNED max
            // (18446744073709551615) inline so callers that omit Limit get the documented
            // "skip N, take all remaining" behaviour.
            // https://dev.mysql.com/doc/refman/8.4/en/select.html
            sb.Append(CultureInfo.InvariantCulture,
                $" LIMIT 18446744073709551615 OFFSET @p{paramIndex++}");
            parameters.Add(query.Offset.Value);
        }
    }

    private static string ConvertNamedParameters(string sql, ref int paramIndex)
    {
        var current = paramIndex;
        var max = -1;
        var result = NamedParameterRegex().Replace(sql, m =>
        {
            var idx = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            if (idx > max)
            {
                max = idx;
            }
            return $"@p{current + idx}";
        });
        paramIndex = current + max + 1;
        return result;
    }

    private static string ParseAndParameterizeWhereClause(
        string whereClause,
        ref int paramIndex,
        List<object> parameters)
    {
        // Minimal safe parser for legacy WHERE strings — restricts to single-field comparisons
        // and IS [NOT] NULL chained by AND. CQL2/OGC filters should reach here as SqlFilter
        // (via ISqlFilterTranslator), not Where.
        var expressions = SplitOnAnd(whereClause);
        if (expressions.Count == 0)
        {
            throw new ArgumentException("WHERE clause format not supported.");
        }

        var translated = new List<string>(expressions.Count);
        foreach (var raw in expressions)
        {
            var expr = raw.Trim();
            if (expr.Length == 0)
            {
                throw new ArgumentException("WHERE clause format not supported.");
            }

            if (expr.Equals("1=1", StringComparison.Ordinal) ||
                expr.Equals("TRUE", StringComparison.OrdinalIgnoreCase))
            {
                translated.Add("TRUE");
                continue;
            }

            var nullMatch = NullCheckRegex().Match(expr);
            if (nullMatch.Success)
            {
                var field = nullMatch.Groups["field"].Value;
                MySqlIdentifier.ValidateFieldName(field);
                var notToken = nullMatch.Groups["not"].Value;
                var notClause = string.IsNullOrWhiteSpace(notToken) ? string.Empty : "NOT ";
                translated.Add($"{MySqlIdentifier.Quote(field)} IS {notClause}NULL");
                continue;
            }

            var compMatch = ComparisonRegex().Match(expr);
            if (compMatch.Success)
            {
                var field = compMatch.Groups["field"].Value;
                MySqlIdentifier.ValidateFieldName(field);
                var op = NormalizeOperator(compMatch.Groups["op"].Value);
                var value = ParseValueToken(compMatch.Groups["value"].Value);
                translated.Add($"{MySqlIdentifier.Quote(field)} {op} @p{paramIndex++}");
                parameters.Add(value);
                continue;
            }

            throw new ArgumentException("WHERE clause format not supported.");
        }

        return string.Join(" AND ", translated);
    }

    private static string NormalizeOperator(string op) => op.Trim().ToUpperInvariant() switch
    {
        "NOT LIKE" => "NOT LIKE",
        "LIKE" => "LIKE",
        ">=" => ">=",
        "<=" => "<=",
        "!=" or "<>" => "!=",
        "=" => "=",
        ">" => ">",
        "<" => "<",
        _ => throw new ArgumentException($"Unsupported operator: {op}")
    };

    private static object ParseValueToken(string token)
    {
        if (token.StartsWith('\'') && token.EndsWith('\''))
        {
            return token[1..^1].Replace("''", "'", StringComparison.Ordinal);
        }

        if (decimal.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var num))
        {
            return num;
        }

        return token;
    }

    private static List<string> SplitOnAnd(string whereClause)
    {
        var parts = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;

        for (var i = 0; i < whereClause.Length; i++)
        {
            var c = whereClause[i];
            if (!inQuotes && c == '\'')
            {
                inQuotes = true;
                current.Append(c);
            }
            else if (inQuotes && c == '\'')
            {
                if (i + 1 < whereClause.Length && whereClause[i + 1] == '\'')
                {
                    current.Append("''");
                    i++;
                }
                else
                {
                    inQuotes = false;
                    current.Append(c);
                }
            }
            else if (!inQuotes && i + 3 <= whereClause.Length &&
                     whereClause.AsSpan(i, 3).Equals("AND", StringComparison.OrdinalIgnoreCase) &&
                     (i == 0 || !IsIdentifierChar(whereClause[i - 1])) &&
                     (i + 3 >= whereClause.Length || !IsIdentifierChar(whereClause[i + 3])))
            {
                parts.Add(current.ToString());
                current.Clear();
                i += 2;
            }
            else
            {
                current.Append(c);
            }
        }

        if (current.Length > 0)
        {
            parts.Add(current.ToString());
        }

        return parts;
    }

    // Identifier-boundary helper used by SplitOnAnd. Mirrors the regex character class
    // [a-zA-Z0-9_] used by ComparisonRegex / NullCheckRegex so column names that contain
    // or begin with "and" (e.g. start_and_end, and_flag) are not falsely split as
    // logical AND boundaries. Underscore is a legal identifier character in MySQL/MariaDB
    // and must not separate tokens.
    private static bool IsIdentifierChar(char c)
        => char.IsLetterOrDigit(c) || c == '_';
}
