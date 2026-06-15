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

    [Fact]
    public void Parse_CastAsType_ReturnsCastFunctionCall()
    {
        var expression = _parser.Parse("CAST(code AS INTEGER) = 5");

        var binary = (BinaryExpression)expression;
        var function = (FunctionCall)binary.Left;
        function.FunctionName.Should().Be("CAST");
        function.Arguments.Should().HaveCount(2);
        ((PropertyReference)function.Arguments[0]).PropertyName.Should().Be("code");

        var typeLiteral = (Literal)function.Arguments[1];
        typeLiteral.Type.Should().Be(LiteralType.Text);
        typeLiteral.Value.Should().Be("INTEGER");
    }

    [Fact]
    public void Parse_CastAsTwoWordType_PreservesFullTypeName()
    {
        var expression = _parser.Parse("CAST(value AS DOUBLE PRECISION) > 1.5");

        var binary = (BinaryExpression)expression;
        var function = (FunctionCall)binary.Left;
        function.FunctionName.Should().Be("CAST");
        ((Literal)function.Arguments[1]).Value.Should().Be("DOUBLE PRECISION");
    }

    [Fact]
    public void Parse_CastMissingAsKeyword_ThrowsArgumentException()
    {
        var act = () => _parser.Parse("CAST(code INTEGER) = 5");

        act.Should().Throw<ArgumentException>()
            .WithMessage("*AS*");
    }

    [Fact]
    public void Parse_ExtractYearFrom_MapsToYearFunction()
    {
        var expression = _parser.Parse("EXTRACT(YEAR FROM created) >= 2024");

        var binary = (BinaryExpression)expression;
        var function = (FunctionCall)binary.Left;
        function.FunctionName.Should().Be("YEAR");
        function.Arguments.Should().HaveCount(1);
        ((PropertyReference)function.Arguments[0]).PropertyName.Should().Be("created");
    }

    [Fact]
    public void Parse_ExtractUnsupportedField_ThrowsArgumentException()
    {
        var act = () => _parser.Parse("EXTRACT(WEEK FROM created) >= 1");

        act.Should().Throw<ArgumentException>()
            .WithMessage("*Unsupported EXTRACT field*");
    }

    [Fact]
    public void Parse_SubstringFromFor_MapsToSubstringFunction()
    {
        var expression = _parser.Parse("SUBSTRING(name FROM 1 FOR 3) = 'abc'");

        var binary = (BinaryExpression)expression;
        var function = (FunctionCall)binary.Left;
        function.FunctionName.Should().Be("SUBSTRING");
        function.Arguments.Should().HaveCount(3);
        ((PropertyReference)function.Arguments[0]).PropertyName.Should().Be("name");
        ((Literal)function.Arguments[1]).Value.Should().Be(1L);
        ((Literal)function.Arguments[2]).Value.Should().Be(3L);
    }

    [Fact]
    public void Parse_SubstringFromWithoutFor_OmitsLengthArgument()
    {
        var expression = _parser.Parse("SUBSTRING(name FROM 2) = 'b'");

        var binary = (BinaryExpression)expression;
        var function = (FunctionCall)binary.Left;
        function.FunctionName.Should().Be("SUBSTRING");
        function.Arguments.Should().HaveCount(2);
    }

    [Fact]
    public void Parse_SubstringCommaForm_StillSupported()
    {
        var expression = _parser.Parse("SUBSTRING(name, 1, 3) = 'abc'");

        var binary = (BinaryExpression)expression;
        var function = (FunctionCall)binary.Left;
        function.FunctionName.Should().Be("SUBSTRING");
        function.Arguments.Should().HaveCount(3);
    }

    [Fact]
    public void Parse_PositionIn_MapsToPositionFunction()
    {
        var expression = _parser.Parse("POSITION('x' IN name) > 0");

        var binary = (BinaryExpression)expression;
        var function = (FunctionCall)binary.Left;
        function.FunctionName.Should().Be("POSITION");
        function.Arguments.Should().HaveCount(2);
        ((Literal)function.Arguments[0]).Value.Should().Be("x");
        ((PropertyReference)function.Arguments[1]).PropertyName.Should().Be("name");
    }

    [Fact]
    public void Parse_GenericFunction_StillUsesCommaArguments()
    {
        var expression = _parser.Parse("UPPER(name) = 'ABC'");

        var binary = (BinaryExpression)expression;
        var function = (FunctionCall)binary.Left;
        function.FunctionName.Should().Be("UPPER");
        function.Arguments.Should().HaveCount(1);
    }

    [Fact]
    public void Parse_LikeEscapeClause_ThrowsArgumentException()
    {
        // LIKE ... ESCAPE is intentionally re-deferred (no ESCAPE node in the shared AST);
        // it must be rejected rather than silently dropped.
        var act = () => _parser.Parse("name LIKE '%50!%%' ESCAPE '!'");

        act.Should().Throw<ArgumentException>();
    }
}
