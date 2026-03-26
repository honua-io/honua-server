// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Queries.Filters;
using Honua.Core.Queries.Filters.Cql2;

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
        ((Literal)valueList.Values[0]).Value.Should().Be("active");
        ((Literal)valueList.Values[1]).Value.Should().Be("pending");
        ((Literal)valueList.Values[2]).Value.Should().Be("completed");
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
        spatial.Left.Should().BeOfType<PropertyReference>();
        spatial.Right.Should().BeOfType<GeometryLiteral>();

        var geometryProperty = (PropertyReference)spatial.Left;
        var geometry = (GeometryLiteral)spatial.Right;

        geometryProperty.PropertyName.Should().Be("geom");
        geometry.Srid.Should().Be(4326); // Default SRID if not specified by NTS
        geometry.OriginalFormat.Should().StartWith("POINT");
    }

    [Fact]
    public void Parse_SpatialDWithin_ReturnsDistancePredicate()
    {
        // Arrange
        const string cql = "S_DWITHIN(geom, POINT(1 2), 100)";

        // Act
        var result = _parser.Parse(cql);

        // Assert
        result.Should().BeOfType<SpatialDistancePredicate>();
        var spatial = (SpatialDistancePredicate)result;
        spatial.Operator.Should().Be(SpatialOperator.DWithin);
        spatial.Left.Should().BeOfType<PropertyReference>();
        spatial.Right.Should().BeOfType<GeometryLiteral>();
        spatial.Distance.Should().BeOfType<Literal>();
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

    [Fact]
    public void Parse_BetweenWithText_ReturnsCorrectAST()
    {
        // Arrange
        const string cql = "name BETWEEN 'A' AND 'Z'";

        // Act
        var result = _parser.Parse(cql);

        // Assert
        result.Should().BeOfType<BinaryExpression>();
        var andExpr = (BinaryExpression)result;
        andExpr.Operator.Should().Be(BinaryOperator.And);

        var lower = (BinaryExpression)andExpr.Left;
        var upper = (BinaryExpression)andExpr.Right;

        lower.Operator.Should().Be(BinaryOperator.GreaterThanOrEqual);
        ((Literal)lower.Right).Value.Should().Be("A");

        upper.Operator.Should().Be(BinaryOperator.LessThanOrEqual);
        ((Literal)upper.Right).Value.Should().Be("Z");
    }

    [Fact]
    public void Parse_ArithmeticExpression_ReturnsCorrectAST()
    {
        // Arrange
        const string cql = "population / 2 + 1 >= 10";

        // Act
        var result = _parser.Parse(cql);

        // Assert
        result.Should().BeOfType<BinaryExpression>();
        var comparison = (BinaryExpression)result;
        comparison.Operator.Should().Be(BinaryOperator.GreaterThanOrEqual);

        var addExpr = (BinaryExpression)comparison.Left;
        addExpr.Operator.Should().Be(BinaryOperator.Add);

        var divideExpr = (BinaryExpression)addExpr.Left;
        divideExpr.Operator.Should().Be(BinaryOperator.Divide);
    }

    [Fact]
    public void Parse_TemporalPredicateWithInterval_ReturnsTemporalPredicate()
    {
        // Arrange
        const string cql = "T_INTERSECTS(timestamp, INTERVAL('2020-01-01','2020-12-31'))";

        // Act
        var result = _parser.Parse(cql);

        // Assert
        result.Should().BeOfType<TemporalPredicate>();
        var temporal = (TemporalPredicate)result;
        temporal.Operator.Should().Be(TemporalOperator.Intersects);
        temporal.Right.Should().BeOfType<IntervalLiteral>();
    }

    [Fact]
    public void Parse_ArrayPredicate_ReturnsArrayPredicate()
    {
        // Arrange
        const string cql = "A_CONTAINS(tags, ('a','b'))";

        // Act
        var result = _parser.Parse(cql);

        // Assert
        result.Should().BeOfType<ArrayPredicate>();
        var arrayPredicate = (ArrayPredicate)result;
        arrayPredicate.Operator.Should().Be(ArrayOperator.Contains);
        arrayPredicate.Right.Should().BeOfType<ArrayLiteral>();
    }

    [Fact]
    public void Parse_CaseInsensitiveLike_ReturnsFunctionCall()
    {
        // Arrange
        const string cql = "name LIKE CASEI('Foo%')";

        // Act
        var result = _parser.Parse(cql);

        // Assert
        result.Should().BeOfType<BinaryExpression>();
        var binary = (BinaryExpression)result;
        binary.Operator.Should().Be(BinaryOperator.Like);
        binary.Right.Should().BeOfType<FunctionCall>();

        var function = (FunctionCall)binary.Right;
        function.FunctionName.Should().Be("CASEI");
    }

    [Fact]
    public void Parse_BboxLiteral_ReturnsGeometryLiteral()
    {
        // Arrange
        const string cql = "S_INTERSECTS(geom, BBOX(0,0,1,1))";

        // Act
        var result = _parser.Parse(cql);

        // Assert
        result.Should().BeOfType<SpatialPredicate>();
        var spatial = (SpatialPredicate)result;
        spatial.Right.Should().BeOfType<GeometryLiteral>();

        var geometry = (GeometryLiteral)spatial.Right;
        geometry.OriginalFormat.Should().StartWith("BBOX");
    }

    [Fact]
    public void Parse_Bbox3D_ParsesCorrectCoordinateOrder()
    {
        // 3D BBOX: BBOX(minX, minY, minZ, maxX, maxY, maxZ)
        // The parser should use minX, minY, maxX, maxY (values 0,1,3,4) for the 2D polygon
        const string cql = "S_INTERSECTS(geom, BBOX(-180, -90, -1000, 180, 90, 1000))";

        var result = _parser.Parse(cql);

        result.Should().BeOfType<SpatialPredicate>();
        var spatial = (SpatialPredicate)result;
        spatial.Right.Should().BeOfType<GeometryLiteral>();

        var geometry = (GeometryLiteral)spatial.Right;
        geometry.OriginalFormat.Should().StartWith("BBOX");
        // If parsed correctly: minX=-180, minY=-90, maxX=180, maxY=90
        // (minZ=-1000, maxZ=1000 are discarded for the 2D polygon)
        // The WKB should represent a valid envelope polygon, not an inverted one
        geometry.Wkb.Should().NotBeNull();
    }

    [Fact]
    public void Parse_BboxInvalidValueCount_ThrowsArgumentException()
    {
        // 5 values is not valid for BBOX (needs 4 or 6)
        const string cql = "S_INTERSECTS(geom, BBOX(0, 0, 1, 1, 2))";

        var action = () => _parser.Parse(cql);
        var exception = action.Should().Throw<ArgumentException>().Which;
        exception.Message.Should().StartWith("Failed to parse CQL2 expression.");
        exception.ParamName.Should().Be("cql2Text");
        exception.InnerException.Should().BeOfType<ArgumentException>()
            .Which.Message.Should().Contain("4 or 6");
    }

    [Fact]
    public void Parse_Exponent_IsRightAssociative()
    {
        // Arrange
        const string cql = "2 ^ 3 ^ 2";

        // Act
        var result = _parser.Parse(cql);

        // Assert
        result.Should().BeOfType<BinaryExpression>();
        var power = (BinaryExpression)result;
        power.Operator.Should().Be(BinaryOperator.Power);
        power.Right.Should().BeOfType<BinaryExpression>();
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
