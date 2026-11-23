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
    [InlineData("SRID=0;POINT(0 0)")]
    [InlineData("SRID=-1;POINT(0 0)")]
    public void Parse_NonPositiveGeometryLiteralSrid_ThrowsParseException(string literal)
    {
        var act = () => _parser.Parse($"geo.intersects(Geometry, geography'{literal}')");

        act.Should().Throw<ODataFilterParseException>().WithMessage("*SRID*");
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
    [Theory]
    [InlineData("status eq 'open' or null")]
    [InlineData("status eq 'open' and null")]
    [InlineData("not null")]
    public void Parse_NullBooleanOperand_RemainsAllowed(string filter)
    {
        var expr = _parser.Parse(filter);

        expr.Should().NotBeNull();
    }

    [Trait("Tier", "Fast")]
    [Fact]
    public void Parse_BareNullRoot_ThrowsParseException()
    {
        var act = () => _parser.Parse("null");

        act.Should().Throw<ODataFilterParseException>();
    }

    [Trait("Tier", "Fast")]
    [Theory]
    [InlineData("1e-29")]
    [InlineData("0.000000000000000000000000000001")]
    public void IsZeroNumericLiteral_SmallNonZeroTextualLiteral_ReturnsFalse(string text)
    {
        var literal = new Literal(text, LiteralType.Number);

        FilterPredicateValidation.IsZeroNumericLiteral(literal).Should().BeFalse();
    }

    [Trait("Tier", "Fast")]
    [Theory]
    [InlineData("0e-29")]
    [InlineData("-0.00e+10")]
    [InlineData("0")]
    public void IsZeroNumericLiteral_ZeroTextualLiteral_ReturnsTrue(string text)
    {
        var literal = new Literal(text, LiteralType.Number);

        FilterPredicateValidation.IsZeroNumericLiteral(literal).Should().BeTrue();
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
