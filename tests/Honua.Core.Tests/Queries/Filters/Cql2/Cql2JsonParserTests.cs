// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Queries.Filters;
using Honua.Core.Queries.Filters.Cql2;

namespace Honua.Core.Tests.Queries.Filters.Cql2;

public class Cql2JsonParserTests
{
    private readonly Cql2JsonParser _parser = new();

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

    [Fact]
    public void Parse_CaseInsensitiveFunction_ReturnsFunctionCall()
    {
        // Arrange
        const string json = """{"op":"casei","args":["Foo"]}""";

        // Act
        var result = _parser.Parse(json);

        // Assert
        result.Should().BeOfType<FunctionCall>();
        var function = (FunctionCall)result;
        function.FunctionName.Should().Be("casei");
    }
}
