// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Queries.Filters;
using Honua.TestKit.Attributes;

namespace Honua.Core.Tests.Queries.Filters;

public sealed class SqlLikePatternTests
{
    [UnitTheory]
    [InlineData("parcel-17", "parcel%")]
    [InlineData("parcel-17", "parcel-1_")]
    [InlineData("a.b(c)", "a.b(%)")]
    public void IsMatch_PercentAndUnderscoreAreWildcards(string input, string pattern)
    {
        SqlLikePattern.IsMatch(input, pattern).Should().BeTrue();
    }

    [UnitTheory]
    [InlineData("parcel-17", "parcel-_")]
    [InlineData("axb", "a.b")]
    [InlineData("aaa", "a*")]
    public void IsMatch_OtherCharactersAreLiteral(string input, string pattern)
    {
        SqlLikePattern.IsMatch(input, pattern).Should().BeFalse();
    }

    [UnitTest]
    public void IsMatch_IsCaseSensitiveAndIsMatchIgnoringCaseIsNot()
    {
        SqlLikePattern.IsMatch("Parcel", "parcel").Should().BeFalse();
        SqlLikePattern.IsMatchIgnoringCase("Parcel", "parcel").Should().BeTrue();
    }

    [UnitTest]
    public void IsMatch_WildcardHeavyPatternOnLongInput_Completes()
    {
        var input = new string('a', 50_000);
        var pattern = string.Concat(Enumerable.Repeat("%a", SqlLikePattern.MaxWildcards - 1)) + "b";

        SqlLikePattern.IsMatchIgnoringCase(input, pattern).Should().BeFalse();
    }

    [UnitTest]
    public void IsMatch_PatternAtTheLimits_IsEvaluated()
    {
        var wildcards = new string('%', SqlLikePattern.MaxWildcards);
        var longest = new string('a', SqlLikePattern.MaxPatternLength);

        SqlLikePattern.IsMatch("anything", wildcards).Should().BeTrue();
        SqlLikePattern.IsMatch(longest, longest).Should().BeTrue();
    }

    [UnitTest]
    public void IsMatch_PatternOverTheLimits_IsRefused()
    {
        var wildcards = new string('_', SqlLikePattern.MaxWildcards + 1);
        var longest = new string('a', SqlLikePattern.MaxPatternLength + 1);

        FluentActions.Invoking(() => SqlLikePattern.IsMatch("a", wildcards))
            .Should().Throw<UnevaluableLikePatternException>();
        FluentActions.Invoking(() => SqlLikePattern.IsMatch("a", longest))
            .Should().Throw<UnevaluableLikePatternException>();
    }
}
