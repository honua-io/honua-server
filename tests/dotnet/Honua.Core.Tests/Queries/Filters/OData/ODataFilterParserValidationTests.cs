// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Queries.Filters;
using Honua.Core.Queries.Filters.OData;

namespace Honua.Core.Tests.Queries.Filters.OData;

public sealed class ODataFilterParserValidationTests
{
    private readonly ODataFilterParser _parser = new();

    [Trait("Tier", "Fast")]
    [Theory]
    [InlineData("1 add 2")]
    [InlineData("1 div 2")]
    [InlineData("-1")]
    [InlineData("tolower('abc')")]
    [InlineData("length(name)")]
    [InlineData("year(created_at)")]
    public void Parse_NonBooleanRootExpression_ThrowsParseException(string filter)
    {
        var act = () => _parser.Parse(filter);

        act.Should().Throw<ODataFilterParseException>();
    }

    [Trait("Tier", "Fast")]
    [Theory]
    [InlineData("not 1")]
    [InlineData("not 'abc'")]
    [InlineData("not (1 add 2)")]
    [InlineData("status eq 'open' and 1")]
    [InlineData("status eq 'open' or 'abc'")]
    [InlineData("1 and status eq 'open'")]
    [InlineData("(1 add 2) or status eq 'open'")]
    public void Parse_NonBooleanLogicalOperand_ThrowsParseException(string filter)
    {
        var act = () => _parser.Parse(filter);

        act.Should().Throw<ODataFilterParseException>();
    }

    [Trait("Tier", "Fast")]
    [Theory]
    [InlineData("1 div 0 eq 1")]
    [InlineData("1 div 0.0 eq 1")]
    [InlineData("1 mod 0 eq 0")]
    [InlineData("1 div -0 eq 1")]
    [InlineData("1 div -(-0) eq 1")]
    public void Parse_ZeroDivisor_ThrowsParseException(string filter)
    {
        var act = () => _parser.Parse(filter);

        act.Should().Throw<ODataFilterParseException>();
    }

    [Trait("Tier", "Fast")]
    [Theory]
    [InlineData("contains(123, 'a')")]
    [InlineData("contains(name, 123)")]
    [InlineData("startswith(123, 'a')")]
    [InlineData("endswith(name, 42)")]
    [InlineData("substringof(123, name)")]
    public void Parse_StringPredicateWithNonStringLiteral_ThrowsParseException(string filter)
    {
        var act = () => _parser.Parse(filter);

        act.Should().Throw<ODataFilterParseException>();
    }

    [Trait("Tier", "Fast")]
    [Fact]
    public void ParseOrderExpression_ArithmeticExpression_RemainsAllowed()
    {
        var expr = _parser.ParseOrderExpression("price add 1");

        expr.Should().BeOfType<BinaryExpression>()
            .Which.Operator.Should().Be(BinaryOperator.Add);
    }
}
