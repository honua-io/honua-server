// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Queries.Filters;
using Honua.DuckDB.Features.FeatureStore.Services;
using Honua.DuckDB.Features.Infrastructure;

namespace Honua.DuckDB.Tests;

/// <summary>
/// Unit tests for DuckDB SQL query generation.
/// Verifies correct DuckDB SQL dialect, parameter handling, and unsupported operation rejection.
/// </summary>
public class DuckDBFeatureQueryBuilderTests
{
    private readonly DuckDBFeatureQueryBuilder _builder;
    private const int TestLayerId = 0;

    public DuckDBFeatureQueryBuilderTests()
    {
        var mapping = new DuckDBLayerMapping
        {
            LayerId = TestLayerId,
            TableName = "parcels",
            GeometryColumn = "geom",
            ObjectIdColumn = "id",
            Srid = 4326,
            AttributeColumns = ["name", "area", "type"]
        };

        var registry = new DuckDBLayerRegistry([mapping]);
        _builder = new DuckDBFeatureQueryBuilder(registry);
    }

    [Fact]
    public void BuildSelectQuery_DefaultQuery_GeneratesCorrectSql()
    {
        var query = new FeatureQuery();

        var result = _builder.BuildSelectQuery(TestLayerId, query);

        Assert.Contains("SELECT \"id\", ST_AsWKB(\"geom\")", result.Sql);
        Assert.Contains("\"name\", \"area\", \"type\"", result.Sql);
        Assert.Contains("FROM \"parcels\"", result.Sql);
        Assert.Contains("WHERE 1=1", result.Sql);
        Assert.Empty(result.WhereParameters);
    }

    [Fact]
    public void BuildSelectQuery_WithOutFields_ProjectsOnlyRequestedColumns()
    {
        var query = new FeatureQuery
        {
            OutFields = ImmutableArray.Create("name", "area")
        };

        var result = _builder.BuildSelectQuery(TestLayerId, query);

        Assert.Contains("\"name\", \"area\"", result.Sql);
        Assert.DoesNotContain("\"type\"", result.Sql);
    }

    [Fact]
    public void BuildSelectQuery_ExcludeAttributes_NoAttributeColumns()
    {
        var query = new FeatureQuery { ExcludeAttributes = true };

        var result = _builder.BuildSelectQuery(TestLayerId, query);

        Assert.DoesNotContain("\"name\"", result.Sql);
        Assert.DoesNotContain("\"area\"", result.Sql);
        Assert.DoesNotContain("\"type\"", result.Sql);
    }

    [Fact]
    public void BuildSelectQuery_WithPagination_GeneratesLimitOffset()
    {
        var query = new FeatureQuery { Limit = 10, Offset = 20 };

        var result = _builder.BuildSelectQuery(TestLayerId, query);

        Assert.Contains("LIMIT $1", result.Sql);
        Assert.Contains("OFFSET $2", result.Sql);
        Assert.Equal(10, result.WhereParameters[0]);
        Assert.Equal(20, result.WhereParameters[1]);
    }

    [Fact]
    public void BuildSelectQuery_WithObjectIds_GeneratesInClause()
    {
        var query = new FeatureQuery
        {
            ObjectIds = ImmutableArray.Create(1L, 2L, 3L)
        };

        var result = _builder.BuildSelectQuery(TestLayerId, query);

        Assert.Contains("\"id\" IN ($1, $2, $3)", result.Sql);
        Assert.Equal(3, result.WhereParameters.Count);
    }

    [Fact]
    public void BuildCountQuery_GeneratesCorrectSql()
    {
        var query = new FeatureQuery();

        var result = _builder.BuildCountQuery(TestLayerId, query);

        Assert.Contains("SELECT COUNT(*) FROM \"parcels\" WHERE 1=1", result.Sql);
    }

    [Fact]
    public void BuildObjectIdsQuery_GeneratesCorrectSql()
    {
        var query = new FeatureQuery();

        var result = _builder.BuildObjectIdsQuery(TestLayerId, query);

        Assert.Contains("SELECT \"id\" FROM \"parcels\" WHERE 1=1", result.Sql);
    }

    [Fact]
    public void BuildExtentQuery_GeneratesStExtent()
    {
        var result = _builder.BuildExtentQuery(TestLayerId, null);

        Assert.Contains("MIN(ST_XMin(\"geom\"))", result.Sql);
        Assert.Contains("MIN(ST_YMin(\"geom\"))", result.Sql);
        Assert.Contains("MAX(ST_XMax(\"geom\"))", result.Sql);
        Assert.Contains("MAX(ST_YMax(\"geom\"))", result.Sql);
    }

    [Fact]
    public void BuildSelectGeoJsonQuery_UsesST_AsGeoJSON()
    {
        var query = new FeatureQuery();

        var result = _builder.BuildSelectGeoJsonQuery(TestLayerId, query);

        Assert.Contains("ST_AsGeoJSON(\"geom\")", result.Sql);
    }

    [Fact]
    public void BuildStatisticsQuery_GeneratesAggregates()
    {
        var query = new FeatureQuery
        {
            OutStatistics = ImmutableArray.Create(
                new StatisticDefinition
                {
                    StatisticType = StatisticType.Sum,
                    OnStatisticField = "area",
                    OutStatisticFieldName = "total_area"
                },
                new StatisticDefinition
                {
                    StatisticType = StatisticType.Count,
                    OnStatisticField = "name",
                    OutStatisticFieldName = "count_name"
                }),
            GroupByFields = ImmutableArray.Create("type")
        };

        var result = _builder.BuildStatisticsQuery(TestLayerId, query);

        Assert.Contains("SUM(\"area\") AS \"total_area\"", result.Sql);
        Assert.Contains("COUNT(\"name\") AS \"count_name\"", result.Sql);
        Assert.Contains("GROUP BY \"type\"", result.Sql);
    }

    [Fact]
    public void BuildOptimizedSelectQuery_IncludesWindowCount()
    {
        var query = new FeatureQuery { Limit = 10, Offset = 0 };

        var result = _builder.BuildOptimizedSelectQuery(TestLayerId, query);

        Assert.Contains("COUNT(*) OVER() AS __honua_total_count", result.Sql);
    }

    [Fact]
    public void BuildSelectQuery_WithSpatialFilter_GeneratesStIntersects()
    {
        var wkb = new byte[] { 1, 2, 3, 4 };
        var query = new FeatureQuery
        {
            SpatialFilter = SpatialFilter.Create(wkb, SpatialRelationship.Intersects, 4326)
        };

        var result = _builder.BuildSelectQuery(TestLayerId, query);

        // Filter SRID matches layer SRID (4326) — no transform required.
        Assert.Contains("ST_Intersects(\"geom\", ST_GeomFromWKB($", result.Sql);
        Assert.DoesNotContain("ST_Transform(", result.Sql);
        Assert.Contains(wkb, result.WhereParameters);
    }

    [Fact]
    public void BuildSelectQuery_WithinFilter_LeadsWithFilterGeometry()
    {
        // Esri esriSpatialRelWithin = filter geometry is within feature geometry, so the filter
        // geometry must be the FIRST operand: ST_Within(filter, feature). Leading with the
        // feature column inverts the relationship and returns the wrong (empty) set (#2068).
        var wkb = new byte[] { 1, 2, 3, 4 };
        var query = new FeatureQuery
        {
            SpatialFilter = SpatialFilter.Create(wkb, SpatialRelationship.Within, 4326)
        };

        var result = _builder.BuildSelectQuery(TestLayerId, query);

        Assert.Contains("ST_Within(ST_GeomFromWKB($", result.Sql);
        Assert.DoesNotContain("ST_Within(\"geom\"", result.Sql);
    }

    [Fact]
    public void BuildSelectQuery_ContainsFilter_LeadsWithFilterGeometry()
    {
        // Esri esriSpatialRelContains = filter geometry contains feature geometry: the filter
        // geometry must be the FIRST operand, ST_Contains(filter, feature) (#2068).
        var wkb = new byte[] { 1, 2, 3, 4 };
        var query = new FeatureQuery
        {
            SpatialFilter = SpatialFilter.Create(wkb, SpatialRelationship.Contains, 4326)
        };

        var result = _builder.BuildSelectQuery(TestLayerId, query);

        Assert.Contains("ST_Contains(ST_GeomFromWKB($", result.Sql);
        Assert.DoesNotContain("ST_Contains(\"geom\"", result.Sql);
    }

    [Fact]
    public void BuildSelectQuery_SpatialFilter_DifferentSrid_TransformsToLayerSrid()
    {
        // Layer is 4326; client supplies a Web Mercator (3857) filter geometry.
        // The SQL must transform the filter into the layer SRID before predicates run.
        var wkb = new byte[] { 1, 2, 3, 4 };
        var query = new FeatureQuery
        {
            SpatialFilter = SpatialFilter.Create(wkb, SpatialRelationship.Intersects, 3857)
        };

        var result = _builder.BuildSelectQuery(TestLayerId, query);

        Assert.Contains("ST_Transform(ST_GeomFromWKB($", result.Sql);
        Assert.Contains("'EPSG:3857', 'EPSG:4326', always_xy := true", result.Sql);
        Assert.Contains("ST_Intersects(\"geom\", ST_Transform(ST_GeomFromWKB($", result.Sql);
        Assert.Contains(wkb, result.WhereParameters);
    }

    [Fact]
    public void BuildSelectQuery_OutputSrid_ReprojectsWithAlwaysXy()
    {
        // Layer is 4326; client requests Web Mercator (3857) output. The output geometry
        // expression must reproject with always_xy := true so DuckDB keeps X=lon/Y=lat order
        // and does not transpose axes for the geographic source CRS.
        var query = new FeatureQuery
        {
            OutputSrid = 3857
        };

        var result = _builder.BuildSelectQuery(TestLayerId, query);

        Assert.Contains("ST_Transform(\"geom\", 'EPSG:4326', 'EPSG:3857', always_xy := true)", result.Sql);
    }

    [Fact]
    public void BuildSelectQuery_SpatialFilter_NoSrid_DoesNotTransform()
    {
        // When the filter SRID is unspecified the builder should not invent a transform;
        // the geometry is assumed to already be in the layer's CRS.
        var wkb = new byte[] { 1, 2, 3, 4 };
        var query = new FeatureQuery
        {
            SpatialFilter = SpatialFilter.Create(wkb, SpatialRelationship.Intersects)
        };

        var result = _builder.BuildSelectQuery(TestLayerId, query);

        Assert.Contains("ST_Intersects(\"geom\", ST_GeomFromWKB($", result.Sql);
        Assert.DoesNotContain("ST_Transform(", result.Sql);
    }

    [Fact]
    public void BuildMvtTileQuery_ThrowsNotSupported()
    {
        Assert.Throws<NotSupportedException>(() =>
            _builder.BuildMvtTileQuery(TestLayerId, 0, 0, 0, null,
                new Core.Features.Tiles.TileOptions(),
                new Core.Configuration.TileLimits()));
    }

    [Fact]
    public void BuildH3AggregationQuery_ThrowsNotSupported()
    {
        Assert.Throws<NotSupportedException>(() =>
            _builder.BuildH3AggregationQuery(TestLayerId, new FeatureQuery(),
                new H3AggregationQuery { Resolution = 5 }));
    }

    [Fact]
    public void BuildSelectFlatGeobufQuery_ThrowsNotSupported()
    {
        Assert.Throws<NotSupportedException>(() =>
            _builder.BuildSelectFlatGeobufQuery(null!, TestLayerId, new FeatureQuery()));
    }

    [Theory]
    [InlineData("valid_field", "Robert'; DROP TABLE--")]
    [InlineData("Robert'; DROP TABLE--", "valid_alias")]
    [InlineData("valid_field", "has space")]
    [InlineData("valid_field", "has.dot")]
    public void BuildStatisticsQuery_InvalidFieldNames_Throws(string onField, string outField)
    {
        var query = new FeatureQuery
        {
            OutStatistics = ImmutableArray.Create(
                new StatisticDefinition
                {
                    StatisticType = StatisticType.Count,
                    OnStatisticField = onField,
                    OutStatisticFieldName = outField
                })
        };

        Assert.Throws<ArgumentException>(() =>
            _builder.BuildStatisticsQuery(TestLayerId, query));
    }

    [Theory]
    [InlineData("'; DROP TABLE parcels--")]
    [InlineData("month'; DROP TABLE")]
    [InlineData("INVALID")]
    public void BuildDateBinsQuery_InvalidCalendarUnit_Throws(string calendarUnit)
    {
        var dateBin = new DateBinDefinition
        {
            BinField = "created_date",
            CalendarUnit = calendarUnit
        };

        Assert.Throws<ArgumentException>(() =>
            _builder.BuildDateBinsQuery(TestLayerId, new FeatureQuery(), dateBin));
    }

    [Theory]
    [InlineData("year")]
    [InlineData("Month")]
    [InlineData("DAY")]
    public void BuildDateBinsQuery_ValidCalendarUnit_Succeeds(string calendarUnit)
    {
        var dateBin = new DateBinDefinition
        {
            BinField = "name",
            CalendarUnit = calendarUnit
        };

        var result = _builder.BuildDateBinsQuery(TestLayerId, new FeatureQuery(), dateBin);

        Assert.Contains("date_trunc('" + calendarUnit.ToLowerInvariant() + "'", result.Sql);
    }

    [Fact]
    public void BuildSelectQuery_UnknownLayerId_Throws()
    {
        Assert.Throws<KeyNotFoundException>(() =>
            _builder.BuildSelectQuery(999, new FeatureQuery()));
    }

    /// <summary>
    /// Regression test for BH-016: a WHERE clause that references a field not in the
    /// layer mapping must produce ArgumentException (HTTP 400), not let the unknown
    /// column reach DuckDB where it causes a DbException (HTTP 500).
    /// </summary>
    [Theory]
    [InlineData("populaton > 1000")]          // misspelled — not in mapping
    [InlineData("nonexistent = 'foo'")]        // entirely unknown field
    [InlineData("nonexistent IS NULL")]        // null-check form
    [InlineData("nonexistent IS NOT NULL")]    // null-check with NOT
    public void BuildSelectQuery_WhereReferencesUnknownField_ThrowsArgumentException(string where)
    {
        var query = new FeatureQuery { Where = where };

        var ex = Assert.Throws<ArgumentException>(() =>
            _builder.BuildSelectQuery(TestLayerId, query));

        Assert.Contains("not defined on layer", ex.Message);
    }

    [Theory]
    [InlineData("name = 'foo'")]       // name is in mapping
    [InlineData("area > 100")]         // area is in mapping
    [InlineData("type IS NULL")]       // type is in mapping
    [InlineData("NAME = 'foo'")]       // case-insensitive match
    public void BuildSelectQuery_WhereReferencesKnownField_Succeeds(string where)
    {
        var query = new FeatureQuery { Where = where };

        // Should not throw — known fields must pass the layer-membership check.
        var result = _builder.BuildSelectQuery(TestLayerId, query);
        Assert.NotEmpty(result.Sql);
    }

    [Fact]
    public void BuildSelectQuery_WithOrderBy_GeneratesOrderClause()
    {
        var query = new FeatureQuery
        {
            OrderBy = ImmutableArray.Create(
                OrderByClause.Asc("name"),
                OrderByClause.Desc("area"))
        };

        var result = _builder.BuildSelectQuery(TestLayerId, query);

        Assert.Contains("ORDER BY \"name\" ASC, \"area\" DESC", result.Sql);
    }

    [Fact]
    public void BuildTemporalExtentQuery_GeneratesMinMax()
    {
        var result = _builder.BuildTemporalExtentQuery(TestLayerId, "created_date", TemporalPropertyType.DateTime);

        Assert.Contains("MIN(\"created_date\") AS min_value", result.Sql);
        Assert.Contains("MAX(\"created_date\") AS max_value", result.Sql);
    }

    [Fact]
    public void BuildSelectQuery_WithinDistance_GeographicSrid_UsesSpheroidDistance()
    {
        // Default fixture SRID is 4326 (geographic) — must use ST_Distance_Spheroid
        var wkb = new byte[] { 1, 2, 3, 4 };
        var query = new FeatureQuery
        {
            SpatialFilter = SpatialFilter.CreateDistanceFilter(wkb, 1000.0, DistanceUnit.Meters)
        };

        var result = _builder.BuildSelectQuery(TestLayerId, query);

        Assert.Contains("ST_Distance_Spheroid(", result.Sql);
        // DuckDB spheroid functions expect lat/lon axis order; the stored WKB is lon/lat, so both
        // operands are flipped before the geodesic distance is computed.
        Assert.Contains("ST_Distance_Spheroid(ST_FlipCoordinates(\"geom\"), ST_FlipCoordinates(", result.Sql);
        Assert.DoesNotContain("ST_DWithin(", result.Sql);
        Assert.Contains(1000.0, result.WhereParameters.OfType<double>());
    }

    [Fact]
    public void BuildSelectQuery_WithinDistance_UnlistedGeographicSrid_Throws()
    {
        // EPSG:4674 (SIRGAS 2000) is a geographic degree CRS but is not in the geodesic allowlist.
        // Running planar ST_DWithin(metres) against degrees would match the whole planet, so the
        // builder must reject the distance filter rather than emit silently-wrong SQL (#2731).
        var geographicMapping = new DuckDBLayerMapping
        {
            LayerId = 1,
            TableName = "parcels_sirgas",
            GeometryColumn = "geom",
            ObjectIdColumn = "id",
            Srid = 4674,
            AttributeColumns = ["name"]
        };
        var registry = new DuckDBLayerRegistry([geographicMapping]);
        var builder = new DuckDBFeatureQueryBuilder(registry);

        var wkb = new byte[] { 1, 2, 3, 4 };
        var query = new FeatureQuery
        {
            SpatialFilter = SpatialFilter.CreateDistanceFilter(wkb, 1000.0, DistanceUnit.Meters)
        };

        Assert.Throws<NotSupportedException>(() => builder.BuildSelectQuery(1, query));
    }

    [Fact]
    public void BuildSelectQuery_WithinDistance_ProjectedSrid_UsesDWithin()
    {
        // UTM zone 10N (SRID 32610) is a projected CRS — meters are CRS units
        var projectedMapping = new DuckDBLayerMapping
        {
            LayerId = 1,
            TableName = "parcels_utm",
            GeometryColumn = "geom",
            ObjectIdColumn = "id",
            Srid = 32610,
            AttributeColumns = ["name"]
        };
        var registry = new DuckDBLayerRegistry([projectedMapping]);
        var projectedBuilder = new DuckDBFeatureQueryBuilder(registry);

        var wkb = new byte[] { 1, 2, 3, 4 };
        var query = new FeatureQuery
        {
            SpatialFilter = SpatialFilter.CreateDistanceFilter(wkb, 500.0, DistanceUnit.Meters)
        };

        var result = projectedBuilder.BuildSelectQuery(1, query);

        Assert.Contains("ST_DWithin(", result.Sql);
        Assert.DoesNotContain("ST_Distance_Spheroid(", result.Sql);
        Assert.Contains(500.0, result.WhereParameters.OfType<double>());
    }

    /// <summary>
    /// BH4-024: Permanent row-visibility filter must be included in the generated WHERE clause.
    /// The enforced filter is applied before any caller-supplied SqlFilter so it cannot be
    /// bypassed.
    /// </summary>
    [Fact]
    public void BuildSelectQuery_WithEnforcedSqlFilter_IncludesFilterInWhereClause()
    {
        // EnforcedSqlFilter uses @pN notation; DuckDB builder converts to positional $N.
        var fragment = new SqlFragment("\"status\" = @p0", new object?[] { "active" });
        var query = new FeatureQuery { EnforcedSqlFilter = fragment };

        var result = _builder.BuildSelectQuery(TestLayerId, query);

        Assert.Contains("AND (\"status\" = $1)", result.Sql);
        Assert.Contains("active", result.WhereParameters);
    }

    [Fact]
    public void BuildSelectQuery_WithEnforcedSqlFilterAndSqlFilter_EnforcedAppearsFirst()
    {
        var enforced = new SqlFragment("\"tenant_id\" = @p0", new object?[] { 42 });
        var caller = new SqlFragment("\"status\" = @p0", new object?[] { "active" });
        var query = new FeatureQuery { EnforcedSqlFilter = enforced, SqlFilter = caller };

        var result = _builder.BuildSelectQuery(TestLayerId, query);

        // Scope the ordering check to the WHERE clause so column names in SELECT don't skew positions.
        var whereStart = result.Sql.IndexOf("WHERE 1=1", StringComparison.Ordinal);
        Assert.True(whereStart >= 0, "SQL must contain WHERE 1=1");
        var whereClause = result.Sql[whereStart..];
        var enforcedPos = whereClause.IndexOf("tenant_id", StringComparison.Ordinal);
        var callerPos = whereClause.IndexOf("status", StringComparison.Ordinal);
        Assert.True(enforcedPos < callerPos, "EnforcedSqlFilter must precede SqlFilter in the generated SQL.");
        // Both parameter values should appear.
        Assert.Contains(42, result.WhereParameters.OfType<int>().Cast<object>());
        Assert.Contains("active", result.WhereParameters);
    }
}
