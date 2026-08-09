// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Buffers.Binary;
using System.Collections.Immutable;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Queries.Filters;
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
    public void BuildSelectQuery_PaginationWithoutOrderBy_FallsBackToPrimaryKeyOrder()
    {
        var mapping = BuildMapping();
        var query = new FeatureQuery { Limit = 25, Offset = 50 };

        var result = SqlServerFeatureQueryBuilder.BuildSelectQuery(mapping, query, _attributeColumns);

        Assert.Contains("ORDER BY [objectid]", result.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain("(SELECT 1)", result.Sql, StringComparison.Ordinal);
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
    public void BuildSelectQuery_StacCandidateInWhere_UsesTsqlParameters()
    {
        var result = SqlServerFeatureQueryBuilder.BuildSelectQuery(
            BuildMapping(), new FeatureQuery { Where = "stac_id IN ('first', 'second')" }, ["stac_id"]);

        Assert.Contains("[stac_id] IN (@p0, @p1)", result.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain("->>", result.Sql, StringComparison.Ordinal);
        Assert.Equal(new object?[] { "first", "second" }, result.WhereParameters);
    }

    [Fact]
    public void BuildSelectQuery_WherePresentWithSqlFilter_PrefersWhereParser()
    {
        // FeatureServer always populates Where alongside the translated SqlFilter; the only
        // registered ISqlFilterTranslator emits Postgres syntax, so SqlServer must re-parse the
        // canonical Where text instead of pasting the Postgres-styled fragment into T-SQL.
        var mapping = BuildMapping();
        var postgresStyledFragment = new SqlFragment(
            "(attributes->>'name') = @p0",
            new object?[] { "Bravo" });
        var query = new FeatureQuery
        {
            Where = "name = 'Alpha'",
            SqlFilter = postgresStyledFragment
        };

        var result = SqlServerFeatureQueryBuilder.BuildSelectQuery(mapping, query, _attributeColumns);

        Assert.Contains("[name] = @p0", result.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain("attributes->>'name'", result.Sql, StringComparison.Ordinal);
        Assert.Single(result.WhereParameters);
        Assert.Equal("Alpha", result.WhereParameters[0]);
    }

    [Fact]
    public void BuildSelectQuery_SqlFilterOnlyWithoutWhere_Throws()
    {
        // The shared ISqlFilterTranslator pipeline emits Postgres-flavored SQL (JSONB ->>,
        // ::casts, ST_* functions). Passing that fragment to the SQL Server provider would
        // produce an opaque SqlException at runtime. The builder rejects it eagerly with a
        // clear diagnostic so callers route through FeatureQuery.Where instead.
        var mapping = BuildMapping();
        var fragment = new SqlFragment("[name] = @p0", new object?[] { "Charlie" });
        var query = new FeatureQuery { SqlFilter = fragment };

        var ex = Assert.Throws<NotSupportedException>(
            () => SqlServerFeatureQueryBuilder.BuildSelectQuery(mapping, query, _attributeColumns));

        Assert.Contains("SqlFilter", ex.Message, StringComparison.Ordinal);
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
    public void BuildSelectQuery_GeoServicesEwkb_BindsIsoFlavoredPlainWkbWithZmPreserved()
    {
        var mapping = BuildMapping();
        var ewkb = CreateEwkbPointZm();
        var query = new FeatureQuery
        {
            SpatialFilter = SpatialFilter.Create(ewkb, SpatialRelationship.Intersects, srid: 4326)
        };

        var result = SqlServerFeatureQueryBuilder.BuildSelectQuery(mapping, query, _attributeColumns);

        var boundWkb = Assert.IsType<byte[]>(Assert.Single(result.WhereParameters));
        var typeWord = BinaryPrimitives.ReadUInt32LittleEndian(boundWkb.AsSpan(1, sizeof(uint)));
        // The bound WKB must carry no embedded SRID (no high SRID bit, and no separate SRID
        // word) and must be in the ISO/OGC type-code flavor STGeomFromWKB expects for Z/M
        // (3001 = PointZM), not Honua's canonical PostGIS-style EWKB high-bit flavor
        // (0xC0000001) the input carried. See SqlServerGeographyWinding.NormalizeToPlainWkb.
        Assert.Equal(0u, typeWord & 0x20000000u);
        Assert.Equal(3001u, typeWord);
        Assert.Equal(ewkb.AsSpan(9).ToArray(), boundWkb.AsSpan(5).ToArray());
    }

    private static byte[] CreateEwkbPointZm()
    {
        var ewkb = new byte[1 + sizeof(uint) + sizeof(uint) + (4 * sizeof(double))];
        ewkb[0] = 1;
        BinaryPrimitives.WriteUInt32LittleEndian(ewkb.AsSpan(1), 0xE0000001u);
        BinaryPrimitives.WriteUInt32LittleEndian(ewkb.AsSpan(5), 4326u);
        BinaryPrimitives.WriteDoubleLittleEndian(ewkb.AsSpan(9), -1.25);
        BinaryPrimitives.WriteDoubleLittleEndian(ewkb.AsSpan(17), 2.75);
        BinaryPrimitives.WriteDoubleLittleEndian(ewkb.AsSpan(25), 3.5);
        BinaryPrimitives.WriteDoubleLittleEndian(ewkb.AsSpan(33), 4.25);
        return ewkb;
    }

    [Fact]
    public void BuildSelectQuery_WithinFilter_LeadsWithFilterGeometry()
    {
        // Esri esriSpatialRelWithin = filter geometry is within feature geometry, so the filter
        // geometry must be the calling instance: filter.STWithin(feature). Leading with the
        // feature column inverts the relationship and returns the wrong (empty) set (#2068).
        var mapping = BuildMapping();
        var query = new FeatureQuery
        {
            SpatialFilter = SpatialFilter.Create([0x01], SpatialRelationship.Within, srid: 4326)
        };

        var result = SqlServerFeatureQueryBuilder.BuildSelectQuery(mapping, query, _attributeColumns);

        Assert.Contains("geometry::STGeomFromWKB(@p0, 4326).STWithin([shape]) = 1", result.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildSelectQuery_ContainsFilter_LeadsWithFilterGeometry()
    {
        // Esri esriSpatialRelContains = filter geometry contains feature geometry, so the filter
        // geometry must be the calling instance: filter.STContains(feature) (#2068).
        var mapping = BuildMapping();
        var query = new FeatureQuery
        {
            SpatialFilter = SpatialFilter.Create([0x01], SpatialRelationship.Contains, srid: 4326)
        };

        var result = SqlServerFeatureQueryBuilder.BuildSelectQuery(mapping, query, _attributeColumns);

        Assert.Contains("geometry::STGeomFromWKB(@p0, 4326).STContains([shape]) = 1", result.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildSelectQuery_GeographyEnvelopeIntersects_Throws()
    {
        var mapping = BuildMapping(SqlServerGeometryColumnType.Geography);
        var query = new FeatureQuery
        {
            SpatialFilter = SpatialFilter.Create([0xFF], SpatialRelationship.EnvelopeIntersects, srid: 4326)
        };

        var ex = Assert.Throws<NotSupportedException>(
            () => SqlServerFeatureQueryBuilder.BuildSelectQuery(mapping, query, _attributeColumns));

        Assert.Contains("EnvelopeIntersects", ex.Message, StringComparison.Ordinal);
        Assert.Contains("geography", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildSelectQuery_GeographyIntersects_UsesGeographyStaticConstructor()
    {
        var mapping = BuildMapping(SqlServerGeometryColumnType.Geography);
        var query = new FeatureQuery
        {
            SpatialFilter = SpatialFilter.Create([0xFF], SpatialRelationship.Intersects, srid: 4326)
        };

        var result = SqlServerFeatureQueryBuilder.BuildSelectQuery(mapping, query, _attributeColumns);

        Assert.Contains("geography::STGeomFromWKB", result.Sql, StringComparison.Ordinal);
        Assert.Contains("[shape].STIntersects(geography::STGeomFromWKB(@p0, 4326)) = 1", result.Sql, StringComparison.Ordinal);
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
    public void BuildExtentQuery_GeographyMapping_UsesGeographyEnvelopeAggregateAndLatLongProperties()
    {
        var mapping = BuildMapping(SqlServerGeometryColumnType.Geography);

        var result = SqlServerFeatureQueryBuilder.BuildExtentQuery(mapping, query: null);

        Assert.Contains("geography::EnvelopeAggregate([shape])", result.Sql, StringComparison.Ordinal);
        Assert.Contains("envelope.STPointN(1).Long AS [min_x]", result.Sql, StringComparison.Ordinal);
        Assert.Contains("envelope.STPointN(1).Lat AS [min_y]", result.Sql, StringComparison.Ordinal);
        Assert.Contains("envelope.STPointN(3).Long AS [max_x]", result.Sql, StringComparison.Ordinal);
        Assert.Contains("envelope.STPointN(3).Lat AS [max_y]", result.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain(".STX", result.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain(".STY", result.Sql, StringComparison.Ordinal);
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

    // BH2-D01 regression: SRID resolves to 0 when both filter and mapping SRID are absent ->
    // SQL Server spatial predicates return NULL (not false) -> silent zero-row result.
    [Fact]
    public void BuildSelectQuery_SpatialFilter_NullFilterSridAndNullMappingSrid_Throws()
    {
        var mapping = BuildMapping(srid: null);
        var query = new FeatureQuery
        {
            SpatialFilter = SpatialFilter.Create([0x01, 0x02], SpatialRelationship.Intersects, srid: null)
        };

        var ex = Assert.Throws<NotSupportedException>(
            () => SqlServerFeatureQueryBuilder.BuildSelectQuery(mapping, query, _attributeColumns));

        Assert.Contains("SRID", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("zero rows", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildSelectQuery_SpatialFilter_NullFilterSridWithMappingSrid_UsesMappingSrid()
    {
        // When the filter carries no SRID but the mapping has a storage SRID, the mapping
        // SRID is used for the geometry constructor — no guard should fire.
        var mapping = BuildMapping(srid: 4326);
        var query = new FeatureQuery
        {
            SpatialFilter = SpatialFilter.Create([0x01], SpatialRelationship.Intersects, srid: null)
        };

        // Should NOT throw — mapping SRID resolves the geometry SRID.
        var result = SqlServerFeatureQueryBuilder.BuildSelectQuery(mapping, query, _attributeColumns);

        Assert.Contains("STGeomFromWKB(@p0, 4326)", result.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildSelectQuery_TemporalFilter_ThrowsNotSupported()
    {
        // Temporal analytics went GA (#2429); providers that cannot translate a temporal
        // predicate must fail loud rather than silently return rows outside the requested
        // window. The SQL Server provider has no T-SQL temporal translation in this slice.
        var query = new FeatureQuery
        {
            TemporalFilter = new TemporalFilter
            {
                PropertyName = "observed_at",
                PropertyType = TemporalPropertyType.DateTime
            }
        };

        var ex = Assert.Throws<NotSupportedException>(
            () => SqlServerFeatureQueryBuilder.BuildSelectQuery(BuildMapping(), query, _attributeColumns));
        Assert.Contains("Temporal filters", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CountObjectIdsAndExtentQueries_TemporalFilter_ThrowNotSupported()
    {
        // The guard must fire on every build path, not just SELECT, so a temporal window
        // can never be silently dropped by count/id/extent envelopes either.
        var query = new FeatureQuery
        {
            TemporalFilter = new TemporalFilter
            {
                PropertyName = "observed_at",
                PropertyType = TemporalPropertyType.DateTime
            }
        };
        var mapping = BuildMapping();

        Assert.Throws<NotSupportedException>(() => SqlServerFeatureQueryBuilder.BuildCountQuery(mapping, query));
        Assert.Throws<NotSupportedException>(() => SqlServerFeatureQueryBuilder.BuildObjectIdsQuery(mapping, query));
        Assert.Throws<NotSupportedException>(() => SqlServerFeatureQueryBuilder.BuildExtentQuery(mapping, query));
    }
}
