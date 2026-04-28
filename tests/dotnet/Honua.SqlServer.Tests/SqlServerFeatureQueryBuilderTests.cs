// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.SqlServer.Features.FeatureStore.Services;

namespace Honua.SqlServer.Tests;

/// <summary>
/// Unit tests for the SQL Server query builder. Verifies T-SQL dialect, identifier
/// quoting, parameter binding, and rejection of unsupported relationships/inputs.
/// </summary>
public class SqlServerFeatureQueryBuilderTests
{
    private const int TestLayerId = 7;

    private static readonly IReadOnlyList<string> _attributeColumns = ["name", "area", "category"];

    private static SqlServerLayerMapping BuildMapping(
        SqlServerGeometryColumnType columnType = SqlServerGeometryColumnType.Geometry,
        string? schema = "dbo",
        int? srid = 4326)
    {
        var providerOptions = new Dictionary<string, string>
        {
            [SqlServerProviderOptions.GeometryType] = columnType == SqlServerGeometryColumnType.Geography ? "geography" : "geometry"
        };

        var storage = new LayerStorageMapping(
            TableName: "parcels",
            SchemaName: schema,
            CatalogName: null,
            DatabaseName: null,
            PrimaryKeyColumn: "objectid",
            GeometryColumn: "shape",
            StorageSrid: srid,
            ProviderOptions: providerOptions);

        return SqlServerLayerMapping.FromStorage(TestLayerId, storage);
    }

    [Fact]
    public void BuildSelectQuery_DefaultQuery_ProjectsKeyAndGeometryAndAttributes()
    {
        var mapping = BuildMapping();

        var result = SqlServerFeatureQueryBuilder.BuildSelectQuery(mapping, new FeatureQuery(), _attributeColumns);

        Assert.Contains("[objectid] AS [__objectid]", result.Sql, StringComparison.Ordinal);
        Assert.Contains("[shape].STAsBinary() AS [__geometry]", result.Sql, StringComparison.Ordinal);
        Assert.Contains("[name]", result.Sql, StringComparison.Ordinal);
        Assert.Contains("[area]", result.Sql, StringComparison.Ordinal);
        Assert.Contains("[category]", result.Sql, StringComparison.Ordinal);
        Assert.Contains("FROM [dbo].[parcels]", result.Sql, StringComparison.Ordinal);
        Assert.Contains("WHERE 1=1", result.Sql, StringComparison.Ordinal);
        Assert.Empty(result.WhereParameters);
    }

    [Fact]
    public void BuildSelectQuery_ExcludeAttributes_OmitsAttributeColumns()
    {
        var mapping = BuildMapping();
        var query = new FeatureQuery { ExcludeAttributes = true };

        var result = SqlServerFeatureQueryBuilder.BuildSelectQuery(mapping, query, _attributeColumns);

        Assert.DoesNotContain("[name]", result.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain("[area]", result.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain("[category]", result.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildSelectQuery_OutFields_LimitsProjection()
    {
        var mapping = BuildMapping();
        var query = new FeatureQuery { OutFields = ImmutableArray.Create("name", "area") };

        var result = SqlServerFeatureQueryBuilder.BuildSelectQuery(mapping, query, _attributeColumns);

        Assert.Contains("[name]", result.Sql, StringComparison.Ordinal);
        Assert.Contains("[area]", result.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain("[category]", result.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildSelectQuery_PaginationWithoutOrderBy_AppendsStableFallbackOrder()
    {
        var mapping = BuildMapping();
        var query = new FeatureQuery { Limit = 25, Offset = 50 };

        var result = SqlServerFeatureQueryBuilder.BuildSelectQuery(mapping, query, _attributeColumns);

        Assert.Contains("ORDER BY (SELECT 1)", result.Sql, StringComparison.Ordinal);
        Assert.Contains("OFFSET @p0 ROWS", result.Sql, StringComparison.Ordinal);
        Assert.Contains("FETCH NEXT @p1 ROWS ONLY", result.Sql, StringComparison.Ordinal);
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

        var result = SqlServerFeatureQueryBuilder.BuildSelectQuery(mapping, query, _attributeColumns);

        Assert.Contains("ORDER BY [name] DESC, [area] ASC", result.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain("(SELECT 1)", result.Sql, StringComparison.Ordinal);
        Assert.Contains("FETCH NEXT @p1 ROWS ONLY", result.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildSelectQuery_WhereWithLiteralAndNullCheck_ProducesParameterizedSql()
    {
        var mapping = BuildMapping();
        var query = new FeatureQuery { Where = "name = 'Alpha' AND category IS NOT NULL" };

        var result = SqlServerFeatureQueryBuilder.BuildSelectQuery(mapping, query, _attributeColumns);

        Assert.Contains("[name] = @p0", result.Sql, StringComparison.Ordinal);
        Assert.Contains("[category] IS NOT NULL", result.Sql, StringComparison.Ordinal);
        Assert.Single(result.WhereParameters);
        Assert.Equal("Alpha", result.WhereParameters[0]);
    }

    [Fact]
    public void BuildSelectQuery_ObjectIdsFilter_GeneratesInClause()
    {
        var mapping = BuildMapping();
        var query = new FeatureQuery { ObjectIds = ImmutableArray.Create(1L, 2L, 3L) };

        var result = SqlServerFeatureQueryBuilder.BuildSelectQuery(mapping, query, _attributeColumns);

        Assert.Contains("[objectid] IN (@p0, @p1, @p2)", result.Sql, StringComparison.Ordinal);
        Assert.Equal(3, result.WhereParameters.Count);
    }

    [Fact]
    public void BuildSelectQuery_IntersectsFilter_UsesGeometryStaticConstructor()
    {
        var mapping = BuildMapping();
        var query = new FeatureQuery
        {
            SpatialFilter = SpatialFilter.Create([0x01, 0x02, 0x03], SpatialRelationship.Intersects, srid: 4326)
        };

        var result = SqlServerFeatureQueryBuilder.BuildSelectQuery(mapping, query, _attributeColumns);

        Assert.Contains("[shape].STIntersects(geometry::STGeomFromWKB(@p0, 4326)) = 1", result.Sql, StringComparison.Ordinal);
        Assert.Equal(new byte[] { 0x01, 0x02, 0x03 }, result.WhereParameters[0]);
    }

    [Fact]
    public void BuildSelectQuery_GeographyEnvelopeIntersects_UsesGeographyStaticConstructor()
    {
        var mapping = BuildMapping(SqlServerGeometryColumnType.Geography);
        var query = new FeatureQuery
        {
            SpatialFilter = SpatialFilter.Create([0xFF], SpatialRelationship.EnvelopeIntersects, srid: 4326)
        };

        var result = SqlServerFeatureQueryBuilder.BuildSelectQuery(mapping, query, _attributeColumns);

        Assert.Contains("geography::STGeomFromWKB", result.Sql, StringComparison.Ordinal);
        Assert.Contains("[shape].STEnvelope().STIntersects", result.Sql, StringComparison.Ordinal);
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
            () => SqlServerFeatureQueryBuilder.BuildSelectQuery(mapping, query, _attributeColumns));
    }

    [Fact]
    public void BuildCountQuery_ProducesCountBigSelect()
    {
        var mapping = BuildMapping();

        var result = SqlServerFeatureQueryBuilder.BuildCountQuery(mapping, new FeatureQuery());

        Assert.Contains("SELECT COUNT_BIG(*)", result.Sql, StringComparison.Ordinal);
        Assert.Contains("FROM [dbo].[parcels]", result.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildExtentQuery_UsesEnvelopeAggregate()
    {
        var mapping = BuildMapping();

        var result = SqlServerFeatureQueryBuilder.BuildExtentQuery(mapping, query: null);

        Assert.Contains("geometry::EnvelopeAggregate([shape])", result.Sql, StringComparison.Ordinal);
        Assert.Contains("envelope.STPointN(1).STX AS [min_x]", result.Sql, StringComparison.Ordinal);
        Assert.Contains("envelope.STPointN(3).STY AS [max_y]", result.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildExtentQuery_GeographyMapping_UsesGeographyEnvelopeAggregate()
    {
        var mapping = BuildMapping(SqlServerGeometryColumnType.Geography);

        var result = SqlServerFeatureQueryBuilder.BuildExtentQuery(mapping, query: null);

        Assert.Contains("geography::EnvelopeAggregate([shape])", result.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildObjectIdsQuery_ReturnsOnlyPrimaryKey()
    {
        var mapping = BuildMapping();
        var query = new FeatureQuery { Where = "name = 'Alpha'" };

        var result = SqlServerFeatureQueryBuilder.BuildObjectIdsQuery(mapping, query);

        Assert.StartsWith("SELECT [objectid] AS [__objectid]", result.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain("STAsBinary", result.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildSelectQuery_InvalidFieldName_RejectsInjection()
    {
        var mapping = BuildMapping();
        var query = new FeatureQuery { Where = "name; DROP TABLE foo = 'bar'" };

        Assert.Throws<ArgumentException>(
            () => SqlServerFeatureQueryBuilder.BuildSelectQuery(mapping, query, _attributeColumns));
    }

    [Fact]
    public void FromStorage_UnsupportedGeometryType_Throws()
    {
        var providerOptions = new Dictionary<string, string>
        {
            [SqlServerProviderOptions.GeometryType] = "wkt"
        };

        var storage = new LayerStorageMapping(
            TableName: "parcels",
            SchemaName: "dbo",
            PrimaryKeyColumn: "objectid",
            GeometryColumn: "shape",
            StorageSrid: 4326,
            ProviderOptions: providerOptions);

        Assert.Throws<ArgumentException>(() => SqlServerLayerMapping.FromStorage(TestLayerId, storage));
    }

    [Fact]
    public void FromStorage_DefaultsToGeometryAndDboSchema()
    {
        var storage = new LayerStorageMapping(
            TableName: "parcels",
            PrimaryKeyColumn: "objectid",
            GeometryColumn: "shape",
            StorageSrid: 4326);

        var mapping = SqlServerLayerMapping.FromStorage(TestLayerId, storage);

        Assert.Equal(SqlServerGeometryColumnType.Geometry, mapping.GeometryColumnType);
        Assert.Equal("[dbo].[parcels]", mapping.QuotedTableReference);
    }

    [Fact]
    public void FromStorage_RejectsInvalidIdentifier()
    {
        var storage = new LayerStorageMapping(
            TableName: "parcels; DROP TABLE x",
            PrimaryKeyColumn: "objectid",
            GeometryColumn: "shape",
            StorageSrid: 4326);

        Assert.Throws<ArgumentException>(() => SqlServerLayerMapping.FromStorage(TestLayerId, storage));
    }
}
