// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Infrastructure.Sql;

// Callers that interpolate a predicate must validate the finished statement before execution.
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

    private static readonly HashSet<string>.AlternateLookup<ReadOnlySpan<char>> MutatingTokenLookup =
        MutatingTokens.GetAlternateLookup<ReadOnlySpan<char>>();

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

        var tokens = new SqlTokenizer(sql);
        for (var token = tokens.Next(); token.Kind != SqlTokenKind.End; token = tokens.Next())
        {
            if (token.Kind == SqlTokenKind.StatementSeparator)
            {
                throw new ArgumentException("SQL command text must contain exactly one statement.", nameof(sql));
            }

            if (token.Kind == SqlTokenKind.Comment)
            {
                throw new ArgumentException("SQL comments are not allowed in dynamic command text.", nameof(sql));
            }

            if (token.Kind == SqlTokenKind.Word && MutatingTokenLookup.Contains(sql.AsSpan(token.Start, token.Length)))
            {
                throw new ArgumentException("Mutating SQL statements are not allowed in dynamic read command text.", nameof(sql));
            }
        }
    }

    // The first word, scanning (and validating literals) only up to it.
    private static ReadOnlySpan<char> GetFirstToken(string sql)
    {
        var tokens = new SqlTokenizer(sql);
        for (var token = tokens.Next(); token.Kind != SqlTokenKind.End; token = tokens.Next())
        {
            if (token.Kind == SqlTokenKind.Word)
            {
                return sql.AsSpan(token.Start, token.Length);
            }
        }

        throw new ArgumentException("SQL command text does not contain an executable statement.", nameof(sql));
    }

    // Tokens outside quoted literals and identifiers, as offsets into the statement, so a scan allocates nothing.
    private struct SqlTokenizer(string sql)
    {
        private int _index;

        public SqlToken Next()
        {
            while (_index < sql.Length)
            {
                var current = sql[_index];
                if (current == '\'')
                {
                    _index = SkipSingleQuotedLiteral(sql, _index + 1);
                }
                else if (current == '"')
                {
                    _index = SkipDoubleQuotedIdentifier(sql, _index + 1);
                }
                else if (ReadPunctuation(current) is { } punctuation)
                {
                    return punctuation;
                }
                else if (char.IsLetter(current) || current == '_')
                {
                    return ReadWord();
                }
                else
                {
                    _index++;
                }
            }

            return new SqlToken(SqlTokenKind.End, _index, 0);
        }

        private SqlToken? ReadPunctuation(char current)
        {
            var start = _index;
            var next = _index + 1 < sql.Length ? sql[_index + 1] : '\0';
            if (current == ';')
            {
                _index++;
                return new SqlToken(SqlTokenKind.StatementSeparator, start, 1);
            }

            if ((current == '-' && next == '-') || (current == '/' && next == '*'))
            {
                _index += 2;
                return new SqlToken(SqlTokenKind.Comment, start, 2);
            }

            return null;
        }

        private SqlToken ReadWord()
        {
            var start = _index++;
            while (_index < sql.Length && (char.IsLetterOrDigit(sql[_index]) || sql[_index] == '_'))
            {
                _index++;
            }

            return new SqlToken(SqlTokenKind.Word, start, _index - start);
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
        End,
        Word,
        StatementSeparator,
        Comment
    }

    private readonly record struct SqlToken(SqlTokenKind Kind, int Start, int Length);
}
