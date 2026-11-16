// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Queries.Filters;
using Honua.TestKit.Attributes;

namespace Honua.Core.Tests.Queries.Filters;

public sealed class LegacyWhereClauseParserTests
{
    [UnitTheory]
    [InlineData("name = 'it''s AND safe'")]
    [InlineData("population >= -12.5")]
    [InlineData("name NOT LIKE 'a%'")]
    [InlineData("name IS NOT NULL")]
    [InlineData("TRUE")]
    [InlineData("1=1")]
    public void Parse_CommonSyntax_PreservesTokensAcrossGrammars(string clause)
    {
        var expressions = WhereClauseSplitter.SplitOnAnd(clause);
        var basic = Assert.Single(LegacyWhereClauseParser.Parse(expressions, LegacyWhereGrammar.Basic));
        foreach (var grammar in new[] { LegacyWhereGrammar.Warehouse, LegacyWhereGrammar.Postgres })
        {
            var parsed = Assert.Single(LegacyWhereClauseParser.Parse(expressions, grammar));
            Assert.Equal(basic.Kind, parsed.Kind);
            Assert.Equal(basic.Match.Value, parsed.Match.Value);
        }
    }

    [UnitTest]
    public void Parse_ProviderExtensions_RetainsGrammarBoundaries()
    {
        Assert.Throws<ArgumentException>(() => LegacyWhereClauseParser.Parse(["id IN (1, 'two')"], LegacyWhereGrammar.Basic).ToArray());
        Assert.Single(LegacyWhereClauseParser.Parse(["id IN (1, 'two')"], LegacyWhereGrammar.Warehouse));
        Assert.Throws<ArgumentException>(() => LegacyWhereClauseParser.Parse(["attributes->>'name' = 'a'"], LegacyWhereGrammar.Warehouse).ToArray());
        Assert.Single(LegacyWhereClauseParser.Parse(["attributes->>'name' = 'a'"], LegacyWhereGrammar.Postgres));
        Assert.Throws<ArgumentException>(() => LegacyWhereClauseParser.Parse(["1 = 1"], LegacyWhereGrammar.Basic).ToArray());
        Assert.Throws<ArgumentException>(() => LegacyWhereClauseParser.Parse(["1 = 1"], LegacyWhereGrammar.Warehouse).ToArray());
        Assert.Single(LegacyWhereClauseParser.Parse(["1 = 1"], LegacyWhereGrammar.Postgres));
    }

    [UnitTest]
    public void Parse_StorageMappedSyntax_RetainsWholeClauseTrueHandling()
    {
        foreach (var clause in new[] { "name = 'a'", "id IN (1, 'two')", "name IS NULL" })
        {
            Assert.Single(LegacyWhereClauseParser.Parse([clause], LegacyWhereGrammar.StorageMapped));
        }
        foreach (var clause in new[] { "TRUE", "1=1", "attributes->>'name' = 'a'" })
        {
            Assert.Throws<ArgumentException>(() => LegacyWhereClauseParser.Parse([clause], LegacyWhereGrammar.StorageMapped).ToArray());
        }
        Assert.True(LegacyWhereClauseParser.IsTrueLiteral("1 = 1"));
    }

    [UnitTheory]
    [InlineData("")]
    [InlineData("name = 'a' OR id = 2")]
    [InlineData("id = 1; DROP TABLE features")]
    [InlineData("id IN (1,)")]
    [InlineData("id = @p0")]
    [InlineData("name = 'unterminated")]
    public void Parse_UnsupportedSyntax_IsRejectedByEveryGrammar(string clause)
    {
        foreach (var grammar in Enum.GetValues<LegacyWhereGrammar>())
        {
            Assert.Throws<ArgumentException>(() => LegacyWhereClauseParser.Parse(WhereClauseSplitter.SplitOnAnd(clause), grammar).ToArray());
        }
    }

    [UnitTest]
    public void Parse_LaterInvalidTerm_LeavesEarlierTermAvailable()
    {
        using var terms = LegacyWhereClauseParser.Parse(["id = 1", "invalid"], LegacyWhereGrammar.Basic).GetEnumerator();
        Assert.True(terms.MoveNext());
        Assert.Equal("id", terms.Current.Match.Groups["field"].Value);
        Assert.Throws<ArgumentException>(() => terms.MoveNext());
    }

    [UnitTest]
    public void Values_OverflowAndLikeText_RetainProviderPolicies()
    {
        const string overflow = "999999999999999999999999999999999999999999";
        Assert.Equal(overflow, LegacyWhereClauseParser.ParseValueToken(overflow));
        Assert.Throws<ArgumentException>(() => LegacyWhereClauseParser.ParseValueToken(overflow, LegacyWhereValuePolicy.NumericRequired));
        Assert.Equal(12.5m, LegacyWhereClauseParser.ParseValueToken("12.5"));
        Assert.Equal("12.5", LegacyWhereClauseParser.ParseValueToken("12.5", LegacyWhereValuePolicy.Text));
        Assert.Equal("it's", LegacyWhereClauseParser.ParseValueToken("'it''s'"));
        Assert.Equal("!=", LegacyWhereClauseParser.NormalizeOperator("<>", LegacyWhereGrammar.Basic));
        Assert.Equal("<>", LegacyWhereClauseParser.NormalizeOperator("!=", LegacyWhereGrammar.Warehouse));
    }
}
