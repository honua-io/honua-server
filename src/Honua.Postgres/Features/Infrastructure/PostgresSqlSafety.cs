// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Npgsql;

namespace Honua.Postgres.Features.Infrastructure;

internal static class PostgresSqlSafety
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

    public static NpgsqlCommand CreateReadCommand(NpgsqlConnection connection, string sql)
    {
        ValidateReadOnlySingleStatement(sql);
        return new NpgsqlCommand(sql, connection);
    }

    public static NpgsqlCommand CreateAnalyzeCommand(NpgsqlConnection connection, string tableName)
    {
        var quotedTableName = SchemaSearchPath.ValidateAndQuote(tableName);
        return new NpgsqlCommand($"ANALYZE {quotedTableName}", connection);
    }

    public static NpgsqlCommand CreateAnalyzeCommand(NpgsqlConnection connection, string schemaName, string tableName)
    {
        var quotedSchemaName = SchemaSearchPath.ValidateAndQuote(schemaName);
        var quotedTableName = SchemaSearchPath.ValidateAndQuote(tableName);
        return new NpgsqlCommand($"ANALYZE {quotedSchemaName}.{quotedTableName}", connection);
    }

    public static bool IsReadOnlySingleStatement(string sql)
    {
        try
        {
            ValidateReadOnlySingleStatement(sql);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

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
        foreach (var token in EnumerateSqlTokensOutsideQuotes(sql))
        {
            if (token.Kind == SqlTokenKind.Word)
            {
                return token.Value;
            }
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
