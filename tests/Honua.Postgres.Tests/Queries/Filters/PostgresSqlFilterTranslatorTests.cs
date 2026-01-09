// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.Shared.Models;
using Honua.Core.Queries.Filters;
using Honua.Postgres.Queries.Filters;

namespace Honua.Postgres.Tests.Queries.Filters;

public class PostgresSqlFilterTranslatorTests
{
    private readonly PostgresSqlFilterTranslator _translator = new();
    private readonly LayerDefinition _layer;

    public PostgresSqlFilterTranslatorTests()
    {
        _layer = new LayerDefinition(
            Id: 1,
            Name: "TestLayer",
            Description: "Test layer for SQL translation",
            GeometryType: GeometryType.Point,
            SpatialReference: SpatialReference.WGS84,
            Fields: [
                new FieldDefinition("id", FieldType.Integer, Nullable: false),
                new FieldDefinition("geom", FieldType.Geometry, Nullable: false),
                new FieldDefinition("name", FieldType.String),
                new FieldDefinition("age", FieldType.Integer),
                new FieldDefinition("active", FieldType.Boolean),
                new FieldDefinition("description", FieldType.String),
                new FieldDefinition("field", FieldType.String),
                new FieldDefinition("status", FieldType.String),
                new FieldDefinition("timestamp", FieldType.DateTime),
                new FieldDefinition("tags", FieldType.Json)
            ]
        );
    }

    [Fact]
    public void Translate_PropertyReference_ReturnsFieldName()
    {
        // Arrange
        var property = new PropertyReference("name");

        // Act
        var result = _translator.Translate(property, _layer);

        // Assert
        result.Sql.Should().Be("\"name\"");
        result.Parameters.Should().BeEmpty();
    }

    [Fact]
    public void Translate_Literal_ReturnsParameterizedValue()
    {
        // Arrange
        var literal = new Literal("John", LiteralType.Text);

        // Act
        var result = _translator.Translate(literal, _layer);

        // Assert
        result.Sql.Should().Be("@p0");
        result.Parameters.Should().HaveCount(1);
        result.Parameters[0].Should().Be("John");
    }

    [Fact]
    public void Translate_BinaryComparison_ReturnsCorrectSQL()
    {
        // Arrange
        var left = new PropertyReference("age");
        var right = new Literal(25, LiteralType.Number);
        var comparison = new BinaryExpression(left, BinaryOperator.GreaterThanOrEqual, right);

        // Act
        var result = _translator.Translate(comparison, _layer);

        // Assert
        result.Sql.Should().Be("\"age\" >= @p0");
        result.Parameters.Should().HaveCount(1);
        result.Parameters[0].Should().Be(25);
    }

    [Fact]
    public void Translate_LogicalAnd_ReturnsCorrectSQL()
    {
        // Arrange
        var left = new BinaryExpression(
            new PropertyReference("age"),
            BinaryOperator.GreaterThanOrEqual,
            new Literal(18, LiteralType.Number));

        var right = new BinaryExpression(
            new PropertyReference("name"),
            BinaryOperator.Like,
            new Literal("John%", LiteralType.Text));

        var andExpr = new BinaryExpression(left, BinaryOperator.And, right);

        // Act
        var result = _translator.Translate(andExpr, _layer);

        // Assert
        result.Sql.Should().Be("(\"age\" >= @p0 AND \"name\" LIKE @p1)");
        result.Parameters.Should().HaveCount(2);
        result.Parameters[0].Should().Be(18);
        result.Parameters[1].Should().Be("John%");
    }

    [Fact]
    public void Translate_NotExpression_ReturnsCorrectSQL()
    {
        // Arrange
        var inner = new BinaryExpression(
            new PropertyReference("active"),
            BinaryOperator.Equal,
            new Literal(true, LiteralType.Boolean));

        var notExpr = new UnaryExpression(UnaryOperator.Not, inner);

        // Act
        var result = _translator.Translate(notExpr, _layer);

        // Assert
        result.Sql.Should().Be("NOT (\"active\" = @p0)");
        result.Parameters.Should().HaveCount(1);
        result.Parameters[0].Should().Be(true);
    }

    [Fact]
    public void Translate_IsNull_ReturnsCorrectSQL()
    {
        // Arrange
        var isNull = new UnaryExpression(UnaryOperator.IsNull, new PropertyReference("description"));

        // Act
        var result = _translator.Translate(isNull, _layer);

        // Assert
        result.Sql.Should().Be("\"description\" IS NULL");
        result.Parameters.Should().BeEmpty();
    }

    [Fact]
    public void Translate_IsNotNull_ReturnsCorrectSQL()
    {
        // Arrange
        var isNotNull = new UnaryExpression(UnaryOperator.IsNotNull, new PropertyReference("description"));

        // Act
        var result = _translator.Translate(isNotNull, _layer);

        // Assert
        result.Sql.Should().Be("\"description\" IS NOT NULL");
        result.Parameters.Should().BeEmpty();
    }

    [Fact]
    public void Translate_InClause_ReturnsCorrectSQL()
    {
        // Arrange
        var valueList = new ValueList([
            new Literal("active", LiteralType.Text),
            new Literal("pending", LiteralType.Text),
            new Literal("completed", LiteralType.Text)
        ]);

        var inClause = new BinaryExpression(
            new PropertyReference("status"),
            BinaryOperator.In,
            valueList);

        // Act
        var result = _translator.Translate(inClause, _layer);

        // Assert
        result.Sql.Should().Be("\"status\" IN (@p0, @p1, @p2)");
        result.Parameters.Should().HaveCount(3);
        result.Parameters[0].Should().Be("active");
        result.Parameters[1].Should().Be("pending");
        result.Parameters[2].Should().Be("completed");
    }

    [Fact]
    public void Translate_SpatialPredicate_ReturnsCorrectSQL()
    {
        // Arrange
        var wkb = new byte[] { 1, 2, 3, 4 }; // Mock WKB data
        var geometry = new GeometryLiteral(wkb, 4326, "POINT(1 2)");
        var spatial = new SpatialPredicate(
            SpatialOperator.Intersects,
            new PropertyReference("geom"),
            geometry);

        // Act
        var result = _translator.Translate(spatial, _layer);

        // Assert
        result.Sql.Should().Be("ST_Intersects(\"geom\"::geometry, ST_GeomFromWKB(@p0, @p1))");
        result.Parameters.Should().HaveCount(2);
        result.Parameters[0].Should().BeEquivalentTo(wkb);
        result.Parameters[1].Should().Be(4326);
    }

    [Fact]
    public void Translate_SpatialDistancePredicate_ReturnsCorrectSQL()
    {
        // Arrange
        var wkb = new byte[] { 1, 2, 3, 4 };
        var geometry = new GeometryLiteral(wkb, 4326, "POINT(1 2)");
        var distance = new Literal(100, LiteralType.Number);
        var spatial = new SpatialDistancePredicate(
            SpatialOperator.DWithin,
            new PropertyReference("geom"),
            geometry,
            distance);

        // Act
        var result = _translator.Translate(spatial, _layer);

        // Assert
        result.Sql.Should().StartWith("ST_DWithin(\"geom\"::geometry, ST_GeomFromWKB(@p0, @p1), @p2)");
        result.Parameters.Should().HaveCount(3);
        result.Parameters[2].Should().Be(100);
    }

    [Fact]
    public void Translate_ArithmeticExpression_ReturnsCorrectSQL()
    {
        // Arrange
        var arithmetic = new BinaryExpression(
            new PropertyReference("age"),
            BinaryOperator.Add,
            new Literal(1, LiteralType.Number));

        // Act
        var result = _translator.Translate(arithmetic, _layer);

        // Assert
        result.Sql.Should().Be("(\"age\" + @p0)");
        result.Parameters.Should().ContainSingle();
    }

    [Fact]
    public void Translate_DivOperator_UsesTruncation()
    {
        // Arrange
        var arithmetic = new BinaryExpression(
            new PropertyReference("age"),
            BinaryOperator.Div,
            new Literal(2, LiteralType.Number));

        // Act
        var result = _translator.Translate(arithmetic, _layer);

        // Assert
        result.Sql.Should().Be("TRUNC((\"age\") / (@p0))");
    }

    [Fact]
    public void Translate_PowerOperator_UsesPowerFunction()
    {
        // Arrange
        var arithmetic = new BinaryExpression(
            new PropertyReference("age"),
            BinaryOperator.Power,
            new Literal(2, LiteralType.Number));

        // Act
        var result = _translator.Translate(arithmetic, _layer);

        // Assert
        result.Sql.Should().Be("POWER(\"age\", @p0)");
    }

    [Fact]
    public void Translate_TemporalPredicate_ReturnsCorrectSQL()
    {
        // Arrange
        var interval = new IntervalLiteral(
            new Literal(new DateTimeOffset(2023, 01, 01, 0, 0, 0, TimeSpan.Zero), LiteralType.DateTime),
            new Literal(new DateTimeOffset(2023, 12, 31, 0, 0, 0, TimeSpan.Zero), LiteralType.DateTime));

        var predicate = new TemporalPredicate(
            TemporalOperator.Intersects,
            new PropertyReference("timestamp"),
            interval);

        // Act
        var result = _translator.Translate(predicate, _layer);

        // Assert
        result.Sql.Should().Be("NOT (\"timestamp\" < @p0 OR \"timestamp\" > @p1)");
        result.Parameters.Should().HaveCount(2);
    }

    [Fact]
    public void Translate_ArrayPredicate_ReturnsCorrectSQL()
    {
        // Arrange
        var array = new ArrayLiteral([
            new Literal("a", LiteralType.Text),
            new Literal("b", LiteralType.Text)
        ]);

        var predicate = new ArrayPredicate(
            ArrayOperator.Contains,
            new PropertyReference("tags"),
            array);

        // Act
        var result = _translator.Translate(predicate, _layer);

        // Assert
        result.Sql.Should().Be("\"attributes\" -> 'tags' @> @p0::jsonb");
    }

    [Fact]
    public void Translate_CaseInsensitiveFunction_ReturnsLoweredSQL()
    {
        // Arrange
        var function = new FunctionCall("CASEI", [new PropertyReference("name")]);

        // Act
        var result = _translator.Translate(function, _layer);

        // Assert
        result.Sql.Should().Be("LOWER(\"name\")");
    }

    [Theory]
    [InlineData(BinaryOperator.Equal, "=")]
    [InlineData(BinaryOperator.NotEqual, "<>")]
    [InlineData(BinaryOperator.LessThan, "<")]
    [InlineData(BinaryOperator.LessThanOrEqual, "<=")]
    [InlineData(BinaryOperator.GreaterThan, ">")]
    [InlineData(BinaryOperator.GreaterThanOrEqual, ">=")]
    [InlineData(BinaryOperator.Like, "LIKE")]
    [InlineData(BinaryOperator.NotLike, "NOT LIKE")]
    public void Translate_BinaryOperators_ReturnsCorrectSQL(BinaryOperator op, string expectedSql)
    {
        // Arrange
        var binary = new BinaryExpression(
            new PropertyReference("field"),
            op,
            new Literal("value", LiteralType.Text));

        // Act
        var result = _translator.Translate(binary, _layer);

        // Assert
        result.Sql.Should().Contain(expectedSql);
    }

    [Theory]
    [InlineData(SpatialOperator.Intersects, "ST_Intersects")]
    [InlineData(SpatialOperator.Contains, "ST_Contains")]
    [InlineData(SpatialOperator.Within, "ST_Within")]
    [InlineData(SpatialOperator.Crosses, "ST_Crosses")]
    [InlineData(SpatialOperator.Touches, "ST_Touches")]
    [InlineData(SpatialOperator.Overlaps, "ST_Overlaps")]
    [InlineData(SpatialOperator.Disjoint, "ST_Disjoint")]
    [InlineData(SpatialOperator.Equals, "ST_Equals")]
    public void Translate_SpatialOperators_ReturnsCorrectFunction(SpatialOperator op, string expectedFunction)
    {
        // Arrange
        var wkb = new byte[] { 1, 2, 3, 4 };
        var geometry = new GeometryLiteral(wkb, 4326, "POINT(1 2)");
        var spatial = new SpatialPredicate(op, new PropertyReference("geom"), geometry);

        // Act
        var result = _translator.Translate(spatial, _layer);

        // Assert
        result.Sql.Should().StartWith(expectedFunction);
    }

    [Fact]
    public void Translate_UnknownField_ThrowsArgumentException()
    {
        // Arrange
        var unknownField = new PropertyReference("unknown_field");

        // Act & Assert
        var action = () => _translator.Translate(unknownField, _layer);
        action.Should().Throw<ArgumentException>()
            .WithMessage("*unknown_field*not found*");
    }

    [Fact]
    public void Translate_ComplexNestedExpression_ReturnsCorrectSQL()
    {
        // Arrange
        // (age >= 18 AND name LIKE 'John%') OR (city = 'Seattle' AND active = TRUE)
        var left = new BinaryExpression(
            new BinaryExpression(
                new PropertyReference("age"),
                BinaryOperator.GreaterThanOrEqual,
                new Literal(18, LiteralType.Number)),
            BinaryOperator.And,
            new BinaryExpression(
                new PropertyReference("name"),
                BinaryOperator.Like,
                new Literal("John%", LiteralType.Text)));

        var right = new BinaryExpression(
            new BinaryExpression(
                new PropertyReference("description"),
                BinaryOperator.Equal,
                new Literal("Seattle", LiteralType.Text)),
            BinaryOperator.And,
            new BinaryExpression(
                new PropertyReference("active"),
                BinaryOperator.Equal,
                new Literal(true, LiteralType.Boolean)));

        var orExpr = new BinaryExpression(left, BinaryOperator.Or, right);

        // Act
        var result = _translator.Translate(orExpr, _layer);

        // Assert
        result.Sql.Should().Be("((\"age\" >= @p0 AND \"name\" LIKE @p1) OR (\"description\" = @p2 AND \"active\" = @p3))");
        result.Parameters.Should().HaveCount(4);
        result.Parameters[0].Should().Be(18);
        result.Parameters[1].Should().Be("John%");
        result.Parameters[2].Should().Be("Seattle");
        result.Parameters[3].Should().Be(true);
    }
}
