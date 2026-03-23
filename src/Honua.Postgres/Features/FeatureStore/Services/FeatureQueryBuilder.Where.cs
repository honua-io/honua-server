// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Postgres.Features.Infrastructure;

namespace Honua.Postgres.Features.FeatureStore.Services;

internal sealed partial class FeatureQueryBuilder
{
    private const string UnsupportedWhereClauseMessage =
        "WHERE clause format not supported. Use simple comparisons like: name = 'value' or age > 18";

    [GeneratedRegex(
        @"^(?<field>[a-zA-Z_][a-zA-Z0-9_]*(?:->>'[^']+')?)\s*(?<op>NOT\s+LIKE|LIKE|>=|<=|!=|<>|=|>|<)\s*(?<value>'(?:''|[^'])*'|-?\d+(?:\.\d+)?)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ComparisonRegex();

    [GeneratedRegex(
        @"^(?<field>[a-zA-Z_][a-zA-Z0-9_]*(?:->>'[^']+')?)\s+IS\s+(?<not>NOT\s+)?NULL$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NullCheckRegex();

    [GeneratedRegex(
        @"^(?:1\s*=\s*1|TRUE)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TrueLiteralRegex();

    private static void AppendWhereClause(StringBuilder sql, FeatureQuery query, ref int paramIndex, List<object> parameters)
    {
        // Prefer SqlFragment if available (CQL2 filters with proper parameterization)
        if (query.SqlFilter != null)
        {
            var sqlFragment = query.SqlFilter;

            // Convert @p0, @p1, etc. to positional $N, $N+1, etc. parameters
            var convertedSql = ConvertNamedParametersToPositional(sqlFragment.Sql, ref paramIndex);

            // Append the converted SQL
            sql.Append(CultureInfo.InvariantCulture, $" AND ({convertedSql})");

            foreach (var param in sqlFragment.Parameters)
            {
                parameters.Add(param ?? DBNull.Value);
            }
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
            var placeholders = new string[objectIds.Length];

            for (var i = 0; i < objectIds.Length; i++)
            {
                placeholders[i] = $"${paramIndex + i}";
            }

            sql.Append(CultureInfo.InvariantCulture, $" AND {DatabaseSchema.ObjectIdColumn} = ANY(ARRAY[{string.Join(", ", placeholders)}])");

            foreach (var objectId in objectIds)
            {
                parameters.Add(objectId);
            }

            paramIndex += objectIds.Length;
        }
    }

    internal static string ParseAndParameterizeWhereClause(string whereClause, ref int paramIndex, List<object> parameters)
    {
        var dangerousPattern = FindDangerousPattern(whereClause);
        if (dangerousPattern != null)
        {
            throw new ArgumentException("WHERE clause contains an unsupported expression");
        }

        var expressions = SplitOnAnd(whereClause);
        if (expressions.Count == 0)
        {
            throw new ArgumentException(UnsupportedWhereClauseMessage);
        }

        var parameterizedExpressions = new List<string>(expressions.Count);

        foreach (var expression in expressions)
        {
            var trimmedExpression = expression.Trim();
            if (trimmedExpression.Length == 0)
            {
                throw new ArgumentException(UnsupportedWhereClauseMessage);
            }

            if (TrueLiteralRegex().IsMatch(trimmedExpression))
            {
                parameterizedExpressions.Add("TRUE");
                continue;
            }

            var nullMatch = NullCheckRegex().Match(trimmedExpression);
            if (nullMatch.Success)
            {
                var fieldName = nullMatch.Groups["field"].Value;
                var fieldSql = MapWhereField(fieldName, ref paramIndex, parameters, out _);
                var notToken = nullMatch.Groups["not"].Value;
                var notClause = string.IsNullOrWhiteSpace(notToken) ? string.Empty : "NOT ";
                parameterizedExpressions.Add($"{fieldSql} IS {notClause}NULL");
                continue;
            }

            var comparisonMatch = ComparisonRegex().Match(trimmedExpression);
            if (comparisonMatch.Success)
            {
                var fieldName = comparisonMatch.Groups["field"].Value;
                var operatorValue = comparisonMatch.Groups["op"].Value;
                var valueToken = comparisonMatch.Groups["value"].Value;

                var fieldSql = MapWhereField(fieldName, ref paramIndex, parameters, out var isAttributeField);
                var normalizedOperator = NormalizeOperator(operatorValue);
                var forceTextComparison = normalizedOperator.Contains("LIKE", StringComparison.OrdinalIgnoreCase);
                var isQuoted = IsQuotedStringLiteral(valueToken);
                var isNumericLiteral = !isQuoted &&
                    decimal.TryParse(valueToken, NumberStyles.Float, CultureInfo.InvariantCulture, out _);

                if (isAttributeField && isNumericLiteral && !forceTextComparison)
                {
                    fieldSql = $"NULLIF({fieldSql}, '')::numeric";
                }

                var parameterizedValue = ParseValueToken(valueToken, forceTextComparison);
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

        isAttributeField = fieldName.ToLowerInvariant() switch
        {
            DatabaseSchema.ObjectIdColumn => false,
            DatabaseSchema.ObjectIdColumnAlt => false,
            DatabaseSchema.LayerIdColumnAlt => false,
            DatabaseSchema.LayerIdColumn => false,
            DatabaseSchema.GeometryColumn => false,
            "created_at" => false,
            "updated_at" => false,
            _ => true
        };

        return fieldName.ToLowerInvariant() switch
        {
            DatabaseSchema.ObjectIdColumn => DatabaseSchema.ObjectIdColumn,
            DatabaseSchema.ObjectIdColumnAlt => DatabaseSchema.ObjectIdColumn,
            DatabaseSchema.LayerIdColumnAlt => DatabaseSchema.LayerIdColumn,
            DatabaseSchema.LayerIdColumn => DatabaseSchema.LayerIdColumn,
            DatabaseSchema.GeometryColumn => DatabaseSchema.GeometryColumn,
            "created_at" => "created_at",
            "updated_at" => "updated_at",
            _ => $"({DatabaseSchema.BuildJsonPath(fieldName)})"
        };
    }

    private static string NormalizeOperator(string operatorValue)
    {
        return operatorValue.Trim().ToUpperInvariant() switch
        {
            "NOT LIKE" => "NOT LIKE",
            "LIKE" => "LIKE",
            ">=" => ">=",
            "<=" => "<=",
            "!=" => "!=",
            "<>" => "!=",
            "=" => "=",
            ">" => ">",
            "<" => "<",
            _ => throw new ArgumentException(UnsupportedWhereClauseMessage)
        };
    }

    private static bool IsQuotedStringLiteral(string valueToken)
        => valueToken.Length >= 2 &&
           valueToken.StartsWith('\'') &&
           valueToken.EndsWith('\'');

    private static object ParseValueToken(string valueToken, bool forceText)
    {
        if (valueToken.StartsWith('\'') && valueToken.EndsWith('\''))
        {
            return UnescapeSqlString(valueToken);
        }

        if (!forceText && decimal.TryParse(valueToken, NumberStyles.Float, CultureInfo.InvariantCulture, out var decimalValue))
        {
            return decimalValue;
        }

        return valueToken;
    }

    private static string UnescapeSqlString(string valueToken)
    {
        var content = valueToken[1..^1]; // Remove surrounding quotes
        return content.Replace("''", "'"); // Unescape single quotes
    }

    private static List<string> SplitOnAnd(string whereClause)
    {
        var parts = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;
        var quoteChar = '\0';

        for (var i = 0; i < whereClause.Length; i++)
        {
            var c = whereClause[i];

            if (!inQuotes && (c == '\'' || c == '"'))
            {
                inQuotes = true;
                quoteChar = c;
                current.Append(c);
            }
            else if (inQuotes && c == quoteChar)
            {
                if (i + 1 < whereClause.Length && whereClause[i + 1] == quoteChar)
                {
                    current.Append(c);
                    current.Append(c);
                    i++;
                }
                else
                {
                    inQuotes = false;
                    quoteChar = '\0';
                    current.Append(c);
                }
            }
            else if (!inQuotes && IsAndTokenAt(whereClause, i))
            {
                parts.Add(current.ToString());
                current.Clear();
                i += 2; // Skip "AND"
                while (i < whereClause.Length && char.IsWhiteSpace(whereClause[i]))
                {
                    i++;
                }
                i--; // Compensate for the loop increment
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

    private static bool IsAndTokenAt(string whereClause, int index)
    {
        if (index + 3 > whereClause.Length)
        {
            return false;
        }

        var candidate = whereClause.Substring(index, 3);
        if (!candidate.Equals("AND", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var prevChar = index > 0 ? whereClause[index - 1] : ' ';
        var nextChar = index + 3 < whereClause.Length ? whereClause[index + 3] : ' ';

        return !IsIdentifierChar(prevChar) && !IsIdentifierChar(nextChar);
    }

    private static bool IsIdentifierChar(char value) =>
        char.IsLetterOrDigit(value) || value == '_';

    private static string? FindDangerousPattern(string whereClause)
    {
        var dangerousPatterns = new[]
        {
            "UNION", "SELECT", "INSERT", "UPDATE", "DELETE", "DROP", "CREATE", "ALTER",
            "EXEC", "EXECUTE", "xp_", "sp_", "INFORMATION_SCHEMA", "--", "/*", "*/", ";",
            "COPY", "TRUNCATE", "GRANT", "REVOKE", "VACUUM", "ANALYZE", "EXPLAIN",
            "DO", "RAISE", "PERFORM", "NOTIFY", "LISTEN",
            "pg_read_file", "pg_write_file", "pg_ls_dir", "pg_stat_file",
            "lo_import", "lo_export", "pg_sleep", "current_setting", "set_config",
            "pg_advisory_lock", "pg_advisory_unlock",
            "dblink", "dblink_connect",
            "pg_terminate_backend", "pg_cancel_backend",
            "pg_reload_conf",
            "query_to_xml", "xpath",
            "RETURNING",
            "chr", "convert_from"
        };

        foreach (var pattern in dangerousPatterns)
        {
            if (ContainsOutsideQuotes(whereClause, pattern))
            {
                return pattern;
            }
        }

        return null;
    }
}
