// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.Shared.Models;
using Honua.Core.Queries.Filters;
using Honua.MySql.Queries.Filters;

namespace Honua.MySql.Tests;

/// <summary>
/// Unit tests for translating CQL2/OGC filter expressions into MySQL/MariaDB SQL.
/// Verifies operator mapping, identifier quoting, KNN rejection, and cross-SRID rejection.
/// </summary>
public class MySqlSqlFilterTranslatorTests
{
    private readonly MySqlSqlFilterTranslator _translator = new();
    private readonly LayerDefinition _layer;

    public MySqlSqlFilterTranslatorTests()
    {
        _layer = new LayerDefinition(
            Id: 1,
            Name: "parcels",
            Description: null,
            GeometryType: GeometryType.Polygon,
            SpatialReference: SpatialReference.Create(4326),
            Fields:
            [
                new("id", FieldType.BigInteger, Nullable: false),
                new("name", FieldType.String),
                new("area", FieldType.Double),
                new("geometry", FieldType.Geometry, Nullable: false)
            ]);
    }

    [Fact]
    public void Translate_PropertyEqualsLiteral_BackticksFieldAndParameterizesValue()
    {
        var filter = new BinaryExpression(
            new PropertyReference("name"),
            BinaryOperator.Equal,
            new Literal("Acme", LiteralType.Text));

        var result = _translator.Translate(filter, _layer);

        Assert.Equal("`name` = @p0", result.Sql);
        Assert.Single(result.Parameters);
        Assert.Equal("Acme", result.Parameters[0]);
    }

    [Theory]
    [InlineData(BinaryOperator.NotEqual, "<>")]
    [InlineData(BinaryOperator.LessThan, "<")]
    [InlineData(BinaryOperator.LessThanOrEqual, "<=")]
    [InlineData(BinaryOperator.GreaterThan, ">")]
    [InlineData(BinaryOperator.GreaterThanOrEqual, ">=")]
    [InlineData(BinaryOperator.Like, "LIKE")]
    [InlineData(BinaryOperator.NotLike, "NOT LIKE")]
    public void Translate_BinaryOperators_RenderExpectedSqlOperator(BinaryOperator op, string expectedToken)
    {
        var filter = new BinaryExpression(
            new PropertyReference("area"),
            op,
            new Literal(10.0, LiteralType.Number));

        var result = _translator.Translate(filter, _layer);

        Assert.Contains(expectedToken, result.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Translate_AndExpression_WrapsBothSides()
    {
        var filter = new BinaryExpression(
            new BinaryExpression(
                new PropertyReference("area"),
                BinaryOperator.GreaterThan,
                new Literal(10.0, LiteralType.Number)),
            BinaryOperator.And,
            new BinaryExpression(
                new PropertyReference("name"),
                BinaryOperator.Equal,
                new Literal("Acme", LiteralType.Text)));

        var result = _translator.Translate(filter, _layer);

        Assert.Equal("(`area` > @p0 AND `name` = @p1)", result.Sql);
        Assert.Equal(2, result.Parameters.Count);
    }

    [Fact]
    public void Translate_IsNullUnary_GeneratesIsNullClause()
    {
        var filter = new UnaryExpression(UnaryOperator.IsNull, new PropertyReference("name"));

        var result = _translator.Translate(filter, _layer);

        Assert.Equal("`name` IS NULL", result.Sql);
    }

    [Fact]
    public void Translate_IntersectsSpatialPredicate_UsesMbrAndStIntersects()
    {
        var filter = new SpatialPredicate(
            SpatialOperator.Intersects,
            new PropertyReference("geometry"),
            new GeometryLiteral([0x01, 0x02], 4326, "POINT"));

        var result = _translator.Translate(filter, _layer);

        Assert.Equal(
            "MBRIntersects(`geometry`, ST_GeomFromWKB(@p0, 4326)) AND ST_Intersects(`geometry`, ST_GeomFromWKB(@p0, 4326))",
            result.Sql);
        Assert.Single(result.Parameters);
    }

    [Theory]
    [InlineData(SpatialOperator.Contains, "ST_Contains")]
    [InlineData(SpatialOperator.Within, "ST_Within")]
    [InlineData(SpatialOperator.Crosses, "ST_Crosses")]
    [InlineData(SpatialOperator.Touches, "ST_Touches")]
    [InlineData(SpatialOperator.Overlaps, "ST_Overlaps")]
    [InlineData(SpatialOperator.Disjoint, "ST_Disjoint")]
    [InlineData(SpatialOperator.Equals, "ST_Equals")]
    public void Translate_SpatialPredicate_MapsToExpectedFunction(SpatialOperator op, string expectedFunction)
    {
        var filter = new SpatialPredicate(
            op,
            new PropertyReference("geometry"),
            new GeometryLiteral([0x01], 4326, "POINT"));

        var result = _translator.Translate(filter, _layer);

        Assert.StartsWith(expectedFunction, result.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Translate_DWithinDistance_UsesStDistanceSphere()
    {
        var filter = new SpatialDistancePredicate(
            SpatialOperator.DWithin,
            new PropertyReference("geometry"),
            new GeometryLiteral([0x01], 4326, "POINT"),
            new Literal(100.0, LiteralType.Number));

        var result = _translator.Translate(filter, _layer);

        Assert.Contains("ST_Distance_Sphere", result.Sql, StringComparison.Ordinal);
        Assert.Contains("<= @p1", result.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Translate_GeometryLiteralCrossSrid_ThrowsNotSupported()
    {
        var filter = new SpatialPredicate(
            SpatialOperator.Intersects,
            new PropertyReference("geometry"),
            new GeometryLiteral([0x01], 3857, "POINT"));

        var ex = Assert.Throws<NotSupportedException>(() => _translator.Translate(filter, _layer));
        Assert.Contains("Cross-SRID", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Translate_UnsupportedExpression_ThrowsNotSupported()
    {
        var filter = new ArrayPredicate(
            ArrayOperator.Contains,
            new PropertyReference("name"),
            new ArrayLiteral([new Literal("x", LiteralType.Text)]));

        var ex = Assert.Throws<NotSupportedException>(() => _translator.Translate(filter, _layer));
        Assert.Contains("ArrayPredicate", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Translate_PropertyReferenceUnknownField_ThrowsArgument()
    {
        var filter = new BinaryExpression(
            new PropertyReference("ghost_field"),
            BinaryOperator.Equal,
            new Literal("x", LiteralType.Text));

        Assert.Throws<ArgumentException>(() => _translator.Translate(filter, _layer));
    }

    [Fact]
    public void Translate_InEmptyValueList_ReturnsFalse()
    {
        var filter = new BinaryExpression(
            new PropertyReference("name"),
            BinaryOperator.In,
            new ValueList([]));

        var result = _translator.Translate(filter, _layer);

        Assert.Equal("FALSE", result.Sql);
    }

    [Fact]
    public void Translate_NotInEmptyValueList_ReturnsTrue()
    {
        var filter = new BinaryExpression(
            new PropertyReference("name"),
            BinaryOperator.NotIn,
            new ValueList([]));

        var result = _translator.Translate(filter, _layer);

        Assert.Equal("TRUE", result.Sql);
    }
}
