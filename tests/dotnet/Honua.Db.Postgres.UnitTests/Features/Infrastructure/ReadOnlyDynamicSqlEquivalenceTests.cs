// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Db.Postgres.Features.Infrastructure;
using Honua.TestKit.Attributes;

namespace Honua.Db.Postgres.Tests.Features.Infrastructure;

// The span tokenizer must accept, reject and word its rejections exactly as the string-token validator it
// replaced, which is kept below as the oracle.
public sealed class ReadOnlyDynamicSqlEquivalenceTests
{
    private static readonly HashSet<string> MutatingTokens = new(
        "alter call copy create delete drop execute grant insert merge revoke truncate update vacuum".Split(' '),
        StringComparer.OrdinalIgnoreCase);

    public static TheoryData<string> Statements => new()
    {
        "SELECT 1",
        "  select $1 AS value",
        "WITH rows AS (SELECT 1) SELECT * FROM rows",
        "SELECT ';'::text AS semicolon_literal",
        "SELECT 'it''s; -- not a comment' AS quoted",
        "SELECT \"semi;colon\", \"a\"\"b\" FROM \"features\"",
        "SELECT 1;",
        "SELECT 1; DROP TABLE features",
        "SELECT 1 -- trailing comment",
        "SELECT /* hidden */ 1",
        "SELECT 1 - -1, 2 / 3, 4 * 5",
        "SELECT 1 -",
        "WITH deleted AS (DELETE FROM features RETURNING *) SELECT * FROM deleted",
        "SELECT * FROM t WHERE Update_count > 0 OR \"update\" = 1 OR updated = 2",
        "select x FROM t WHERE y = 'DROP'",
        "SELECT VaCuUm",
        "UPDATE features SET attributes = '{}'",
        "DROP TABLE features",
        "-- comment first\nSELECT 1",
        "; SELECT 1",
        "'literal' SELECT 1",
        "SELECT 'unterminated",
        "SELECT \"unterminated",
        "'unterminated before any word",
        "123 456",
        "   ",
        "_private_fn(1)",
        "SELECT ÄÖ, é_1 FROM données",
        "WITH x AS (SELECT 1) INSERT INTO t SELECT * FROM x",
    };

    [UnitTheory]
    [MemberData(nameof(Statements))]
    public void ValidateReadOnlySingleStatement_MatchesTheStringTokenValidator(string sql)
    {
        var expected = Outcome(() => OracleValidate(sql));

        var actual = Outcome(() => PostgresSqlSafety.ValidateReadOnlySingleStatement(sql));

        actual.Should().Be(expected);
    }

    private static string Outcome(Action validate)
    {
        try
        {
            validate();
            return "accepted";
        }
        catch (ArgumentException exception)
        {
            return exception.Message;
        }
    }

    private static void OracleValidate(string sql)
    {
        if (string.IsNullOrWhiteSpace(sql))
        {
            throw new ArgumentException("SQL command text is required.", nameof(sql));
        }

        var first = Tokens(sql).Where(token => token.Kind == "word").Select(token => token.Value).FirstOrDefault()
            ?? throw new ArgumentException("SQL command text does not contain an executable statement.", nameof(sql));
        if (!first.Equals("select", StringComparison.OrdinalIgnoreCase) && !first.Equals("with", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Only read-only SELECT statements are eligible for dynamic execution.", nameof(sql));
        }

        foreach (var (kind, value) in Tokens(sql))
        {
            ThrowIfUnsafe(kind, value, sql);
        }
    }

    private static void ThrowIfUnsafe(string kind, string value, string sql)
    {
        if (kind == ";")
        {
            throw new ArgumentException("SQL command text must contain exactly one statement.", nameof(sql));
        }

        if (kind == "comment")
        {
            throw new ArgumentException("SQL comments are not allowed in dynamic command text.", nameof(sql));
        }

        if (kind == "word" && MutatingTokens.Contains(value))
        {
            throw new ArgumentException("Mutating SQL statements are not allowed in dynamic read command text.", nameof(sql));
        }
    }

    private static IEnumerable<(string Kind, string Value)> Tokens(string sql)
    {
        var index = 0;
        while (index < sql.Length)
        {
            var current = sql[index];
            var next = index + 1 < sql.Length ? sql[index + 1] : '\0';
            if (current is '\'' or '"')
            {
                index = SkipQuoted(sql, index + 1, current);
            }
            else if (current == ';')
            {
                index++;
                yield return (";", ";");
            }
            else if ((current == '-' && next == '-') || (current == '/' && next == '*'))
            {
                index += 2;
                yield return ("comment", string.Empty);
            }
            else if (char.IsLetter(current) || current == '_')
            {
                var start = index++;
                while (index < sql.Length && (char.IsLetterOrDigit(sql[index]) || sql[index] == '_'))
                {
                    index++;
                }

                yield return ("word", sql[start..index]);
            }
            else
            {
                index++;
            }
        }
    }

    private static int SkipQuoted(string sql, int index, char quote)
    {
        for (; index < sql.Length; index++)
        {
            if (sql[index] != quote)
            {
                continue;
            }

            if (index + 1 < sql.Length && sql[index + 1] == quote)
            {
                index++;
                continue;
            }

            return index + 1;
        }

        throw new ArgumentException(
            quote == '\'' ? "SQL command text contains an unterminated string literal." : "SQL command text contains an unterminated quoted identifier.",
            nameof(sql));
    }
}
