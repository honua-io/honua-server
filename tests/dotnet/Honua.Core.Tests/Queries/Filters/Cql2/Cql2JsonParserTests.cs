// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Linq;
using FluentAssertions;
using Honua.Core.Queries.Filters;
using Honua.Core.Queries.Filters.Cql2;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;

namespace Honua.Core.Tests.Queries.Filters.Cql2;

public class Cql2JsonParserTests
{
    private readonly Cql2JsonParser _parser = new();

    [Trait("Tier", "Fast")]
    [Fact]
    public void Parse_EqualityExpression_ReturnsBinaryExpression()
    {
        // Arrange
        const string json = """{"op":"=","args":[{"property":"name"},"Test"]}""";

        // Act
        var result = _parser.Parse(json);

        // Assert
        result.Should().BeOfType<BinaryExpression>();
        var binary = (BinaryExpression)result;
        binary.Operator.Should().Be(BinaryOperator.Equal);
        ((PropertyReference)binary.Left).PropertyName.Should().Be("name");
        ((Literal)binary.Right).Value.Should().Be("Test");
    }

    [Trait("Tier", "Fast")]
    [Theory]
    [InlineData("\"just a string\"")]
    [InlineData("42")]
    [InlineData("null")]
    [InlineData("[1,2]")]
    [InlineData("""{"property":"name"}""")]
    [InlineData("""{"interval":["2020-01-01",".."]}""")]
    public void Parse_RootIsNotAPredicate_ThrowsArgumentException(string json)
    {
        var act = () => _parser.Parse(json);

        act.Should().Throw<ArgumentException>().WithMessage("CQL2-JSON filter must be a predicate*");
    }

    [Trait("Tier", "Fast")]
    [Theory]
    [InlineData("true")]
    [InlineData("""{"op":"isNull","args":[{"property":"name"}]}""")]
    public void Parse_RootIsBooleanOrPredicate_IsAccepted(string json)
    {
        var act = () => _parser.Parse(json);

        act.Should().NotThrow();
    }

    [Trait("Tier", "Fast")]
    [Theory]
    [InlineData("and")]
    [InlineData("or")]
    public void Parse_FlatLogicalOperatorBeyondOperandLimit_ThrowsArgumentException(string op)
    {
        var act = () => _parser.Parse(FlatLogicalJson(op, FilterParserGuard.MaxExpressionDepth + 1));

        act.Should().Throw<ArgumentException>()
            .WithMessage($"*maximum nesting depth of {FilterParserGuard.MaxExpressionDepth}*");
    }

    [Trait("Tier", "Fast")]
    [Fact]
    public void Parse_FlatLogicalOperatorWithinDepthLimit_ReturnsLeftDeepChain()
    {
        // N comparison operands fold into a tree of depth N + 1.
        var result = _parser.Parse(FlatLogicalJson("and", FilterParserGuard.MaxExpressionDepth - 1));

        result.Should().BeOfType<BinaryExpression>().Which.Operator.Should().Be(BinaryOperator.And);
    }

    [Trait("Tier", "Fast")]
    [Fact]
    public void Parse_FlatLogicalOperatorUnderNegationsBeyondDepthLimit_ThrowsArgumentException()
    {
        // Operand count and JSON nesting are each within their limits; the folded tree is not.
        const int negations = 10;
        var json = FlatLogicalJson("and", FilterParserGuard.MaxExpressionDepth - negations);
        for (var i = 0; i < negations; i++)
        {
            json = $$"""{"op":"not","args":[{{json}}]}""";
        }

        var act = () => _parser.Parse(json);

        act.Should().Throw<ArgumentException>()
            .WithMessage($"*maximum nesting depth of {FilterParserGuard.MaxExpressionDepth}*");
    }

    [Trait("Tier", "Fast")]
    [Theory]
    [InlineData("""{"op":"=","args":[{"property":"name"},"\udc00 note"]}""")]
    [InlineData("""{"op":"=","args":[{"property":"\ud800"},"value"]}""")]
    [InlineData("""{"op":5,"args":[{"property":"name"},"value"]}""")]
    [InlineData("""{"op":"=","args":[{"property":true},"value"]}""")]
    public void Parse_UnreadableStringMember_ThrowsArgumentException(string json)
    {
        var act = () => _parser.Parse(json);

        act.Should().Throw<ArgumentException>().WithMessage("Invalid CQL2-JSON payload*");
    }

    private static string FlatLogicalJson(string op, int operandCount)
        => $$"""{"op":"{{op}}","args":[""" +
           string.Join(",", Enumerable.Repeat("""{"op":"=","args":[{"property":"name"},"a"]}""", operandCount)) +
           "]}";

    [Trait("Tier", "Fast")]
    [Fact]
    public void Parse_SpatialPredicate_ReturnsSpatialPredicate()
    {
        // Arrange
        const string json = """{"op":"s_intersects","args":[{"property":"geom"},{"type":"Point","coordinates":[1,2]}]}""";

        // Act
        var result = _parser.Parse(json);

        // Assert
        result.Should().BeOfType<SpatialPredicate>();
        var spatial = (SpatialPredicate)result;
        spatial.Operator.Should().Be(SpatialOperator.Intersects);
        spatial.Left.Should().BeOfType<PropertyReference>();
        spatial.Right.Should().BeOfType<GeometryLiteral>();
    }

    [Trait("Tier", "Fast")]
    [Fact]
    public void Parse_SpatialPredicate_WithExplicitGeometryCrsObject_UsesExplicitSrid()
    {
        // Arrange
        const string json =
            """{"op":"s_intersects","args":[{"property":"geom"},{"type":"Point","coordinates":[1,2],"crs":{"type":"name","properties":{"name":"EPSG:3857"}}}]}""";

        // Act
        var result = _parser.Parse(json);

        // Assert
        var spatial = result.Should().BeOfType<SpatialPredicate>().Subject;
        var geometry = spatial.Right.Should().BeOfType<GeometryLiteral>().Subject;
        geometry.Srid.Should().Be(3857);
    }

    [Trait("Tier", "Fast")]
    [Fact]
    public void Parse_SpatialPredicate_WithExplicitGeometryCrsUri_UsesExplicitSrid()
    {
        // Arrange
        const string json =
            """{"op":"s_intersects","args":[{"property":"geom"},{"type":"Point","coordinates":[1,2],"crs":{"type":"name","properties":{"name":"http://www.opengis.net/def/crs/EPSG/0/26910"}}}]}""";

        // Act
        var result = _parser.Parse(json);

        // Assert
        var spatial = result.Should().BeOfType<SpatialPredicate>().Subject;
        var geometry = spatial.Right.Should().BeOfType<GeometryLiteral>().Subject;
        geometry.Srid.Should().Be(26910);
    }

    [Trait("Tier", "Fast")]
    [Fact]
    public void Parse_SpatialPredicate_WithMalformedExplicitGeometryCrs_ThrowsArgumentException()
    {
        // Arrange
        const string json =
            """{"op":"s_intersects","args":[{"property":"geom"},{"type":"Point","coordinates":[1,2],"crs":{"type":"name","properties":{"name":"prefixhttp://www.opengis.net/def/crs/EPSG/0/4326"}}}]}""";

        // Act
        var act = () => _parser.Parse(json);

        // Assert
        act.Should().Throw<ArgumentException>()
            .WithMessage("*Invalid geometry CRS identifier*");
    }

    [Trait("Tier", "Fast")]
    [Fact]
    public void Parse_BboxLiteral_WithMalformedExplicitGeometryCrs_ThrowsArgumentException()
    {
        // Arrange
        const string json =
            """{"op":"s_intersects","args":[{"property":"geom"},{"bbox":[-10,-10,10,10],"crs":"bad-CRS84"}]}""";

        // Act
        var act = () => _parser.Parse(json);

        // Assert
        act.Should().Throw<ArgumentException>()
            .WithMessage("*Invalid geometry CRS identifier*");
    }

    [Trait("Tier", "Fast")]
    [Fact]
    public void Parse_BboxLiteral_AntimeridianCrossing_ReturnsMultiPolygonGeometry()
    {
        const string json =
            """{"op":"s_intersects","args":[{"property":"geom"},{"bbox":[170,-10,-170,10]}]}""";

        var result = _parser.Parse(json);

        var spatial = result.Should().BeOfType<SpatialPredicate>().Subject;
        var geometry = spatial.Right.Should().BeOfType<GeometryLiteral>().Subject;

        var parsed = new WKBReader().Read(geometry.Wkb);
        parsed.Should().BeOfType<MultiPolygon>();
        ((MultiPolygon)parsed).NumGeometries.Should().Be(2);
    }

    [Trait("Tier", "Fast")]
    [Fact]
    public void Parse_BboxLiteral_WithProjectedCrsAndInvertedX_ThrowsArgumentException()
    {
        const string json =
            """{"op":"s_intersects","args":[{"property":"geom"},{"bbox":[10,-10,-10,10],"crs":"EPSG:3857"}]}""";

        var act = () => _parser.Parse(json);

        act.Should().Throw<ArgumentException>()
            .WithMessage("*Invalid bbox literal in CQL2-JSON*");
    }

    [Trait("Tier", "Fast")]
    [Fact]
    public void Parse_BboxLiteral_WithGeographicCrsAndOutOfRangeCoordinates_ThrowsArgumentException()
    {
        const string json =
            """{"op":"s_intersects","args":[{"property":"geom"},{"bbox":[200,-10,210,10],"crs":"EPSG:4326"}]}""";

        var act = () => _parser.Parse(json);

        act.Should().Throw<ArgumentException>()
            .WithMessage("*Invalid bbox literal in CQL2-JSON*");
    }

    [Trait("Tier", "Fast")]
    [Fact]
    public void Parse_BboxLiteral_3DBox_ThrowsArgumentException()
    {
        const string json =
            """{"op":"s_intersects","args":[{"property":"geom"},{"bbox":[-180,-90,-10,180,90,10]}]}""";

        var act = () => _parser.Parse(json);

        act.Should().Throw<ArgumentException>()
            .WithMessage("*3D bounding boxes are not supported*");
    }

    [Trait("Tier", "Fast")]
    [Fact]
    public void Parse_GeometryLiteral_WithTooManyVertices_ThrowsArgumentException()
    {
        var coordinates = string.Join(",", Enumerable.Range(0, 50_001).Select(i => $"[{i},0]"));
        var json = @"{""op"":""s_intersects"",""args"":[{""property"":""geom""},{""type"":""LineString"",""coordinates"":[" + coordinates + @"]}]}";

        var act = () => _parser.Parse(json);

        act.Should().Throw<ArgumentException>()
            .WithMessage("*maximum geometry complexity*");
    }

    [Trait("Tier", "Fast")]
    [Fact]
    public void Parse_SpatialDWithin_ReturnsSpatialDistancePredicate()
    {
        // Arrange
        const string json = """{"op":"s_dwithin","args":[{"property":"geom"},{"type":"Point","coordinates":[1,2]},100]}""";

        // Act
        var result = _parser.Parse(json);

        // Assert
        result.Should().BeOfType<SpatialDistancePredicate>();
        var spatial = (SpatialDistancePredicate)result;
        spatial.Operator.Should().Be(SpatialOperator.DWithin);
        spatial.Distance.Should().BeOfType<Literal>();
    }

    [Trait("Tier", "Fast")]
    [Fact]
    public void Parse_TemporalPredicateWithInterval_ReturnsTemporalPredicate()
    {
        // Arrange
        const string json = """{"op":"t_intersects","args":[{"property":"timestamp"},{"interval":["2020-01-01","2020-12-31"]}]}""";

        // Act
        var result = _parser.Parse(json);

        // Assert
        result.Should().BeOfType<TemporalPredicate>();
        var temporal = (TemporalPredicate)result;
        temporal.Operator.Should().Be(TemporalOperator.Intersects);
        temporal.Right.Should().BeOfType<IntervalLiteral>();
    }

    [Trait("Tier", "Fast")]
    [Fact]
    public void Parse_ArrayPredicate_ReturnsArrayPredicate()
    {
        // Arrange
        const string json = """{"op":"a_contains","args":[{"property":"tags"},["a","b"]]}""";

        // Act
        var result = _parser.Parse(json);

        // Assert
        result.Should().BeOfType<ArrayPredicate>();
        var arrayPredicate = (ArrayPredicate)result;
        arrayPredicate.Operator.Should().Be(ArrayOperator.Contains);
        arrayPredicate.Right.Should().BeOfType<ArrayLiteral>();
    }

    [Trait("Tier", "Fast")]
    [Fact]
    public void Parse_CaseInsensitiveFunction_ReturnsFunctionCall()
    {
        // Arrange
        const string json = """{"op":"=","args":[{"property":"name"},{"op":"casei","args":["Foo"]}]}""";

        // Act
        var result = _parser.Parse(json);

        // Assert
        var binary = result.Should().BeOfType<BinaryExpression>().Subject;
        var function = binary.Right.Should().BeOfType<FunctionCall>().Subject;
        function.FunctionName.Should().Be("casei");
    }

    [Trait("Tier", "Fast")]
    [Theory]
    [InlineData("""{"op":"casei","args":["x"]}""")]
    [InlineData("""{"op":"accenti","args":["x"]}""")]
    [InlineData("""{"op":"lower","args":["abc"]}""")]
    [InlineData("""{"op":"+","args":[1,2]}""")]
    [InlineData("""{"op":"-","args":[5,3]}""")]
    [InlineData("""{"op":"-","args":[5]}""")]
    [InlineData("""{"op":"*","args":[2,3]}""")]
    [InlineData("""{"op":"/","args":[4,2]}""")]
    [InlineData("""{"op":"%","args":[5,2]}""")]
    [InlineData("""{"op":"^","args":[2,3]}""")]
    [InlineData("""{"op":"ST_Buffer","args":[{"property":"geom"},50]}""")]
    public void Parse_ScalarOrArithmeticRoot_ThrowsArgumentException(string json)
    {
        var act = () => _parser.Parse(json);

        act.Should().Throw<ArgumentException>()
            .WithMessage("CQL2-JSON filter must be a predicate*");
    }

    [Trait("Tier", "Fast")]
    [Theory]
    [InlineData("""{"op":"not","args":[1]}""")]
    [InlineData("""{"op":"not","args":["abc"]}""")]
    [InlineData("""{"op":"not","args":[{"op":"+","args":[1,2]}]}""")]
    [InlineData("""{"op":"and","args":[true,1]}""")]
    [InlineData("""{"op":"or","args":[false,"abc"]}""")]
    public void Parse_NonBooleanLogicalOperand_ThrowsArgumentException(string json)
    {
        var act = () => _parser.Parse(json);

        act.Should().Throw<ArgumentException>()
            .WithMessage("*requires boolean or predicate operands*");
    }

    [Trait("Tier", "Fast")]
    [Theory]
    [InlineData("""{"op":"like","args":[{"property":"name"},123]}""")]
    [InlineData("""{"op":"like","args":[{"property":"name"},true]}""")]
    [InlineData("""{"op":"like","args":[123,"%a%"]}""")]
    [InlineData("""{"op":"notLike","args":[{"property":"name"},123]}""")]
    public void Parse_LikeWithInvalidOperandType_ThrowsArgumentException(string json)
    {
        var act = () => _parser.Parse(json);

        act.Should().Throw<ArgumentException>();
    }

    [Trait("Tier", "Fast")]
    [Theory]
    [InlineData("""{"op":"between","args":["a",1,2]}""")]
    [InlineData("""{"op":"notBetween","args":["a",1,2]}""")]
    [InlineData("""{"op":"between","args":[{"property":"name"},"a",2]}""")]
    [InlineData("""{"op":">=","args":["a",1]}""")]
    public void Parse_BetweenOrComparisonWithIncompatibleLiteralTypes_ThrowsArgumentException(string json)
    {
        var act = () => _parser.Parse(json);

        act.Should().Throw<ArgumentException>()
            .WithMessage("*Incompatible literal types*");
    }

    [Trait("Tier", "Fast")]
    [Theory]
    [InlineData("""{"op":"=","args":[{"op":"/","args":[1,0]},1]}""")]
    [InlineData("""{"op":"/","args":[1,0]}""")]
    [InlineData("""{"op":"=","args":[{"op":"%","args":[1,0]},0]}""")]
    [InlineData("""{"op":"=","args":[{"op":"div","args":[1,0]},1]}""")]
    [InlineData("""{"op":"=","args":[{"op":"/","args":[1,{"op":"-","args":[2,2]}]},1]}""")]
    [InlineData("""{"op":"=","args":[{"op":"mod","args":[1,0]},0]}""")]
    public void Parse_DivisionByZero_ThrowsArgumentException(string json)
    {
        var act = () => _parser.Parse(json);

        act.Should().Throw<ArgumentException>();
    }

    [Trait("Tier", "Fast")]
    [Theory]
    [InlineData("""{"op":"like","args":[{"property":"name"},"%foo%"]}""")]
    [InlineData("""{"op":"like","args":[{"property":"name"},{"op":"casei","args":["%foo%"]}]}""")]
    [InlineData("""{"op":"between","args":[{"property":"count"},1,5]}""")]
    [InlineData("""{"op":"ST_IsValid","args":[{"property":"geom"}]}""")]
    [InlineData("""{"op":"=","args":[{"op":"/","args":[4,2]},2]}""")]
    public void Parse_ValidPredicatesAndArithmetic_Succeed(string json)
    {
        var act = () => _parser.Parse(json);

        act.Should().NotThrow();
    }
}
