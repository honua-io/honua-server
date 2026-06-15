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

    [Theory]
    [InlineData("EXTRACT(YEAR FROM hire_date) = 2024", "YEAR")]
    [InlineData("EXTRACT(month FROM hire_date) = 6", "MONTH")]
    [InlineData("EXTRACT(Day FROM hire_date) = 14", "DAY")]
    [InlineData("EXTRACT(HOUR FROM event_time) > 12", "HOUR")]
    [InlineData("EXTRACT(MINUTE FROM event_time) = 30", "MINUTE")]
    [InlineData("EXTRACT(SECOND FROM event_time) = 0", "SECOND")]
    public void Parse_Extract_MapsToDatePartFunction(string clause, string expectedFunction)
    {
        var expression = _parser.Parse(clause);

        var binary = (BinaryExpression)expression;
        binary.Left.Should().BeOfType<FunctionCall>();

        var function = (FunctionCall)binary.Left;
        function.FunctionName.Should().Be(expectedFunction);
        function.Arguments.Should().HaveCount(1);
        function.Arguments[0].Should().BeOfType<PropertyReference>();
        ((PropertyReference)function.Arguments[0]).PropertyName.Should().Be(
            clause.Contains("hire_date", StringComparison.OrdinalIgnoreCase) ? "hire_date" : "event_time");
    }

    [Fact]
    public void Parse_Extract_WithUnsupportedField_Throws()
    {
        var act = () => _parser.Parse("EXTRACT(QUARTER FROM hire_date) = 1");

        act.Should().Throw<ArgumentException>()
            .WithMessage("*Unsupported EXTRACT field*");
    }

    [Fact]
    public void Parse_Extract_MissingFrom_Throws()
    {
        var act = () => _parser.Parse("EXTRACT(YEAR hire_date) = 2024");

        act.Should().Throw<ArgumentException>()
            .WithMessage("*Expected FROM*");
    }

    [Theory]
    [InlineData("CAST(code AS INTEGER) = 42", "INTEGER")]
    [InlineData("CAST(amount AS NUMERIC) > 3.14", "NUMERIC")]
    [InlineData("CAST(label AS TEXT) = 'abc'", "TEXT")]
    [InlineData("CAST(ratio AS DOUBLE PRECISION) > 1.0", "DOUBLE PRECISION")]
    public void Parse_CastWithAs_EmitsCastFunctionWithTypeLiteral(string clause, string expectedType)
    {
        var expression = _parser.Parse(clause);

        var binary = (BinaryExpression)expression;
        binary.Left.Should().BeOfType<FunctionCall>();

        var function = (FunctionCall)binary.Left;
        function.FunctionName.Should().Be("CAST");
        function.Arguments.Should().HaveCount(2);
        function.Arguments[0].Should().BeOfType<PropertyReference>();

        function.Arguments[1].Should().BeOfType<Literal>();
        var typeLiteral = (Literal)function.Arguments[1];
        typeLiteral.Type.Should().Be(LiteralType.Text);
        typeLiteral.Value.Should().Be(expectedType);
    }

    [Fact]
    public void Parse_CastMissingAs_Throws()
    {
        var act = () => _parser.Parse("CAST(code, INTEGER) = 42");

        act.Should().Throw<ArgumentException>()
            .WithMessage("*Expected AS*");
    }

    [Fact]
    public void Parse_StringConcatenation_MapsToConcatFunction()
    {
        var expression = _parser.Parse("first_name || ' ' || last_name = 'John Doe'");

        var binary = (BinaryExpression)expression;
        binary.Left.Should().BeOfType<FunctionCall>();

        var function = (FunctionCall)binary.Left;
        function.FunctionName.Should().Be("CONCAT");
        function.Arguments.Should().HaveCount(3);
        function.Arguments[0].Should().BeOfType<PropertyReference>();
        function.Arguments[1].Should().BeOfType<Literal>();
        function.Arguments[2].Should().BeOfType<PropertyReference>();
    }

    [Fact]
    public void Parse_SinglePipe_Throws()
    {
        var act = () => _parser.Parse("name = 'a' | 'b'");

        act.Should().Throw<ArgumentException>()
            .WithMessage("*Unexpected '|'*");
    }
}
