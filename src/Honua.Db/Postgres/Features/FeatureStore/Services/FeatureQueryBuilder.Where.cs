// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Buffers;
using System.Collections.Frozen;
using System.Globalization;
using System.Text;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Queries.Filters;
using Honua.Db.Postgres.Features.Infrastructure;

namespace Honua.Db.Postgres.Features.FeatureStore.Services;

internal sealed partial class FeatureQueryBuilder
{
    private const string UnsupportedWhereClauseMessage =
        "WHERE clause format not supported. Use simple comparisons like: name = 'value' or age > 18";

    private static readonly FrozenDictionary<string, string> PhysicalWhereColumns =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [DatabaseSchema.ObjectIdColumn] = DatabaseSchema.ObjectIdColumn,
            [DatabaseSchema.ObjectIdColumnAlt] = DatabaseSchema.ObjectIdColumn,
            [DatabaseSchema.LayerIdColumnAlt] = DatabaseSchema.LayerIdColumn,
            [DatabaseSchema.LayerIdColumn] = DatabaseSchema.LayerIdColumn,
            [DatabaseSchema.GeometryColumn] = DatabaseSchema.GeometryColumn,
            ["created_at"] = "created_at",
            ["updated_at"] = "updated_at",
        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    private static readonly (string Pattern, bool IsWord)[] DangerousPatternEntries =
    [
        ("UNION", true), ("SELECT", true), ("INSERT", true), ("UPDATE", true), ("DELETE", true),
        ("DROP", true), ("CREATE", true), ("ALTER", true),
        ("EXEC", true), ("EXECUTE", true), ("xp_", true), ("sp_", true), ("INFORMATION_SCHEMA", true),
        ("--", false), ("/*", false), ("*/", false), (";", false),
        ("COPY", true), ("TRUNCATE", true), ("GRANT", true), ("REVOKE", true), ("VACUUM", true),
        ("ANALYZE", true), ("EXPLAIN", true),
        ("DO", true), ("RAISE", true), ("PERFORM", true), ("NOTIFY", true), ("LISTEN", true),
        ("pg_read_file", true), ("pg_write_file", true), ("pg_ls_dir", true), ("pg_stat_file", true),
        ("lo_import", true), ("lo_export", true), ("pg_sleep", true), ("current_setting", true), ("set_config", true),
        ("pg_advisory_lock", true), ("pg_advisory_unlock", true),
        ("dblink", true), ("dblink_connect", true),
        ("pg_terminate_backend", true), ("pg_cancel_backend", true),
        ("pg_reload_conf", true),
        ("query_to_xml", true), ("xpath", true),
        ("RETURNING", true),
        ("chr", true), ("convert_from", true)
    ];

    private static readonly SearchValues<string> DangerousPatterns = SearchValues.Create(
        DangerousPatternEntries.Select(static entry => entry.Pattern).ToArray(),
        StringComparison.OrdinalIgnoreCase);

    private static void AppendWhereClause(StringBuilder sql, FeatureQuery query, ref int paramIndex, List<object> parameters)
    {
        if (query.TextSearch is { } search)
        {
            var searchIndex = paramIndex;
            var predicate = FeatureTextSearchSql.Build(search, field => "attributes->>" + "\u0027" + field.Replace("\u0027", "\u0027\u0027", StringComparison.Ordinal) + "\u0027", text =>
            {
                var marker = "$" + (searchIndex++).ToString(CultureInfo.InvariantCulture);
                parameters.Add(text);
                return marker;
            }, (column, value) => $"STRPOS({column}, {value})");
            paramIndex = searchIndex;
            sql.Append(" AND ").Append(predicate);
        }

        if (query.EnforcedSqlFilter != null)
        {
            AppendSqlFragment(sql, query.EnforcedSqlFilter, ref paramIndex, parameters);
        }

        // Prefer SqlFragment if available (CQL2 filters with proper parameterization)
        if (query.SqlFilter != null)
        {
            AppendSqlFragment(sql, query.SqlFilter, ref paramIndex, parameters);
        }
        // Fall back to legacy string WHERE clause for backward compatibility
        else if (!string.IsNullOrWhiteSpace(query.Where))
        {
            var whereClause = query.Where.Trim();

            // Parse and parameterize simple WHERE clauses
            // Supports: field = 'value', field > 123, field LIKE 'pattern%'
            var parameterizedClause = ParseAndParameterizeWhereClause(whereClause, ref paramIndex, parameters);

            sql.Append(CultureInfo.InvariantCulture, $" AND ({parameterizedClause})");
        }

        if (query.ObjectIds.HasValue && !query.ObjectIds.Value.IsDefaultOrEmpty)
        {
            var objectIds = query.ObjectIds.Value;
            sql.Append(CultureInfo.InvariantCulture, $" AND {DatabaseSchema.ObjectIdColumn} = ANY(${paramIndex}::bigint[])");
            parameters.Add(objectIds.ToArray());
            paramIndex++;
        }
    }

    private static void AppendSqlFragment(
        StringBuilder sql,
        SqlFragment sqlFragment,
        ref int paramIndex,
        List<object> parameters)
    {
        // Convert @p0, @p1, etc. to positional $N, $N+1, etc. parameters
        var convertedSql = ConvertNamedParametersToPositional(sqlFragment.Sql, ref paramIndex);

        sql.Append(CultureInfo.InvariantCulture, $" AND ({convertedSql})");

        foreach (var param in sqlFragment.Parameters)
        {
            parameters.Add(param ?? DBNull.Value);
        }
    }

    internal static bool IsSupportedWhereClause(string whereClause)
    {
        ArgumentNullException.ThrowIfNull(whereClause);
        var scratchIndex = 1;
        try
        {
            ParseAndParameterizeWhereClause(whereClause.Trim(), ref scratchIndex, []);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    internal static string ParseAndParameterizeWhereClause(string whereClause, ref int paramIndex, List<object> parameters)
    {
        var dangerousPattern = FindDangerousPattern(whereClause);
        if (dangerousPattern != null)
        {
            throw new ArgumentException("WHERE clause contains an unsupported expression");
        }

        var parameterizedExpressions = new List<string>();
        foreach (var expression in LegacyWhereClauseParser.Parse(WhereClauseSplitter.SplitOnAnd(whereClause), LegacyWhereGrammar.Postgres, UnsupportedWhereClauseMessage))
        {
            if (expression.Kind == LegacyWhereExpressionKind.True)
            {
                parameterizedExpressions.Add("TRUE");
                continue;
            }

            var nullMatch = expression.Match;
            if (expression.Kind == LegacyWhereExpressionKind.NullCheck)
            {
                var fieldName = nullMatch.Groups["field"].Value;
                var fieldSql = MapWhereField(fieldName, ref paramIndex, parameters, out _);
                var notToken = nullMatch.Groups["not"].Value;
                var notClause = string.IsNullOrWhiteSpace(notToken) ? string.Empty : "NOT ";
                parameterizedExpressions.Add($"{fieldSql} IS {notClause}NULL");
                continue;
            }

            var inMatch = expression.Match;
            if (expression.Kind == LegacyWhereExpressionKind.In)
            {
                var valueTokens = LegacyWhereClauseParser.SplitValueTokens(inMatch.Groups["values"].Value);
                var fieldSql = MapWhereField(
                    inMatch.Groups["field"].Value,
                    ref paramIndex,
                    parameters,
                    out var isAttributeField);
                var allNumeric = valueTokens.All(token =>
                    !IsQuotedStringLiteral(token) &&
                    decimal.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out _));
                if (isAttributeField && allNumeric)
                {
                    fieldSql = $"NULLIF({fieldSql}, '')::numeric";
                }

                var placeholders = new List<string>(valueTokens.Count);
                foreach (var valueToken in valueTokens)
                {
                    placeholders.Add($"${paramIndex}");
                    parameters.Add(LegacyWhereClauseParser.ParseValueToken(valueToken));
                    paramIndex++;
                }

                parameterizedExpressions.Add($"{fieldSql} IN ({string.Join(", ", placeholders)})");
                continue;
            }

            var comparisonMatch = expression.Match;
            if (expression.Kind == LegacyWhereExpressionKind.Comparison)
            {
                var fieldName = comparisonMatch.Groups["field"].Value;
                var operatorValue = comparisonMatch.Groups["op"].Value;
                var valueToken = comparisonMatch.Groups["value"].Value;

                var fieldSql = MapWhereField(fieldName, ref paramIndex, parameters, out var isAttributeField);
                var normalizedOperator = LegacyWhereClauseParser.NormalizeOperator(operatorValue, LegacyWhereGrammar.Postgres, UnsupportedWhereClauseMessage);
                var forceTextComparison = normalizedOperator.Contains("LIKE", StringComparison.OrdinalIgnoreCase);
                var isQuoted = IsQuotedStringLiteral(valueToken);
                var isNumericLiteral = !isQuoted &&
                    decimal.TryParse(valueToken, NumberStyles.Float, CultureInfo.InvariantCulture, out _);

                if (isAttributeField && isNumericLiteral && !forceTextComparison)
                {
                    fieldSql = $"NULLIF({fieldSql}, '')::numeric";
                }

                var parameterizedValue = LegacyWhereClauseParser.ParseValueToken(valueToken, forceTextComparison ? LegacyWhereValuePolicy.Text : LegacyWhereValuePolicy.NumericOrText);
                parameterizedExpressions.Add($"{fieldSql} {normalizedOperator} ${paramIndex}");
                parameters.Add(parameterizedValue);
                paramIndex++;
            }
            else
            {
                throw new ArgumentException(UnsupportedWhereClauseMessage);
            }
        }

        return string.Join(" AND ", parameterizedExpressions);
    }

    private static string BuildAttributeValueExpression(string attributeName, ref int paramIndex, List<object> parameters)
    {
        var fieldParamIndex = paramIndex++;
        parameters.Add(attributeName);
        return DatabaseSchema.BuildJsonPathParameter(fieldParamIndex);
    }

    private static string MapWhereField(string fieldName, ref int paramIndex, List<object> parameters, out bool isAttributeField)
    {
        var jsonPathIndex = fieldName.IndexOf("->>", StringComparison.Ordinal);
        if (jsonPathIndex >= 0)
        {
            var baseField = fieldName[..jsonPathIndex];
            if (!baseField.Equals(DatabaseSchema.AttributesColumn, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException(UnsupportedWhereClauseMessage);
            }

            var jsonPathLiteral = fieldName[(jsonPathIndex + 3)..];
            if (!IsQuotedStringLiteral(jsonPathLiteral))
            {
                throw new ArgumentException(UnsupportedWhereClauseMessage);
            }

            var attributeName = UnescapeSqlString(jsonPathLiteral);
            isAttributeField = true;
            return DatabaseSchema.BuildJsonPath(attributeName);
        }

        if (PhysicalWhereColumns.TryGetValue(fieldName, out var physicalColumn))
        {
            isAttributeField = false;
            return physicalColumn;
        }

        isAttributeField = true;
        return $"({DatabaseSchema.BuildJsonPath(fieldName)})";
    }

    private static bool IsQuotedStringLiteral(string valueToken)
        => valueToken.Length >= 2 &&
           valueToken.StartsWith('\'') &&
           valueToken.EndsWith('\'');

    private static string UnescapeSqlString(string valueToken)
    {
        var content = valueToken[1..^1]; // Remove surrounding quotes
        return content.Replace("''", "'"); // Unescape single quotes
    }

    private static bool IsIdentifierChar(char value) =>
        char.IsLetterOrDigit(value) || value == '_';

    internal static string? FindDangerousPattern(string whereClause)
    {
        var span = whereClause.AsSpan();
        var segmentStart = 0;
        var inQuotes = false;
        var quoteChar = '\0';

        for (var i = 0; i < span.Length; i++)
        {
            var c = span[i];
            if (!inQuotes && (c == '\'' || c == '"'))
            {
                if (i > segmentStart)
                {
                    var match = FindDangerousPatternInSegment(span, segmentStart, i - segmentStart);
                    if (match is not null)
                    {
                        return match;
                    }
                }

                inQuotes = true;
                quoteChar = c;
            }
            else if (inQuotes && c == quoteChar)
            {
                if (i + 1 < span.Length && span[i + 1] == quoteChar)
                {
                    i++;
                }
                else
                {
                    inQuotes = false;
                    quoteChar = '\0';
                    segmentStart = i + 1;
                }
            }
        }

        if (!inQuotes && segmentStart < span.Length)
        {
            return FindDangerousPatternInSegment(span, segmentStart, span.Length - segmentStart);
        }

        return null;
    }

    private static string? FindDangerousPatternInSegment(
        ReadOnlySpan<char> fullInput,
        int segmentStart,
        int segmentLength)
    {
        var segmentEnd = segmentStart + segmentLength;
        var searchPos = segmentStart;

        while (searchPos < segmentEnd)
        {
            var relativeHit = fullInput.Slice(searchPos, segmentEnd - searchPos).IndexOfAny(DangerousPatterns);
            if (relativeHit < 0)
            {
                return null;
            }

            var hitPos = searchPos + relativeHit;
            var remaining = fullInput.Slice(hitPos, segmentEnd - hitPos);
            var beforeChar = hitPos > 0 ? fullInput[hitPos - 1] : ' ';

            foreach (var (pattern, isWordPattern) in DangerousPatternEntries)
            {
                if (remaining.Length >= pattern.Length &&
                    remaining.Slice(0, pattern.Length).Equals(pattern, StringComparison.OrdinalIgnoreCase))
                {
                    if (!isWordPattern)
                    {
                        return pattern;
                    }

                    var afterPos = hitPos + pattern.Length;
                    var afterChar = afterPos < fullInput.Length ? fullInput[afterPos] : ' ';
                    if (!IsIdentifierChar(beforeChar) && !IsIdentifierChar(afterChar))
                    {
                        return pattern;
                    }
                }
            }

            searchPos = hitPos + 1;
        }

        return null;
    }
}
