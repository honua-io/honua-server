// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Infrastructure.Sql;

/// <summary>
/// Rejects dynamic SQL that is not one read-only <c>SELECT</c> or <c>WITH</c> statement.
/// Callers that interpolate a predicate compose the finished statement and run it through
/// <see cref="ValidateReadOnlySingleStatement"/> before execution.
/// </summary>
internal static class ReadOnlyDynamicSql
{
    private static readonly HashSet<string> MutatingTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "alter",
        "call",
        "copy",
        "create",
        "delete",
        "drop",
        "execute",
        "grant",
        "insert",
        "merge",
        "revoke",
        "truncate",
        "update",
        "vacuum"
    };

    public static void ValidateReadOnlySingleStatement(string sql)
    {
        if (string.IsNullOrWhiteSpace(sql))
        {
            throw new ArgumentException("SQL command text is required.", nameof(sql));
        }

        var firstToken = GetFirstToken(sql);
        if (!firstToken.Equals("select", StringComparison.OrdinalIgnoreCase) &&
            !firstToken.Equals("with", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Only read-only SELECT statements are eligible for dynamic execution.", nameof(sql));
        }

        foreach (var token in EnumerateSqlTokensOutsideQuotes(sql))
        {
            if (token.Kind == SqlTokenKind.StatementSeparator)
            {
                throw new ArgumentException("SQL command text must contain exactly one statement.", nameof(sql));
            }

            if (token.Kind == SqlTokenKind.Comment)
            {
                throw new ArgumentException("SQL comments are not allowed in dynamic command text.", nameof(sql));
            }

            if (token.Kind == SqlTokenKind.Word && MutatingTokens.Contains(token.Value))
            {
                throw new ArgumentException("Mutating SQL statements are not allowed in dynamic read command text.", nameof(sql));
            }
        }
    }

    private static string GetFirstToken(string sql)
    {
        // Not rewritten as .Where(...).FirstOrDefault(): this needs to throw when no Word token
        // exists, and SqlToken is a struct where a default() sentinel would be ambiguous with a
        // genuine empty-value token.
        foreach (var token in (EnumerateSqlTokensOutsideQuotes(sql)).Where(token => token.Kind == SqlTokenKind.Word))
        {
            return token.Value;
        }

        throw new ArgumentException("SQL command text does not contain an executable statement.", nameof(sql));
    }

    private static IEnumerable<SqlToken> EnumerateSqlTokensOutsideQuotes(string sql)
    {
        var index = 0;
        while (index < sql.Length)
        {
            var current = sql[index];

            if (current == '\'')
            {
                index = SkipSingleQuotedLiteral(sql, index + 1);
                continue;
            }

            if (current == '"')
            {
                index = SkipDoubleQuotedIdentifier(sql, index + 1);
                continue;
            }

            if (current == ';')
            {
                yield return new SqlToken(SqlTokenKind.StatementSeparator, ";");
                index++;
                continue;
            }

            if (current == '-' && index + 1 < sql.Length && sql[index + 1] == '-')
            {
                yield return new SqlToken(SqlTokenKind.Comment, "--");
                index += 2;
                continue;
            }

            if (current == '/' && index + 1 < sql.Length && sql[index + 1] == '*')
            {
                yield return new SqlToken(SqlTokenKind.Comment, "/*");
                index += 2;
                continue;
            }

            if (char.IsLetter(current) || current == '_')
            {
                var start = index++;
                while (index < sql.Length && (char.IsLetterOrDigit(sql[index]) || sql[index] == '_'))
                {
                    index++;
                }

                yield return new SqlToken(SqlTokenKind.Word, sql[start..index]);
                continue;
            }

            index++;
        }
    }

    private static int SkipSingleQuotedLiteral(string sql, int index)
    {
        while (index < sql.Length)
        {
            if (sql[index] == '\'')
            {
                if (index + 1 < sql.Length && sql[index + 1] == '\'')
                {
                    index += 2;
                    continue;
                }

                return index + 1;
            }

            index++;
        }

        throw new ArgumentException("SQL command text contains an unterminated string literal.", nameof(sql));
    }

    private static int SkipDoubleQuotedIdentifier(string sql, int index)
    {
        while (index < sql.Length)
        {
            if (sql[index] == '"')
            {
                if (index + 1 < sql.Length && sql[index + 1] == '"')
                {
                    index += 2;
                    continue;
                }

                return index + 1;
            }

            index++;
        }

        throw new ArgumentException("SQL command text contains an unterminated quoted identifier.", nameof(sql));
    }

    private enum SqlTokenKind
    {
        Word,
        StatementSeparator,
        Comment
    }

    private readonly record struct SqlToken(SqlTokenKind Kind, string Value);
}
