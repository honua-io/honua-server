// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Queries.Filters;
using Honua.Core.Queries.Filters.Cql2;
using Xunit;

namespace Honua.Core.Tests.Queries.Filters.Cql2;

public class Cql2ParserTests
{
    private readonly Cql2Parser _parser = new();

    [Fact]
    public void Parse_SimplePropertyComparison_ReturnsCorrectAST()
    {
        // Arrange
        const string cql = "name = 'John'";

        // Act
        var result = _parser.Parse(cql);

        // Assert
        result.Should().BeOfType<BinaryExpression>();
        var binary = (BinaryExpression)result;
        binary.Operator.Should().Be(BinaryOperator.Equal);
        binary.Left.Should().BeOfType<PropertyReference>();
        binary.Right.Should().BeOfType<Literal>();

        var left = (PropertyReference)binary.Left;
        var right = (Literal)binary.Right;
        left.PropertyName.Should().Be("name");
        right.Value.Should().Be("John");
        right.Type.Should().Be(LiteralType.Text);
    }

    [Fact]
    public void Parse_NumericComparison_ReturnsCorrectAST()
    {
        // Arrange
        const string cql = "age >= 18";

        // Act
        var result = _parser.Parse(cql);

        // Assert
        result.Should().BeOfType<BinaryExpression>();
        var binary = (BinaryExpression)result;
        binary.Operator.Should().Be(BinaryOperator.GreaterThanOrEqual);

        var left = (PropertyReference)binary.Left;
        var right = (Literal)binary.Right;
        left.PropertyName.Should().Be("age");
        right.Value.Should().Be(18.0);
        right.Type.Should().Be(LiteralType.Number);
    }

    [Fact]
    public void Parse_BooleanLogic_ReturnsCorrectAST()
    {
        // Arrange
        const string cql = "age >= 18 AND name LIKE 'John%'";

        // Act
        var result = _parser.Parse(cql);

        // Assert
        result.Should().BeOfType<BinaryExpression>();
        var andExpr = (BinaryExpression)result;
        andExpr.Operator.Should().Be(BinaryOperator.And);

        // Left side: age >= 18
        var leftBinary = (BinaryExpression)andExpr.Left;
        leftBinary.Operator.Should().Be(BinaryOperator.GreaterThanOrEqual);

        // Right side: name LIKE 'John%'
        var rightBinary = (BinaryExpression)andExpr.Right;
        rightBinary.Operator.Should().Be(BinaryOperator.Like);
    }

    [Fact]
    public void Parse_NestedParentheses_ReturnsCorrectAST()
    {
        // Arrange
        const string cql = "(age >= 18 AND age < 65) OR retired = TRUE";

        // Act
        var result = _parser.Parse(cql);

        // Assert
        result.Should().BeOfType<BinaryExpression>();
        var orExpr = (BinaryExpression)result;
        orExpr.Operator.Should().Be(BinaryOperator.Or);

        // Left side should be the parenthesized AND expression
        orExpr.Left.Should().BeOfType<BinaryExpression>();
        var leftAnd = (BinaryExpression)orExpr.Left;
        leftAnd.Operator.Should().Be(BinaryOperator.And);
    }

    [Fact]
    public void Parse_NotExpression_ReturnsCorrectAST()
    {
        // Arrange
        const string cql = "NOT deleted = TRUE";

        // Act
        var result = _parser.Parse(cql);

        // Assert
        result.Should().BeOfType<UnaryExpression>();
        var unary = (UnaryExpression)result;
        unary.Operator.Should().Be(UnaryOperator.Not);
        unary.Operand.Should().BeOfType<BinaryExpression>();
    }

    [Fact]
    public void Parse_IsNull_ReturnsCorrectAST()
    {
        // Arrange
        const string cql = "description IS NULL";

        // Act
        var result = _parser.Parse(cql);

        // Assert
        result.Should().BeOfType<UnaryExpression>();
        var unary = (UnaryExpression)result;
        unary.Operator.Should().Be(UnaryOperator.IsNull);
        unary.Operand.Should().BeOfType<PropertyReference>();

        var property = (PropertyReference)unary.Operand;
        property.PropertyName.Should().Be("description");
    }

    [Fact]
    public void Parse_IsNotNull_ReturnsCorrectAST()
    {
        // Arrange
        const string cql = "description IS NOT NULL";

        // Act
        var result = _parser.Parse(cql);

        // Assert
        result.Should().BeOfType<UnaryExpression>();
        var unary = (UnaryExpression)result;
        unary.Operator.Should().Be(UnaryOperator.IsNotNull);
    }

    [Fact]
    public void Parse_InClause_ReturnsCorrectAST()
    {
        // Arrange
        const string cql = "status IN ('active', 'pending', 'completed')";

        // Act
        var result = _parser.Parse(cql);

        // Assert
        result.Should().BeOfType<BinaryExpression>();
        var binary = (BinaryExpression)result;
        binary.Operator.Should().Be(BinaryOperator.In);
        binary.Left.Should().BeOfType<PropertyReference>();
        binary.Right.Should().BeOfType<ValueList>();

        var valueList = (ValueList)binary.Right;
        valueList.Values.Should().HaveCount(3);
        valueList.Values[0].Value.Should().Be("active");
        valueList.Values[1].Value.Should().Be("pending");
        valueList.Values[2].Value.Should().Be("completed");
    }

    [Fact]
    public void Parse_SpatialIntersects_ReturnsCorrectAST()
    {
        // Arrange
        const string cql = "S_INTERSECTS(geom, POINT(1 2))";

        // Act
        var result = _parser.Parse(cql);

        // Assert
        result.Should().BeOfType<SpatialPredicate>();
        var spatial = (SpatialPredicate)result;
        spatial.Operator.Should().Be(SpatialOperator.Intersects);
        spatial.GeometryProperty.PropertyName.Should().Be("geom");
        spatial.Geometry.Srid.Should().Be(4326); // Default SRID if not specified by NTS
        spatial.Geometry.OriginalFormat.Should().StartWith("POINT");
    }

    [Theory]
    [InlineData("name = 'O''Brien'", "O'Brien")] // Escaped single quote
    [InlineData("value = 42.5", 42.5)] // Decimal number
    [InlineData("active = TRUE", true)] // Boolean true
    [InlineData("active = FALSE", false)] // Boolean false
    [InlineData("value = NULL", null)] // Null value
    public void Parse_LiteralValues_ReturnsCorrectValues(string cql, object? expectedValue)
    {
        // Act
        var result = _parser.Parse(cql);

        // Assert
        result.Should().BeOfType<BinaryExpression>();
        var binary = (BinaryExpression)result;
        var literal = (Literal)binary.Right;
        literal.Value.Should().Be(expectedValue);
    }

    [Fact]
    public void Parse_ComplexNestedExpression_ReturnsCorrectAST()
    {
        // Arrange
        const string cql = "(name LIKE 'John%' AND age > 25) OR (city = 'Seattle' AND state = 'WA')";

        // Act
        var result = _parser.Parse(cql);

        // Assert
        result.Should().BeOfType<BinaryExpression>();
        var orExpr = (BinaryExpression)result;
        orExpr.Operator.Should().Be(BinaryOperator.Or);

        // Both sides should be AND expressions
        orExpr.Left.Should().BeOfType<BinaryExpression>();
        orExpr.Right.Should().BeOfType<BinaryExpression>();

        var leftAnd = (BinaryExpression)orExpr.Left;
        var rightAnd = (BinaryExpression)orExpr.Right;

        leftAnd.Operator.Should().Be(BinaryOperator.And);
        rightAnd.Operator.Should().Be(BinaryOperator.And);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Parse_EmptyOrNullInput_ThrowsArgumentException(string? cql)
    {
        // Act & Assert
        var action = () => _parser.Parse(cql!);
        action.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData("name =")]  // Incomplete expression
    [InlineData("AND name = 'John'")] // Invalid start
    [InlineData("name = 'John' AND")] // Incomplete end
    [InlineData("(name = 'John'")] // Unclosed parenthesis
    [InlineData("name = 'John')")] // Unmatched parenthesis
    [InlineData("name == 'John'")] // Invalid operator
    public void Parse_InvalidSyntax_ThrowsArgumentException(string cql)
    {
        // Act & Assert
        var action = () => _parser.Parse(cql);
        action.Should().Throw<ArgumentException>();
    }
}
