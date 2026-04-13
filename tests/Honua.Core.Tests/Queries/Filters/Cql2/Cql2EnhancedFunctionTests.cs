// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Queries.Filters;
using Honua.Core.Queries.Filters.Cql2;

namespace Honua.Core.Tests.Queries.Filters.Cql2;

/// <summary>
/// Tests for enhanced spatial and math function support in CQL2
/// </summary>
public class Cql2EnhancedFunctionTests
{
    private readonly Cql2Parser _parser = new();

    [Theory]
    [InlineData("ST_Area(shape) > 1000")]
    [InlineData("ST_Length(road) < 500")]
    [InlineData("ST_Distance(point1, point2) BETWEEN 100 AND 200")]
    [InlineData("ST_Buffer(geom, 50)")]
    [InlineData("ST_IsValid(shape) = TRUE")]
    public void Parse_SpatialFunctions_ShouldGenerateCorrectAST(string cql)
    {
        // Act
        var result = _parser.Parse(cql);

        // Assert
        result.Should().NotBeNull();

        // Should parse without throwing exceptions
        if (result is BinaryExpression binary)
        {
            // For functions that return values used in comparisons
            (binary.Left.Should().BeOfType<FunctionCall>().Or.BeOfType<PropertyReference>());
        }
        else if (result is FunctionCall)
        {
            // For functions that are standalone
            result.Should().BeOfType<FunctionCall>();
        }
    }

    [Theory]
    [InlineData("SQRT(area) > 10")]
    [InlineData("SIN(angle) < 0.5")]
    [InlineData("COS(angle) > 0.8")]
    [InlineData("TAN(angle) BETWEEN -1 AND 1")]
    [InlineData("LOG(value) > 0")]
    [InlineData("EXP(value) < 100")]
    [InlineData("POWER(base, 2) = 16")]
    public void Parse_MathFunctions_ShouldGenerateCorrectAST(string cql)
    {
        // Act
        var result = _parser.Parse(cql);

        // Assert
        result.Should().NotBeNull();
        result.Should().BeOfType<BinaryExpression>();

        var binary = (BinaryExpression)result;
        binary.Left.Should().BeOfType<FunctionCall>();
    }

    [Theory]
    [InlineData("COUNT(id) > 5")]
    [InlineData("SUM(amount) > 1000")]
    [InlineData("AVG(score) BETWEEN 75 AND 90")]
    [InlineData("MIN(date_created) > TIMESTAMP('2024-01-01T00:00:00Z')")]
    [InlineData("MAX(updated_at) < NOW()")]
    public void Parse_AggregateFunctions_ShouldGenerateCorrectAST(string cql)
    {
        // Act
        var result = _parser.Parse(cql);

        // Assert
        result.Should().NotBeNull();
        result.Should().BeOfType<BinaryExpression>();

        var binary = (BinaryExpression)result;
        binary.Left.Should().BeOfType<FunctionCall>();
    }

    [Theory]
    [InlineData("CAST(value, 'INTEGER') = 42")]
    [InlineData("CAST(text_field, 'NUMERIC') > 3.14")]
    [InlineData("CAST(date_string, 'TIMESTAMP') > NOW()")]
    public void Parse_CastFunction_ShouldGenerateCorrectAST(string cql)
    {
        // Act
        var result = _parser.Parse(cql);

        // Assert
        result.Should().NotBeNull();
        result.Should().BeOfType<BinaryExpression>();

        var binary = (BinaryExpression)result;
        binary.Left.Should().BeOfType<FunctionCall>();

        var functionCall = (FunctionCall)binary.Left;
        functionCall.FunctionName.Should().Be("CAST");
        functionCall.Arguments.Should().HaveCount(2);
    }

    [Fact]
    public void Parse_ComplexSpatialExpression_ShouldHandleNestedFunctions()
    {
        // Arrange
        const string cql = "ST_Area(ST_Buffer(geom, 100)) > ST_Area(geom) * 2";

        // Act
        var result = _parser.Parse(cql);

        // Assert
        result.Should().NotBeNull();
        result.Should().BeOfType<BinaryExpression>();

        var binary = (BinaryExpression)result;
        binary.Left.Should().BeOfType<FunctionCall>(); // ST_Area
        binary.Right.Should().BeOfType<BinaryExpression>(); // ST_Area(geom) * 2

        var leftFunction = (FunctionCall)binary.Left;
        leftFunction.FunctionName.Should().Be("ST_Area");
        leftFunction.Arguments.Should().HaveCount(1);
        leftFunction.Arguments[0].Should().BeOfType<FunctionCall>(); // ST_Buffer
    }

    [Fact]
    public void Parse_SpatialDistanceWithGeometry_ShouldParseCorrectly()
    {
        // Arrange
        const string cql = "ST_Distance(shape, POINT(1 2)) < 1000";

        // Act
        var result = _parser.Parse(cql);

        // Assert
        result.Should().NotBeNull();
        result.Should().BeOfType<BinaryExpression>();

        var binary = (BinaryExpression)result;
        binary.Left.Should().BeOfType<FunctionCall>();
        binary.Operator.Should().Be(BinaryOperator.LessThan);
        binary.Right.Should().BeOfType<Literal>();

        var distanceFunction = (FunctionCall)binary.Left;
        distanceFunction.FunctionName.Should().Be("ST_Distance");
        distanceFunction.Arguments.Should().HaveCount(2);
        distanceFunction.Arguments[0].Should().BeOfType<PropertyReference>(); // shape
        distanceFunction.Arguments[1].Should().BeOfType<GeometryLiteral>(); // POINT(1 2)
    }

    [Fact]
    public void Parse_MultipleEnhancedFunctions_InComplexExpression_ShouldParseCorrectly()
    {
        // Arrange
        const string cql = "(ST_Area(shape) > 1000 AND UPPER(name) LIKE 'BUILD%') OR (SIN(angle) > 0.5 AND COUNT(items) > 5)";

        // Act
        var result = _parser.Parse(cql);

        // Assert
        result.Should().NotBeNull();
        result.Should().BeOfType<BinaryExpression>();

        var orExpression = (BinaryExpression)result;
        orExpression.Operator.Should().Be(BinaryOperator.Or);

        // Left side should be an AND expression with spatial and string functions
        orExpression.Left.Should().BeOfType<BinaryExpression>();
        var leftAnd = (BinaryExpression)orExpression.Left;
        leftAnd.Operator.Should().Be(BinaryOperator.And);

        // Right side should be an AND expression with math and aggregate functions
        orExpression.Right.Should().BeOfType<BinaryExpression>();
        var rightAnd = (BinaryExpression)orExpression.Right;
        rightAnd.Operator.Should().Be(BinaryOperator.And);
    }
}