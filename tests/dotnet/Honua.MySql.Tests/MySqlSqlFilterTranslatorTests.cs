// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.Metadata.Domain.V2;
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
    private readonly MetadataV2Resource _resource;

    public MySqlSqlFilterTranslatorTests()
    {
        _resource = CreateResource("parcels", MetadataV2GeometryType.Polygon);
    }

    [Fact]
    public void Translate_PropertyEqualsLiteral_BackticksFieldAndParameterizesValue()
    {
        var filter = new BinaryExpression(
            new PropertyReference("name"),
            BinaryOperator.Equal,
            new Literal("Acme", LiteralType.Text));

        var result = _translator.Translate(filter, _resource);

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

        var result = _translator.Translate(filter, _resource);

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

        var result = _translator.Translate(filter, _resource);

        Assert.Equal("(`area` > @p0 AND `name` = @p1)", result.Sql);
        Assert.Equal(2, result.Parameters.Count);
    }

    [Fact]
    public void Translate_IsNullUnary_GeneratesIsNullClause()
    {
        var filter = new UnaryExpression(UnaryOperator.IsNull, new PropertyReference("name"));

        var result = _translator.Translate(filter, _resource);

        Assert.Equal("`name` IS NULL", result.Sql);
    }

    [Fact]
    public void Translate_IntersectsSpatialPredicate_UsesMbrAndStIntersects()
    {
        var filter = new SpatialPredicate(
            SpatialOperator.Intersects,
            new PropertyReference("geometry"),
            new GeometryLiteral([0x01, 0x02], 4326, "POINT"));

        var result = _translator.Translate(filter, _resource);

        // Default flavor is Mysql, so the axis-order=long-lat option is emitted on
        // ST_GeomFromWKB to interpret canonical X/Y WKB on geographic SRSes.
        Assert.Equal(
            "MBRIntersects(`geometry`, ST_GeomFromWKB(@p0, 4326, 'axis-order=long-lat')) AND ST_Intersects(`geometry`, ST_GeomFromWKB(@p0, 4326, 'axis-order=long-lat'))",
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

        var result = _translator.Translate(filter, _resource);

        Assert.StartsWith(expectedFunction, result.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Translate_DWithinDistance_OnPointLayer_UsesStDistanceSphere()
    {
        // ST_Distance_Sphere requires a point resource; the polygon-typed default _resource is
        // covered by Translate_SpatialDistance_OnNonPointLayer_ThrowsNotSupported.
        var pointResource = CreateResource("stations", MetadataV2GeometryType.Point);

        var filter = new SpatialDistancePredicate(
            SpatialOperator.DWithin,
            new PropertyReference("geometry"),
            new GeometryLiteral([0x01], 4326, "POINT"),
            new Literal(100.0, LiteralType.Number));

        var result = _translator.Translate(filter, pointResource);

        Assert.Contains("ST_Distance_Sphere", result.Sql, StringComparison.Ordinal);
        Assert.Contains("<= @p1", result.Sql, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(MetadataV2GeometryType.Polygon)]
    [InlineData(MetadataV2GeometryType.MultiPolygon)]
    [InlineData(MetadataV2GeometryType.LineString)]
    [InlineData(MetadataV2GeometryType.MultiLineString)]
    public void Translate_SpatialDistance_OnNonPointLayer_ThrowsNotSupported(MetadataV2GeometryType geometryType)
    {
        var nonPointResource = CreateResource("non_point", geometryType);

        var filter = new SpatialDistancePredicate(
            SpatialOperator.DWithin,
            new PropertyReference("geometry"),
            new GeometryLiteral([0x01], 4326, "POINT"),
            new Literal(100.0, LiteralType.Number));

        var ex = Assert.Throws<NotSupportedException>(() => _translator.Translate(filter, nonPointResource));
        Assert.Contains("point layers", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Translate_BeyondDistance_OnPolygonLayer_ThrowsNotSupported()
    {
        var filter = new SpatialDistancePredicate(
            SpatialOperator.Beyond,
            new PropertyReference("geometry"),
            new GeometryLiteral([0x01], 4326, "POINT"),
            new Literal(50.0, LiteralType.Number));

        var ex = Assert.Throws<NotSupportedException>(() => _translator.Translate(filter, _resource));
        Assert.Contains("point layers", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Translate_DWithinDistance_OnMultiPointLayer_UsesStDistanceSphere()
    {
        var multiPointResource = CreateResource("swarm", MetadataV2GeometryType.MultiPoint);

        var filter = new SpatialDistancePredicate(
            SpatialOperator.DWithin,
            new PropertyReference("geometry"),
            new GeometryLiteral([0x01], 4326, "POINT"),
            new Literal(100.0, LiteralType.Number));

        var result = _translator.Translate(filter, multiPointResource);

        Assert.Contains("ST_Distance_Sphere", result.Sql, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(3857)]
    [InlineData(2154)]
    [InlineData(4269)]
    public void Translate_SpatialDistance_NonGeographicLayerSrid_ThrowsNotSupported(int layerSrid)
    {
        // ST_Distance_Sphere is documented for EPSG:4326 / SRID 0 only. Mirror the
        // FeatureQuery.SpatialFilter SRID guard so OGC API Features / OData distance
        // predicates against non-WGS84 layers fail with the same descriptive contract
        // instead of leaking ER_INVALID_GIS_DATA from the engine.
        var resource = CreateResource("stations", MetadataV2GeometryType.Point, layerSrid);

        var filter = new SpatialDistancePredicate(
            SpatialOperator.DWithin,
            new PropertyReference("geometry"),
            new GeometryLiteral([0x01], layerSrid, "POINT"),
            new Literal(100.0, LiteralType.Number));

        var ex = Assert.Throws<NotSupportedException>(() => _translator.Translate(filter, resource));
        Assert.Contains("EPSG:4326 or SRID 0", ex.Message, StringComparison.Ordinal);
        Assert.Contains(layerSrid.ToString(CultureInfo.InvariantCulture), ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Translate_DWithinDistance_Srid0PointLayer_UsesStDistanceSphere()
    {
        // SRID 0 (Cartesian, no SRS) is the second SRID MySQL ST_Distance_Sphere
        // documents as accepted. Mirror the MySqlFeatureQueryBuilder coverage so the
        // CQL2 path is consistent.
        var srid0Resource = CreateResource("stations_no_srs", MetadataV2GeometryType.Point, srid: 0);

        var filter = new SpatialDistancePredicate(
            SpatialOperator.DWithin,
            new PropertyReference("geometry"),
            new GeometryLiteral([0x01], 0, "POINT"),
            new Literal(100.0, LiteralType.Number));

        var result = _translator.Translate(filter, srid0Resource);

        Assert.Contains("ST_Distance_Sphere", result.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Translate_GeometryLiteralCrossSrid_ThrowsNotSupported()
    {
        var filter = new SpatialPredicate(
            SpatialOperator.Intersects,
            new PropertyReference("geometry"),
            new GeometryLiteral([0x01], 3857, "POINT"));

        var ex = Assert.Throws<NotSupportedException>(() => _translator.Translate(filter, _resource));
        Assert.Contains("Cross-SRID", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Translate_UnsupportedExpression_ThrowsNotSupported()
    {
        var filter = new ArrayPredicate(
            ArrayOperator.Contains,
            new PropertyReference("name"),
            new ArrayLiteral([new Literal("x", LiteralType.Text)]));

        var ex = Assert.Throws<NotSupportedException>(() => _translator.Translate(filter, _resource));
        Assert.Contains("ArrayPredicate", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Translate_PropertyReferenceUnknownField_ThrowsArgument()
    {
        var filter = new BinaryExpression(
            new PropertyReference("ghost_field"),
            BinaryOperator.Equal,
            new Literal("x", LiteralType.Text));

        Assert.Throws<ArgumentException>(() => _translator.Translate(filter, _resource));
    }

    [Fact]
    public void Translate_InEmptyValueList_ReturnsFalse()
    {
        var filter = new BinaryExpression(
            new PropertyReference("name"),
            BinaryOperator.In,
            new ValueList([]));

        var result = _translator.Translate(filter, _resource);

        Assert.Equal("FALSE", result.Sql);
    }

    [Fact]
    public void Translate_NotInEmptyValueList_ReturnsTrue()
    {
        var filter = new BinaryExpression(
            new PropertyReference("name"),
            BinaryOperator.NotIn,
            new ValueList([]));

        var result = _translator.Translate(filter, _resource);

        Assert.Equal("TRUE", result.Sql);
    }

    [Theory]
    [InlineData("geom")]
    [InlineData("shape")]
    [InlineData("geometry")]
    [InlineData("GEOM")]
    public void Translate_GeometryAliasIsNotNull_ResolvesToConfiguredGeometryColumn(string alias)
    {
        // When a CQL2 / OGC filter references a geometry alias (geom/shape/geometry) outside
        // a spatial predicate — e.g. `geom IS NOT NULL` — the translator must resolve the
        // alias to the layer's configured geometry column. Quoting the alias text directly
        // would emit SQL against a non-existent column whenever the backing geometry column
        // has a non-default name. Mirrors the alias handling in spatial-expression paths so
        // attribute and spatial filters agree.
        var resource = CreateResource(
            "non_default_geom",
            MetadataV2GeometryType.Polygon,
            geometryFieldName: "the_geom",
            fields:
            [
                Field("id", MetadataV2FieldType.BigInteger, nullable: false, semanticRoles: ["id.primary"]),
                Field("the_geom", MetadataV2FieldType.Geometry, nullable: false, semanticRoles: ["geometry.primary"]),
            ]);

        var filter = new UnaryExpression(UnaryOperator.IsNotNull, new PropertyReference(alias));

        var result = _translator.Translate(filter, resource);

        Assert.Equal("`the_geom` IS NOT NULL", result.Sql);
    }

    [Fact]
    public void Translate_GeometryAliasShadowedByAttribute_PrefersAttributeColumn()
    {
        // If a layer happens to expose an attribute literally named "geom" (and the geometry
        // column is something else), filters referencing "geom" target the attribute, not the
        // geometry. Field-by-name lookup wins over alias resolution to avoid silently
        // redirecting user filters away from the column they explicitly named.
        var resource = CreateResource(
            "geom_attribute",
            MetadataV2GeometryType.Polygon,
            geometryFieldName: "the_geom",
            fields:
            [
                Field("id", MetadataV2FieldType.BigInteger, nullable: false, semanticRoles: ["id.primary"]),
                Field("geom", MetadataV2FieldType.String),
                Field("the_geom", MetadataV2FieldType.Geometry, nullable: false, semanticRoles: ["geometry.primary"]),
            ]);

        var filter = new BinaryExpression(
            new PropertyReference("geom"),
            BinaryOperator.Equal,
            new Literal("x", LiteralType.Text));

        var result = _translator.Translate(filter, resource);

        Assert.Equal("`geom` = @p0", result.Sql);
    }

    [Fact]
    public void Translate_MariaDbFlavor_OmitsAxisOrderOption()
    {
        // CQL2 / OGC translator emits ST_GeomFromWKB for geometry literals; on MariaDB the
        // 3-arg axis-order form is unsupported and would error at evaluation time. The
        // flavor parameter must be honored so MariaDB users can run filters that include
        // geometry literals (STAC bbox, OGC API Features bbox, etc.).
        var translator = new MySqlSqlFilterTranslator(MySqlEngineFlavor.MariaDb);
        var filter = new SpatialPredicate(
            SpatialOperator.Intersects,
            new PropertyReference("geometry"),
            new GeometryLiteral([0x01, 0x02], 4326, "POINT"));

        var result = translator.Translate(filter, _resource);

        Assert.Contains("ST_GeomFromWKB(@p0, 4326)", result.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain("axis-order", result.Sql, StringComparison.Ordinal);
    }

    private static MetadataV2Resource CreateResource(
        string name,
        MetadataV2GeometryType geometryType,
        int srid = 4326,
        string geometryFieldName = "geometry",
        IReadOnlyList<MetadataV2Field>? fields = null)
        => new()
        {
            Metadata = new MetadataV2ObjectMetadata { Id = $"res-{name}", Name = name },
            Type = MetadataV2ResourceType.FeatureDataset,
            Spatial = new MetadataV2ResourceSpatial
            {
                SpatialReference = new MetadataV2SpatialReference
                {
                    Srid = srid,
                    Crs = $"EPSG:{srid}",
                    IsGeographic = srid is 4326 or 0,
                },
                GeometryType = geometryType,
                PrimaryGeometryField = geometryFieldName,
            },
            SchemaFields = fields ??
            [
                Field("id", MetadataV2FieldType.BigInteger, nullable: false, semanticRoles: ["id.primary"]),
                Field("name", MetadataV2FieldType.String),
                Field("area", MetadataV2FieldType.Double),
                Field(geometryFieldName, MetadataV2FieldType.Geometry, nullable: false, semanticRoles: ["geometry.primary"]),
            ],
        };

    private static MetadataV2Field Field(
        string name,
        MetadataV2FieldType type,
        bool nullable = true,
        params string[] semanticRoles)
        => new()
        {
            Name = name,
            Type = type,
            Nullable = nullable,
            SemanticRoles = semanticRoles,
        };
}
