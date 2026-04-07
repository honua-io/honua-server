// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
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

        Assert.Contains("ST_Intersects(\"geom\", ST_GeomFromWKB($", result.Sql);
        Assert.Contains(wkb, result.WhereParameters);
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
        var result = _builder.BuildTemporalExtentQuery(TestLayerId, "created_date", Honua.Core.Features.Catalog.Domain.FieldType.DateTime);

        Assert.Contains("MIN(\"created_date\") AS min_value", result.Sql);
        Assert.Contains("MAX(\"created_date\") AS max_value", result.Sql);
    }
}
