// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Linq;
using FluentAssertions;
using Honua.Core.Queries.Filters;
using Honua.Core.Queries.Filters.GeoServicesSql;

namespace Honua.Core.Tests.Queries.Filters.GeoServicesSql;

public class GeoServicesSqlParserTests
{
    private readonly GeoServicesSqlParser _parser = new();

    [Fact]
    public void Parse_SimpleEquality_ReturnsBinaryExpression()
    {
        var expression = _parser.Parse("name = 'Test Feature'");

        expression.Should().BeOfType<BinaryExpression>();
        var binary = (BinaryExpression)expression;
        binary.Operator.Should().Be(BinaryOperator.Equal);

        var left = (PropertyReference)binary.Left;
        var right = (Literal)binary.Right;

        left.PropertyName.Should().Be("name");
        right.Type.Should().Be(LiteralType.Text);
        right.Value.Should().Be("Test Feature");
    }

    [Fact]
    public void Parse_InList_ReturnsValueList()
    {
        var expression = _parser.Parse("category IN ('test', 'sample')");

        expression.Should().BeOfType<BinaryExpression>();
        var binary = (BinaryExpression)expression;
        binary.Operator.Should().Be(BinaryOperator.In);

        binary.Right.Should().BeOfType<ValueList>();
        var values = ((ValueList)binary.Right).Values;
        values.Should().HaveCount(2);
    }

    [Fact]
    public void Parse_Between_ReturnsRangeExpression()
    {
        var expression = _parser.Parse("value BETWEEN 1 AND 5");

        expression.Should().BeOfType<BinaryExpression>();
        var binary = (BinaryExpression)expression;
        binary.Operator.Should().Be(BinaryOperator.And);
    }

    [Fact]
    public void Parse_WithNestedParenthesesBeyondLimit_ThrowsArgumentException()
    {
        var filter = string.Concat(Enumerable.Repeat("(", FilterParserGuard.MaxExpressionDepth + 1)) +
            "name = 'deep'" +
            string.Concat(Enumerable.Repeat(")", FilterParserGuard.MaxExpressionDepth + 1));

        var act = () => _parser.Parse(filter);

        act.Should().Throw<ArgumentException>()
            .WithMessage($"*maximum nesting depth of {FilterParserGuard.MaxExpressionDepth}*");
    }

    [Fact]
    public void Parse_DateLiteral_ProducesDateOnly()
    {
        var expression = _parser.Parse("timestamp >= #2024-01-01#");

        var binary = (BinaryExpression)expression;
        var literal = (Literal)binary.Right;
        literal.Type.Should().Be(LiteralType.Date);
        literal.Value.Should().Be(DateOnly.Parse("2024-01-01", CultureInfo.InvariantCulture));
    }

    [Fact]
    public void Parse_CurrentDate_ReturnsFunctionCall()
    {
        var expression = _parser.Parse("timestamp >= CURRENT_DATE");

        var binary = (BinaryExpression)expression;
        binary.Right.Should().BeOfType<FunctionCall>();

        var function = (FunctionCall)binary.Right;
        function.FunctionName.Should().Be("CURRENT_DATE");
        function.Arguments.Should().BeEmpty();
    }
}
