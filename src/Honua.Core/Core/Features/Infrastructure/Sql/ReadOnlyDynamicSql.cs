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
        while (tokens.MoveNext(out var kind, out var word))
        {
            if (kind == SqlTokenKind.StatementSeparator)
            {
                throw new ArgumentException("SQL command text must contain exactly one statement.", nameof(sql));
            }

            if (kind == SqlTokenKind.Comment)
            {
                throw new ArgumentException("SQL comments are not allowed in dynamic command text.", nameof(sql));
            }

            if (kind == SqlTokenKind.Word && MutatingTokenLookup.Contains(sql.AsSpan(word.Start, word.Length)))
            {
                throw new ArgumentException("Mutating SQL statements are not allowed in dynamic read command text.", nameof(sql));
            }
        }
    }

    // The first word, scanning (and validating literals) only up to it.
    private static ReadOnlySpan<char> GetFirstToken(string sql)
    {
        var tokens = new SqlTokenizer(sql);
        while (tokens.MoveNext(out var kind, out var word))
        {
            if (kind == SqlTokenKind.Word)
            {
                return sql.AsSpan(word.Start, word.Length);
            }
        }

        throw new ArgumentException("SQL command text does not contain an executable statement.", nameof(sql));
    }

    // Tokens outside quoted literals and identifiers, as offsets into the statement, so a scan allocates nothing.
    private struct SqlTokenizer(string sql)
    {
        private int _index;

        public bool MoveNext(out SqlTokenKind kind, out (int Start, int Length) word)
        {
            word = default;
            while (_index < sql.Length)
            {
                var current = sql[_index];
                if (current == '\'')
                {
                    _index = SkipSingleQuotedLiteral(sql, _index + 1);
                    continue;
                }

                if (current == '"')
                {
                    _index = SkipDoubleQuotedIdentifier(sql, _index + 1);
                    continue;
                }

                if (TryReadPunctuation(current, out kind))
                {
                    return true;
                }

                if (char.IsLetter(current) || current == '_')
                {
                    var start = _index++;
                    while (_index < sql.Length && (char.IsLetterOrDigit(sql[_index]) || sql[_index] == '_'))
                    {
                        _index++;
                    }

                    kind = SqlTokenKind.Word;
                    word = (start, _index - start);
                    return true;
                }

                _index++;
            }

            kind = default;
            return false;
        }

        private bool TryReadPunctuation(char current, out SqlTokenKind kind)
        {
            var next = _index + 1 < sql.Length ? sql[_index + 1] : '\0';
            if (current == ';')
            {
                kind = SqlTokenKind.StatementSeparator;
                _index++;
                return true;
            }

            if ((current == '-' && next == '-') || (current == '/' && next == '*'))
            {
                kind = SqlTokenKind.Comment;
                _index += 2;
                return true;
            }

            kind = default;
            return false;
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
}
