// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Queries.Filters;
using Honua.Snowflake.Features.FeatureStore.Services;

namespace Honua.Snowflake.Tests;

/// <summary>
/// Unit tests for the Snowflake query builder. Verifies Snowflake SQL dialect, double-quoted
/// identifier handling, positional parameter binding, paging, spatial predicates, and rejection of
/// unsupported relationships and injection attempts. These tests run in normal CI; they do not
/// require a live Snowflake warehouse.
/// </summary>
[global::Xunit.Trait("Tier", "Fast")]
public class SnowflakeFeatureQueryBuilderTests
{
    private const int TestLayerId = 7;

    private static readonly IReadOnlyList<string> _attributeColumns = ["NAME", "AREA", "CATEGORY"];

    private static SnowflakeLayerMapping BuildMapping(
        SnowflakeGeometryColumnType columnType = SnowflakeGeometryColumnType.Geography,
        string? schema = "PUBLIC",
        string? database = "ANALYTICS",
        int? srid = 4326)
    {
        var providerOptions = new Dictionary<string, string>
        {
            [SnowflakeProviderOptions.GeometryType] = columnType == SnowflakeGeometryColumnType.Geometry ? "geometry" : "geography"
        };

        var storage = new LayerStorageMapping(
            TableName: "PARCELS",
            SchemaName: schema,
            CatalogName: null,
            DatabaseName: database,
            PrimaryKeyColumn: "OBJECTID",
            GeometryColumn: "SHAPE",
            StorageSrid: srid,
            ProviderOptions: providerOptions);

        return SnowflakeLayerMapping.FromStorage(TestLayerId, storage);
    }

    [Fact]
    public void BuildSelectQuery_TextSearch_BindsLiteralTermsInProviderDialect()
    {
        const string term = "Harbor%_'\\";
        var query = new FeatureQuery
        {
            TextSearch = new FeatureTextSearch(["NAME", "CATEGORY"],
                [new FeatureSearchTerm[] { new(term, false), new("closed", true) }]),
            Limit = 2,
            Offset = 1
        };
        var result = SnowflakeFeatureQueryBuilder.BuildSelectQuery(BuildMapping(), query, _attributeColumns);
        Assert.Contains("POSITION(", result.Sql, StringComparison.Ordinal);
        Assert.Contains(" OR ", result.Sql, StringComparison.Ordinal);
        Assert.Contains("NOT ", result.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain(term, result.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain("ILIKE", result.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain("attributes->>", result.Sql, StringComparison.Ordinal);
        Assert.Equal(term, result.WhereParameters[0]);
        Assert.Equal(term, result.WhereParameters[1]);
        Assert.Equal("closed", result.WhereParameters[2]);
        Assert.Equal("closed", result.WhereParameters[3]);
    }
    [Fact]
    public void BuildSelectQuery_DefaultQuery_ProjectsKeyGeometryAndAttributes()
    {
        var mapping = BuildMapping();

        var result = SnowflakeFeatureQueryBuilder.BuildSelectQuery(mapping, new FeatureQuery(), _attributeColumns);

        Assert.Contains("\"OBJECTID\" AS \"__objectid\"", result.Sql, StringComparison.Ordinal);
        Assert.Contains("ST_ASWKB(\"SHAPE\") AS \"__geometry\"", result.Sql, StringComparison.Ordinal);
        Assert.Contains("\"NAME\"", result.Sql, StringComparison.Ordinal);
        Assert.Contains("\"AREA\"", result.Sql, StringComparison.Ordinal);
        Assert.Contains("\"CATEGORY\"", result.Sql, StringComparison.Ordinal);
        Assert.Contains("FROM \"ANALYTICS\".\"PUBLIC\".\"PARCELS\"", result.Sql, StringComparison.Ordinal);
        Assert.Contains("WHERE 1=1", result.Sql, StringComparison.Ordinal);
        Assert.Empty(result.WhereParameters);
    }

    [Fact]
    public void BuildSelectQuery_ExcludeAttributes_OmitsAttributeColumns()
    {
        var mapping = BuildMapping();
        var query = new FeatureQuery { ExcludeAttributes = true };

        var result = SnowflakeFeatureQueryBuilder.BuildSelectQuery(mapping, query, _attributeColumns);

        Assert.DoesNotContain("\"NAME\"", result.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain("\"AREA\"", result.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain("\"CATEGORY\"", result.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildSelectQuery_EmptyOutFields_OmitsAttributeColumns()
    {
        var mapping = BuildMapping();

        var defaults = SnowflakeFeatureQueryBuilder.BuildSelectQuery(mapping, new FeatureQuery(), _attributeColumns);
        Assert.Contains("\"NAME\"", defaults.Sql, StringComparison.Ordinal);
        Assert.Contains("\"AREA\"", defaults.Sql, StringComparison.Ordinal);
        Assert.Contains("\"CATEGORY\"", defaults.Sql, StringComparison.Ordinal);

        var unset = SnowflakeFeatureQueryBuilder.BuildSelectQuery(
            mapping, new FeatureQuery { OutFields = default(ImmutableArray<string>) }, _attributeColumns);
        Assert.Contains("\"NAME\"", unset.Sql, StringComparison.Ordinal);
        Assert.Contains("\"AREA\"", unset.Sql, StringComparison.Ordinal);
        Assert.Contains("\"CATEGORY\"", unset.Sql, StringComparison.Ordinal);

        var result = SnowflakeFeatureQueryBuilder.BuildSelectQuery(
            mapping, new FeatureQuery { OutFields = ImmutableArray<string>.Empty }, _attributeColumns);

        Assert.DoesNotContain("\"NAME\"", result.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain("\"AREA\"", result.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain("\"CATEGORY\"", result.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildSelectQuery_OutFields_LimitsProjection()
    {
        var mapping = BuildMapping();
        var query = new FeatureQuery { OutFields = ImmutableArray.Create("NAME", "AREA") };

        var result = SnowflakeFeatureQueryBuilder.BuildSelectQuery(mapping, query, _attributeColumns);

        Assert.Contains("\"NAME\"", result.Sql, StringComparison.Ordinal);
        Assert.Contains("\"AREA\"", result.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain("\"CATEGORY\"", result.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildSelectQuery_PaginationWithoutOrderBy_FallsBackToPrimaryKeyOrder()
    {
        var mapping = BuildMapping();
        var query = new FeatureQuery { Limit = 25, Offset = 50 };

        var result = SnowflakeFeatureQueryBuilder.BuildSelectQuery(mapping, query, _attributeColumns);

        Assert.Contains("ORDER BY \"OBJECTID\"", result.Sql, StringComparison.Ordinal);
        Assert.Contains("LIMIT 25", result.Sql, StringComparison.Ordinal);
        Assert.Contains("OFFSET 50", result.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildSelectQuery_OrderByCustom_UsesProvidedColumns()
    {
        var mapping = BuildMapping();
        var query = new FeatureQuery
        {
            OrderBy = ImmutableArray.Create(OrderByClause.Desc("NAME"), OrderByClause.Asc("AREA")),
            Limit = 10
        };

        var result = SnowflakeFeatureQueryBuilder.BuildSelectQuery(mapping, query, _attributeColumns);

        Assert.Contains("ORDER BY \"NAME\" DESC, \"AREA\" ASC", result.Sql, StringComparison.Ordinal);
        Assert.Contains("LIMIT 10", result.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildSelectQuery_WhereWithLiteralAndNullCheck_ProducesParameterizedSql()
    {
        var mapping = BuildMapping();
        var query = new FeatureQuery { Where = "NAME = 'Alpha' AND CATEGORY IS NOT NULL" };

        var result = SnowflakeFeatureQueryBuilder.BuildSelectQuery(mapping, query, _attributeColumns);

        Assert.Contains("\"NAME\" = ?", result.Sql, StringComparison.Ordinal);
        Assert.Contains("\"CATEGORY\" IS NOT NULL", result.Sql, StringComparison.Ordinal);
        Assert.Single(result.WhereParameters);
        Assert.Equal("Alpha", result.WhereParameters[0]);
    }

    [Fact]
    public void BuildSelectQuery_StacCandidateInWhere_UsesSnowflakeParameters()
    {
        var result = SnowflakeFeatureQueryBuilder.BuildSelectQuery(
            BuildMapping(), new FeatureQuery { Where = "STAC_ID IN ('first', 'second')" }, ["STAC_ID"]);

        Assert.Contains("\"STAC_ID\" IN (?, ?)", result.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain("->>", result.Sql, StringComparison.Ordinal);
        Assert.Equal(new object?[] { "first", "second" }, result.WhereParameters);
    }

    [Fact]
    public void BuildSelectQuery_WithSqlFilter_Throws()
    {
        // The shared ISqlFilterTranslator pipeline registers a PostgreSQL translator; its emitted
        // SqlFilter is not valid Snowflake SQL, so the builder must reject it with a clear error.
        var mapping = BuildMapping();
        var query = new FeatureQuery
        {
            SqlFilter = new SqlFragment("(attributes->>'name') = @p0", new object?[] { "Bravo" })
        };

        Assert.Throws<NotSupportedException>(
            () => SnowflakeFeatureQueryBuilder.BuildSelectQuery(mapping, query, _attributeColumns));
    }

    [Fact]
    public void BuildSelectQuery_SupplementalSqlFilter_DoesNotEmitWhereThatIgnoresIt()
    {
        var query = new FeatureQuery
        {
            Where = "NAME = 'Alpha'",
            SqlFilter = new SqlFragment("(attributes->>'NAME') = @p0 AND observed_at > @p1", new object?[] { "Alpha", 1 }),
            SqlFilterSupplementsWhere = true
        };

        var ex = Assert.Throws<NotSupportedException>(
            () => SnowflakeFeatureQueryBuilder.BuildSelectQuery(BuildMapping(), query, _attributeColumns));
        Assert.Contains("Postgres-flavored", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildSelectQuery_WhereWithTranslatedSqlFilter_StillUsesWhere()
    {
        var query = new FeatureQuery
        {
            Where = "NAME = 'Alpha'",
            SqlFilter = new SqlFragment("(attributes->>'NAME') = @p0", new object?[] { "Alpha" })
        };

        var result = SnowflakeFeatureQueryBuilder.BuildSelectQuery(BuildMapping(), query, _attributeColumns);

        Assert.Contains("\"NAME\" = ?", result.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain("->>", result.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildSelectQuery_ObjectIdsFilter_GeneratesInClause()
    {
        var mapping = BuildMapping();
        var query = new FeatureQuery { ObjectIds = ImmutableArray.Create(1L, 2L, 3L) };

        var result = SnowflakeFeatureQueryBuilder.BuildSelectQuery(mapping, query, _attributeColumns);

        Assert.Contains("\"OBJECTID\" IN (?, ?, ?)", result.Sql, StringComparison.Ordinal);
        Assert.Equal(3, result.WhereParameters.Count);
    }

    [Fact]
    public void BuildSelectQuery_GeographyIntersects_UsesToGeographyConstructor()
    {
        var mapping = BuildMapping(SnowflakeGeometryColumnType.Geography);
        var query = new FeatureQuery
        {
            SpatialFilter = SpatialFilter.Create([0x01, 0x02, 0x03], SpatialRelationship.Intersects, srid: 4326)
        };

        var result = SnowflakeFeatureQueryBuilder.BuildSelectQuery(mapping, query, _attributeColumns);

        Assert.Contains("ST_INTERSECTS(\"SHAPE\", TO_GEOGRAPHY(?, TRUE))", result.Sql, StringComparison.Ordinal);
        Assert.Equal(new byte[] { 0x01, 0x02, 0x03 }, result.WhereParameters[0]);
    }

    [Fact]
    public void BuildSelectQuery_GeometryIntersects_UsesToGeometryConstructor()
    {
        var mapping = BuildMapping(SnowflakeGeometryColumnType.Geometry);
        var query = new FeatureQuery
        {
            SpatialFilter = SpatialFilter.Create([0xFF], SpatialRelationship.Intersects, srid: 4326)
        };

        var result = SnowflakeFeatureQueryBuilder.BuildSelectQuery(mapping, query, _attributeColumns);

        Assert.Contains("ST_INTERSECTS(\"SHAPE\", TO_GEOMETRY(?, TRUE))", result.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildSelectQuery_EnvelopeIntersects_MapsToIntersects()
    {
        var mapping = BuildMapping();
        var query = new FeatureQuery
        {
            SpatialFilter = SpatialFilter.Create([0x01], SpatialRelationship.EnvelopeIntersects, srid: 4326)
        };

        var result = SnowflakeFeatureQueryBuilder.BuildSelectQuery(mapping, query, _attributeColumns);

        Assert.Contains("ST_INTERSECTS(\"SHAPE\"", result.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildSelectQuery_DisjointFilter_MapsToStDisjoint()
    {
        var mapping = BuildMapping();
        var query = new FeatureQuery
        {
            SpatialFilter = SpatialFilter.Create([0x01], SpatialRelationship.Disjoint, srid: 4326)
        };

        var result = SnowflakeFeatureQueryBuilder.BuildSelectQuery(mapping, query, _attributeColumns);

        Assert.Contains("ST_DISJOINT(\"SHAPE\"", result.Sql, StringComparison.Ordinal);
    }

    [Theory]
    // Esri esriSpatialRelWithin/Contains: filter geometry is within/contains the feature. The
    // filter geometry must be the FIRST operand (ST_WITHIN(filter, feature)); leading with the
    // feature column inverts the relationship and returns the wrong (empty) set (#2068).
    [InlineData(SpatialRelationship.Within, "ST_WITHIN")]
    [InlineData(SpatialRelationship.Contains, "ST_CONTAINS")]
    public void BuildSelectQuery_WithinContainsFilters_LeadWithFilterGeometry(SpatialRelationship relationship, string expectedFunction)
    {
        var mapping = BuildMapping();
        var query = new FeatureQuery
        {
            SpatialFilter = SpatialFilter.Create([0x01], relationship, srid: 4326)
        };

        var result = SnowflakeFeatureQueryBuilder.BuildSelectQuery(mapping, query, _attributeColumns);

        Assert.Contains(expectedFunction + "(TO_GEOGRAPHY(?, TRUE), \"SHAPE\")", result.Sql, StringComparison.Ordinal);
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
            () => SnowflakeFeatureQueryBuilder.BuildSelectQuery(mapping, query, _attributeColumns));
    }

    [Fact]
    public void BuildSelectQuery_CrossSridSpatialFilter_Throws()
    {
        var mapping = BuildMapping(srid: 4326);
        var query = new FeatureQuery
        {
            SpatialFilter = SpatialFilter.Create([0x01], SpatialRelationship.Intersects, srid: 3857)
        };

        var ex = Assert.Throws<NotSupportedException>(
            () => SnowflakeFeatureQueryBuilder.BuildSelectQuery(mapping, query, _attributeColumns));
        Assert.Contains("Cross-SRID", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildCountQuery_ProducesCountSelect()
    {
        var mapping = BuildMapping();

        var result = SnowflakeFeatureQueryBuilder.BuildCountQuery(mapping, new FeatureQuery());

        Assert.Contains("SELECT COUNT(*)", result.Sql, StringComparison.Ordinal);
        Assert.Contains("FROM \"ANALYTICS\".\"PUBLIC\".\"PARCELS\"", result.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildExtentQuery_UsesStBoundingOrdinateAggregates()
    {
        var mapping = BuildMapping();

        var result = SnowflakeFeatureQueryBuilder.BuildExtentQuery(mapping, query: null);

        Assert.Contains("MIN(ST_XMIN(\"SHAPE\")) AS \"min_x\"", result.Sql, StringComparison.Ordinal);
        Assert.Contains("MIN(ST_YMIN(\"SHAPE\")) AS \"min_y\"", result.Sql, StringComparison.Ordinal);
        Assert.Contains("MAX(ST_XMAX(\"SHAPE\")) AS \"max_x\"", result.Sql, StringComparison.Ordinal);
        Assert.Contains("MAX(ST_YMAX(\"SHAPE\")) AS \"max_y\"", result.Sql, StringComparison.Ordinal);
        Assert.Contains("\"SHAPE\" IS NOT NULL", result.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildObjectIdsQuery_ReturnsOnlyPrimaryKey()
    {
        var mapping = BuildMapping();
        var query = new FeatureQuery { Where = "NAME = 'Alpha'" };

        var result = SnowflakeFeatureQueryBuilder.BuildObjectIdsQuery(mapping, query);

        Assert.StartsWith("SELECT \"OBJECTID\" AS \"__objectid\"", result.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain("ST_ASWKB", result.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildSelectQuery_InvalidFieldName_RejectsInjection()
    {
        var mapping = BuildMapping();
        var query = new FeatureQuery { Where = "name; DROP TABLE foo = 'bar'" };

        Assert.Throws<ArgumentException>(
            () => SnowflakeFeatureQueryBuilder.BuildSelectQuery(mapping, query, _attributeColumns));
    }

    [Fact]
    public void FromStorage_UnsupportedGeometryType_Throws()
    {
        var providerOptions = new Dictionary<string, string>
        {
            [SnowflakeProviderOptions.GeometryType] = "wkt"
        };

        var storage = new LayerStorageMapping(
            TableName: "PARCELS",
            SchemaName: "PUBLIC",
            PrimaryKeyColumn: "OBJECTID",
            GeometryColumn: "SHAPE",
            StorageSrid: 4326,
            ProviderOptions: providerOptions);

        Assert.Throws<ArgumentException>(() => SnowflakeLayerMapping.FromStorage(TestLayerId, storage));
    }

    [Fact]
    public void FromStorage_DefaultsToGeography()
    {
        var storage = new LayerStorageMapping(
            TableName: "PARCELS",
            PrimaryKeyColumn: "OBJECTID",
            GeometryColumn: "SHAPE",
            StorageSrid: 4326);

        var mapping = SnowflakeLayerMapping.FromStorage(TestLayerId, storage);

        Assert.Equal(SnowflakeGeometryColumnType.Geography, mapping.GeometryColumnType);
        Assert.Equal("\"PARCELS\"", mapping.QuotedTableReference);
        Assert.Equal("TO_GEOGRAPHY", mapping.SpatialConstructor);
    }

    [Fact]
    public void FromStorage_RejectsInvalidIdentifier()
    {
        var storage = new LayerStorageMapping(
            TableName: "PARCELS; DROP TABLE x",
            PrimaryKeyColumn: "OBJECTID",
            GeometryColumn: "SHAPE",
            StorageSrid: 4326);

        Assert.Throws<ArgumentException>(() => SnowflakeLayerMapping.FromStorage(TestLayerId, storage));
    }

    [Fact]
    public void BuildSelectQuery_TemporalFilter_ThrowsNotSupported()
    {
        // Temporal analytics went GA (#2429); providers that cannot translate a temporal
        // predicate must fail loud rather than silently return rows outside the requested
        // window. The Snowflake provider has no temporal translation in this slice.
        var query = new FeatureQuery
        {
            TemporalFilter = new TemporalFilter
            {
                PropertyName = "OBSERVED_AT",
                PropertyType = TemporalPropertyType.DateTime
            }
        };

        var ex = Assert.Throws<NotSupportedException>(
            () => SnowflakeFeatureQueryBuilder.BuildSelectQuery(BuildMapping(), query, _attributeColumns));
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
                PropertyName = "OBSERVED_AT",
                PropertyType = TemporalPropertyType.DateTime
            }
        };
        var mapping = BuildMapping();

        Assert.Throws<NotSupportedException>(() => SnowflakeFeatureQueryBuilder.BuildCountQuery(mapping, query));
        Assert.Throws<NotSupportedException>(() => SnowflakeFeatureQueryBuilder.BuildObjectIdsQuery(mapping, query));
        Assert.Throws<NotSupportedException>(() => SnowflakeFeatureQueryBuilder.BuildExtentQuery(mapping, query));
        Assert.Throws<NotSupportedException>(() => SnowflakeFeatureQueryBuilder.BuildStatistics(mapping, query));
    }

    [Theory]
    [InlineData(StatisticType.Count, "COUNT")]
    [InlineData(StatisticType.Sum, "SUM")]
    [InlineData(StatisticType.Min, "MIN")]
    [InlineData(StatisticType.Max, "MAX")]
    [InlineData(StatisticType.Avg, "AVG")]
    [InlineData(StatisticType.Stddev, "STDDEV_SAMP")]
    [InlineData(StatisticType.Var, "VAR_SAMP")]
    public void BuildStatistics_EachStatisticType_QuotesFieldAndAlias(StatisticType statisticType, string sqlFunction)
    {
        var query = new FeatureQuery
        {
            OutStatistics = ImmutableArray.Create(new StatisticDefinition
            {
                StatisticType = statisticType,
                OnStatisticField = "AREA",
                OutStatisticFieldName = "AREA_STAT"
            })
        };

        var result = SnowflakeFeatureQueryBuilder.BuildStatistics(BuildMapping(), query);

        Assert.Contains($"{sqlFunction}(\"AREA\") AS \"AREA_STAT\"", result.Sql, StringComparison.Ordinal);
        Assert.Contains("FROM \"ANALYTICS\".\"PUBLIC\".\"PARCELS\"", result.Sql, StringComparison.Ordinal);
        Assert.Contains("WHERE 1=1", result.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain("GROUP BY", result.Sql, StringComparison.Ordinal);
        Assert.Empty(result.WhereParameters);
    }

    [Fact]
    public void BuildStatistics_WithGroupBy_ProjectsAndGroupsQuotedColumns()
    {
        var query = new FeatureQuery
        {
            OutStatistics = ImmutableArray.Create(new StatisticDefinition
            {
                StatisticType = StatisticType.Avg,
                OnStatisticField = "AREA",
                OutStatisticFieldName = "AREA_AVG"
            }),
            GroupByFields = ImmutableArray.Create("CATEGORY", "NAME")
        };

        var result = SnowflakeFeatureQueryBuilder.BuildStatistics(BuildMapping(), query);

        Assert.Contains("SELECT \"CATEGORY\", \"NAME\", AVG(\"AREA\") AS \"AREA_AVG\"", result.Sql, StringComparison.Ordinal);
        Assert.Contains("GROUP BY \"CATEGORY\", \"NAME\"", result.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildStatistics_WhereObjectIdsAndSpatialFilter_MatchCountEnvelope()
    {
        var geometry = new byte[] { 1, 2, 3, 4 };
        var query = new FeatureQuery
        {
            Where = "CATEGORY = 'commercial'",
            ObjectIds = ImmutableArray.Create(7L),
            SpatialFilter = SpatialFilter.Create(geometry, SpatialRelationship.Intersects, 4326),
            OutStatistics = ImmutableArray.Create(new StatisticDefinition
            {
                StatisticType = StatisticType.Count,
                OnStatisticField = "OBJECTID",
                OutStatisticFieldName = "N"
            })
        };
        var mapping = BuildMapping();

        var statistics = SnowflakeFeatureQueryBuilder.BuildStatistics(mapping, query);
        var count = SnowflakeFeatureQueryBuilder.BuildCountQuery(mapping, query);

        Assert.Equal(FilterTail(count.Sql), FilterTail(statistics.Sql));
        Assert.Equal(count.WhereParameters.ToArray(), statistics.WhereParameters.ToArray());
        Assert.Contains("COUNT(\"OBJECTID\") AS \"N\"", statistics.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildStatistics_UnsupportedStatisticType_ThrowsClearError()
    {
        var query = new FeatureQuery
        {
            OutStatistics = ImmutableArray.Create(new StatisticDefinition
            {
                StatisticType = (StatisticType)42,
                OnStatisticField = "AREA",
                OutStatisticFieldName = "AREA_STAT"
            })
        };

        var ex = Assert.Throws<NotSupportedException>(() => SnowflakeFeatureQueryBuilder.BuildStatistics(BuildMapping(), query));

        Assert.Contains("not supported", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Snowflake", ex.Message, StringComparison.Ordinal);
        Assert.Contains("42", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildStatistics_UnsafeFieldName_Throws()
    {
        var query = new FeatureQuery
        {
            OutStatistics = ImmutableArray.Create(new StatisticDefinition
            {
                StatisticType = StatisticType.Sum,
                OnStatisticField = "AREA;DROP",
                OutStatisticFieldName = "AREA_SUM"
            })
        };

        Assert.Throws<ArgumentException>(() => SnowflakeFeatureQueryBuilder.BuildStatistics(BuildMapping(), query));
    }

    [Fact]
    public void BuildStatistics_HavingOrderByLimitAndOffset_BoundTheAggregateResult()
    {
        var query = new FeatureQuery
        {
            OutStatistics = ImmutableArray.Create(new StatisticDefinition
            {
                StatisticType = StatisticType.Avg,
                OnStatisticField = "AREA",
                OutStatisticFieldName = "AREA_AVG"
            }),
            GroupByFields = ImmutableArray.Create("CATEGORY"),
            Having = ImmutableArray.Create(new HavingCondition
            {
                StatisticType = StatisticType.Count,
                OnStatisticField = "OBJECTID",
                Operator = HavingComparisonOperator.GreaterThanOrEqual,
                Value = 5
            }),
            OrderBy = ImmutableArray.Create(new OrderByClause("area_avg", ascending: false), new OrderByClause("CATEGORY")),
            Limit = 2,
            Offset = 1
        };

        var result = SnowflakeFeatureQueryBuilder.BuildStatistics(BuildMapping(), query);

        var havingAt = result.Sql.IndexOf("HAVING COUNT(\"OBJECTID\") >= ?", StringComparison.Ordinal);
        var orderAt = result.Sql.IndexOf("ORDER BY \"AREA_AVG\" DESC, \"CATEGORY\" ASC", StringComparison.Ordinal);
        var limitAt = result.Sql.IndexOf("LIMIT 2", StringComparison.Ordinal);
        var offsetAt = result.Sql.IndexOf("OFFSET 1", StringComparison.Ordinal);
        Assert.True(havingAt > 0 && orderAt > havingAt && limitAt > orderAt && offsetAt > limitAt, result.Sql);
        Assert.Equal(new object[] { 5d }, result.WhereParameters.ToArray());
        Assert.DoesNotContain("LIMIT ?", result.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildStatistics_OffsetWithoutOrderBy_OrdersByTheFirstAggregateColumn()
    {
        var query = new FeatureQuery
        {
            OutStatistics = ImmutableArray.Create(new StatisticDefinition
            {
                StatisticType = StatisticType.Count,
                OnStatisticField = "OBJECTID",
                OutStatisticFieldName = "N"
            }),
            GroupByFields = ImmutableArray.Create("CATEGORY"),
            Offset = 3
        };

        var result = SnowflakeFeatureQueryBuilder.BuildStatistics(BuildMapping(), query);

        Assert.Contains("ORDER BY \"CATEGORY\"", result.Sql, StringComparison.Ordinal);
        Assert.EndsWith("OFFSET 3", result.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain("LIMIT", result.Sql, StringComparison.Ordinal);
        Assert.Empty(result.WhereParameters);
    }

    [Fact]
    public void BuildStatistics_UnknownOrderByField_Throws()
    {
        var query = new FeatureQuery
        {
            OutStatistics = ImmutableArray.Create(new StatisticDefinition
            {
                StatisticType = StatisticType.Count,
                OnStatisticField = "OBJECTID",
                OutStatisticFieldName = "N"
            }),
            OrderBy = ImmutableArray.Create(new OrderByClause("AREA;DROP"))
        };

        var ex = Assert.Throws<ArgumentException>(() => SnowflakeFeatureQueryBuilder.BuildStatistics(BuildMapping(), query));
        Assert.Contains("order-by", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static string FilterTail(string sql)
    {
        const string marker = "WHERE 1=1";
        var index = sql.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(index >= 0, sql);
        return sql[index..];
    }
}
