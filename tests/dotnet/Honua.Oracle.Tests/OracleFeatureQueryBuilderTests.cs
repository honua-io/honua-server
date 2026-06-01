// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Queries.Filters;
using Honua.Oracle.Features.FeatureStore.Services;

namespace Honua.Oracle.Tests;

/// <summary>
/// Unit tests for the Oracle query builder. Verifies Oracle SQL dialect, identifier
/// quoting, bind-parameter syntax, and spatial-filter correctness for each supported
/// relationship.
/// </summary>
public class OracleFeatureQueryBuilderTests
{
    private const int TestLayerId = 7;

    private static readonly IReadOnlyList<string> _attributeColumns = ["name", "area", "category"];

    private static OracleLayerMapping BuildMapping(string? schema = "GIS", int? srid = 4326)
    {
        var storage = new LayerStorageMapping(
            TableName: "PARCELS",
            SchemaName: schema,
            CatalogName: null,
            DatabaseName: null,
            PrimaryKeyColumn: "OBJECTID",
            GeometryColumn: "SHAPE",
            StorageSrid: srid);

        return OracleLayerMapping.FromStorage(TestLayerId, storage);
    }

    [Fact]
    public void BuildSelectQuery_DefaultQuery_ProjectsKeyAndGeometryAndAttributes()
    {
        var mapping = BuildMapping();

        var result = OracleFeatureQueryBuilder.BuildSelectQuery(mapping, new FeatureQuery(), _attributeColumns);

        Assert.Contains("\"OBJECTID\" AS \"__objectid\"", result.Sql, StringComparison.Ordinal);
        Assert.Contains("SDO_UTIL.TO_WKBGEOMETRY(\"SHAPE\") AS \"__geometry\"", result.Sql, StringComparison.Ordinal);
        Assert.Contains("\"name\"", result.Sql, StringComparison.Ordinal);
        Assert.Contains("\"area\"", result.Sql, StringComparison.Ordinal);
        Assert.Contains("\"category\"", result.Sql, StringComparison.Ordinal);
        Assert.Contains("FROM \"GIS\".\"PARCELS\"", result.Sql, StringComparison.Ordinal);
        Assert.Contains("WHERE 1=1", result.Sql, StringComparison.Ordinal);
        Assert.Empty(result.WhereParameters);
    }

    [Fact]
    public void BuildSelectQuery_NoSchema_OmitsOwnerQualifier()
    {
        var mapping = BuildMapping(schema: null);

        var result = OracleFeatureQueryBuilder.BuildSelectQuery(mapping, new FeatureQuery(), _attributeColumns);

        Assert.Contains("FROM \"PARCELS\"", result.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain("..\"PARCELS\"", result.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildSelectQuery_PaginationWithoutOrderBy_FallsBackToPrimaryKeyOrder()
    {
        var mapping = BuildMapping();
        var query = new FeatureQuery { Limit = 25, Offset = 50 };

        var result = OracleFeatureQueryBuilder.BuildSelectQuery(mapping, query, _attributeColumns);

        Assert.Contains("ORDER BY \"OBJECTID\"", result.Sql, StringComparison.Ordinal);
        Assert.Contains("OFFSET :p0 ROWS", result.Sql, StringComparison.Ordinal);
        Assert.Contains("FETCH NEXT :p1 ROWS ONLY", result.Sql, StringComparison.Ordinal);
        Assert.Equal(50, result.WhereParameters[0]);
        Assert.Equal(25, result.WhereParameters[1]);
    }

    [Fact]
    public void BuildSelectQuery_OrderByCustom_UsesProvidedColumns()
    {
        var mapping = BuildMapping();
        var query = new FeatureQuery
        {
            OrderBy = ImmutableArray.Create(OrderByClause.Desc("name"), OrderByClause.Asc("area")),
            Limit = 10
        };

        var result = OracleFeatureQueryBuilder.BuildSelectQuery(mapping, query, _attributeColumns);

        Assert.Contains("ORDER BY \"name\" DESC, \"area\" ASC", result.Sql, StringComparison.Ordinal);
        Assert.Contains("FETCH NEXT :p1 ROWS ONLY", result.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildSelectQuery_WhereWithLiteralAndNullCheck_ProducesParameterizedSql()
    {
        var mapping = BuildMapping();
        var query = new FeatureQuery { Where = "name = 'Alpha' AND category IS NOT NULL" };

        var result = OracleFeatureQueryBuilder.BuildSelectQuery(mapping, query, _attributeColumns);

        Assert.Contains("\"name\" = :p0", result.Sql, StringComparison.Ordinal);
        Assert.Contains("\"category\" IS NOT NULL", result.Sql, StringComparison.Ordinal);
        Assert.Single(result.WhereParameters);
        Assert.Equal("Alpha", result.WhereParameters[0]);
    }

    [Fact]
    public void BuildSelectQuery_ObjectIdsFilter_GeneratesInClause()
    {
        var mapping = BuildMapping();
        var query = new FeatureQuery { ObjectIds = ImmutableArray.Create(1L, 2L, 3L) };

        var result = OracleFeatureQueryBuilder.BuildSelectQuery(mapping, query, _attributeColumns);

        Assert.Contains("\"OBJECTID\" IN (:p0, :p1, :p2)", result.Sql, StringComparison.Ordinal);
        Assert.Equal(3, result.WhereParameters.Count);
    }

    [Fact]
    public void BuildSelectQuery_IntersectsFilter_UsesSdoRelateAnyInteract()
    {
        var mapping = BuildMapping();
        var query = new FeatureQuery
        {
            SpatialFilter = SpatialFilter.Create([0x01, 0x02, 0x03], SpatialRelationship.Intersects, srid: 4326)
        };

        var result = OracleFeatureQueryBuilder.BuildSelectQuery(mapping, query, _attributeColumns);

        Assert.Contains("SDO_RELATE(\"SHAPE\", SDO_UTIL.FROM_WKBGEOMETRY(:p0), 'mask=ANYINTERACT') = 'TRUE'", result.Sql, StringComparison.Ordinal);
        Assert.Equal(new byte[] { 0x01, 0x02, 0x03 }, result.WhereParameters[0]);
    }

    [Fact]
    public void BuildSelectQuery_WithinFilter_UsesInsideCoveredbyMask()
    {
        var mapping = BuildMapping();
        var query = new FeatureQuery
        {
            SpatialFilter = SpatialFilter.Create([0xAA], SpatialRelationship.Within, srid: 4326)
        };

        var result = OracleFeatureQueryBuilder.BuildSelectQuery(mapping, query, _attributeColumns);

        Assert.Contains("SDO_RELATE(\"SHAPE\", SDO_UTIL.FROM_WKBGEOMETRY(:p0), 'mask=INSIDE+COVEREDBY') = 'TRUE'", result.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildSelectQuery_ContainsFilter_UsesContainsCoversMask()
    {
        var mapping = BuildMapping();
        var query = new FeatureQuery
        {
            SpatialFilter = SpatialFilter.Create([0xBB], SpatialRelationship.Contains, srid: 4326)
        };

        var result = OracleFeatureQueryBuilder.BuildSelectQuery(mapping, query, _attributeColumns);

        Assert.Contains("SDO_RELATE(\"SHAPE\", SDO_UTIL.FROM_WKBGEOMETRY(:p0), 'mask=CONTAINS+COVERS') = 'TRUE'", result.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildSelectQuery_DisjointFilter_NegatesAnyInteract()
    {
        var mapping = BuildMapping();
        var query = new FeatureQuery
        {
            SpatialFilter = SpatialFilter.Create([0xCC], SpatialRelationship.Disjoint, srid: 4326)
        };

        var result = OracleFeatureQueryBuilder.BuildSelectQuery(mapping, query, _attributeColumns);

        Assert.Contains("NOT (SDO_RELATE(\"SHAPE\", SDO_UTIL.FROM_WKBGEOMETRY(:p0), 'mask=ANYINTERACT') = 'TRUE')", result.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildSelectQuery_EnvelopeIntersects_UsesSdoMbrColumn()
    {
        var mapping = BuildMapping();
        var query = new FeatureQuery
        {
            SpatialFilter = SpatialFilter.Create([0xDD], SpatialRelationship.EnvelopeIntersects, srid: 4326)
        };

        var result = OracleFeatureQueryBuilder.BuildSelectQuery(mapping, query, _attributeColumns);

        Assert.Contains("SDO_RELATE(SDO_GEOM.SDO_MBR(\"SHAPE\"), SDO_UTIL.FROM_WKBGEOMETRY(:p0), 'mask=ANYINTERACT') = 'TRUE'", result.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildSelectQuery_DistanceFilter_NotSupported_Throws()
    {
        var mapping = BuildMapping();
        var query = new FeatureQuery
        {
            SpatialFilter = SpatialFilter.CreateDistanceFilter([0x01], 1000, srid: 4326)
        };

        Assert.Throws<NotSupportedException>(
            () => OracleFeatureQueryBuilder.BuildSelectQuery(mapping, query, _attributeColumns));
    }

    [Fact]
    public void BuildSelectQuery_SqlFilterWithoutWhere_ThrowsNotSupported()
    {
        // The shared ISqlFilterTranslator pipeline registers a PostgreSQL translator, so
        // FeatureQuery.SqlFilter carries Postgres-flavored SQL (JSONB ->>, casts, ST_*).
        // Pasting it through to Oracle would emit invalid SQL; the builder must reject it
        // explicitly so callers see the limitation instead of an opaque ORA-* failure.
        var mapping = BuildMapping();
        var query = new FeatureQuery
        {
            SqlFilter = new SqlFragment("\"attributes\" ->> 'name' = @p0", new object?[] { "Alpha" })
        };

        var ex = Assert.Throws<NotSupportedException>(
            () => OracleFeatureQueryBuilder.BuildSelectQuery(mapping, query, _attributeColumns));

        Assert.Contains("SqlFilter", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Oracle", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildCountQuery_SqlFilterWithoutWhere_ThrowsNotSupported()
    {
        var mapping = BuildMapping();
        var query = new FeatureQuery
        {
            SqlFilter = new SqlFragment("\"attributes\" ->> 'name' = @p0", new object?[] { "Alpha" })
        };

        Assert.Throws<NotSupportedException>(
            () => OracleFeatureQueryBuilder.BuildCountQuery(mapping, query));
    }

    [Fact]
    public void BuildSelectQuery_SqlFilterIgnoredWhenWherePresent_UsesOracleParser()
    {
        // Canonical Where wins over SqlFilter — the docs promise the provider re-parses
        // Where with its own Oracle parser and ignores any Postgres-styled SqlFilter.
        var mapping = BuildMapping();
        var query = new FeatureQuery
        {
            Where = "name = 'Alpha'",
            SqlFilter = new SqlFragment("\"attributes\" ->> 'name' = @p0", new object?[] { "ShouldNotAppear" })
        };

        var result = OracleFeatureQueryBuilder.BuildSelectQuery(mapping, query, _attributeColumns);

        Assert.Contains("\"name\" = :p0", result.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain("attributes", result.Sql, StringComparison.Ordinal);
        Assert.Single(result.WhereParameters);
        Assert.Equal("Alpha", result.WhereParameters[0]);
    }

    [Fact]
    public void BuildCountQuery_ProducesCountSelect()
    {
        var mapping = BuildMapping();

        var result = OracleFeatureQueryBuilder.BuildCountQuery(mapping, new FeatureQuery());

        Assert.Contains("SELECT COUNT(*)", result.Sql, StringComparison.Ordinal);
        Assert.Contains("FROM \"GIS\".\"PARCELS\"", result.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildExtentQuery_UsesSdoAggrMbrAndOrdinates()
    {
        var mapping = BuildMapping();

        var result = OracleFeatureQueryBuilder.BuildExtentQuery(mapping, query: null);

        Assert.Contains("SDO_AGGR_MBR(\"SHAPE\")", result.Sql, StringComparison.Ordinal);
        Assert.Contains("t.mbb.SDO_ORDINATES(1) AS \"min_x\"", result.Sql, StringComparison.Ordinal);
        Assert.Contains("t.mbb.SDO_ORDINATES(4) AS \"max_y\"", result.Sql, StringComparison.Ordinal);
        Assert.Contains("FROM \"GIS\".\"PARCELS\"", result.Sql, StringComparison.Ordinal);
        Assert.Contains("WHERE \"SHAPE\" IS NOT NULL", result.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildObjectIdsQuery_ReturnsOnlyPrimaryKey()
    {
        var mapping = BuildMapping();
        var query = new FeatureQuery { Where = "name = 'Alpha'" };

        var result = OracleFeatureQueryBuilder.BuildObjectIdsQuery(mapping, query);

        Assert.StartsWith("SELECT \"OBJECTID\" AS \"__objectid\"", result.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain("TO_WKBGEOMETRY", result.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildSelectQuery_InvalidFieldName_RejectsInjection()
    {
        var mapping = BuildMapping();
        var query = new FeatureQuery { Where = "name; DROP TABLE foo = 'bar'" };

        Assert.Throws<ArgumentException>(
            () => OracleFeatureQueryBuilder.BuildSelectQuery(mapping, query, _attributeColumns));
    }

    [Fact]
    public void FromStorage_RejectsInvalidIdentifier()
    {
        var storage = new LayerStorageMapping(
            TableName: "PARCELS; DROP TABLE x",
            PrimaryKeyColumn: "OBJECTID",
            GeometryColumn: "SHAPE",
            StorageSrid: 4326);

        Assert.Throws<ArgumentException>(() => OracleLayerMapping.FromStorage(TestLayerId, storage));
    }

    [Fact]
    public void FromStorage_DefaultsToNoSchema()
    {
        var storage = new LayerStorageMapping(
            TableName: "PARCELS",
            PrimaryKeyColumn: "OBJECTID",
            GeometryColumn: "SHAPE",
            StorageSrid: 4326);

        var mapping = OracleLayerMapping.FromStorage(TestLayerId, storage);

        Assert.Null(mapping.SchemaName);
        Assert.Equal("\"PARCELS\"", mapping.QuotedTableReference);
    }
}
