// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Databricks.Features.FeatureStore.Services;
using Honua.Databricks.Features.Infrastructure;

namespace Honua.Databricks.Tests;

public class DatabricksFeatureQueryBuilderTests
{
    private static readonly DatabricksFeatureQueryBuilder Builder = new();

    private static DatabricksLayerMapping Mapping(params string[] attributes) => new()
    {
        LayerId = 1,
        Table = "parcels",
        Catalog = "main",
        Schema = "gis",
        GeometryColumn = "geom",
        PrimaryKeyColumn = "id",
        Srid = 4326,
        GeometryType = GeometryType.Polygon,
        AttributeColumns = attributes.Length == 0 ? ["name", "owner"] : attributes,
    };

    [Fact]
    public void BuildSelect_ProjectsIdGeometryHexAndAttributes_WithQualifiedTable()
    {
        var statement = Builder.BuildSelect(Mapping(), new FeatureQuery());

        Assert.Contains("`id` AS __honua_id", statement.Sql, StringComparison.Ordinal);
        Assert.Contains("hex(st_asbinary(`geom`)) AS __honua_geom_hex", statement.Sql, StringComparison.Ordinal);
        Assert.Contains("`name`", statement.Sql, StringComparison.Ordinal);
        Assert.Contains("`owner`", statement.Sql, StringComparison.Ordinal);
        Assert.Contains("FROM `main`.`gis`.`parcels`", statement.Sql, StringComparison.Ordinal);
        Assert.Empty(statement.Parameters);
    }

    [Fact]
    public void BuildSelect_TranslatesWhereClause_ToParameterizedSparkSql()
    {
        var statement = Builder.BuildSelect(Mapping(), new FeatureQuery { Where = "owner = 'acme'" });

        // The literal is parameterized (no inline 'acme'); the column is backtick-quoted.
        Assert.Contains("WHERE (`owner` = :f0)", statement.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain("'acme'", statement.Sql, StringComparison.Ordinal);
        Assert.Single(statement.Parameters);
        Assert.Equal("f0", statement.Parameters[0].Name);
        Assert.Equal("acme", statement.Parameters[0].Value);
    }

    [Fact]
    public void BuildSelect_TranslatesNumericComparison_WithTypedParameter()
    {
        var statement = Builder.BuildSelect(Mapping("name", "owner", "pop"), new FeatureQuery { Where = "pop >= 1000" });

        Assert.Contains("`pop` >= :f0", statement.Sql, StringComparison.Ordinal);
        Assert.Equal("1000", statement.Parameters[0].Value);
        Assert.Equal("BIGINT", statement.Parameters[0].Type);
    }

    [Fact]
    public void BuildSelect_TranslatesInList_Parameterized()
    {
        var statement = Builder.BuildSelect(Mapping("name", "owner", "code"), new FeatureQuery { Where = "code IN (1, 2, 3)" });

        Assert.Contains("`code` IN (:f0, :f1, :f2)", statement.Sql, StringComparison.Ordinal);
        Assert.Equal(3, statement.Parameters.Count);
    }

    [Fact]
    public void BuildSelect_TranslatesIsNull()
    {
        var statement = Builder.BuildSelect(Mapping(), new FeatureQuery { Where = "owner IS NULL" });

        Assert.Contains("`owner` IS NULL", statement.Sql, StringComparison.Ordinal);
        Assert.Empty(statement.Parameters);
    }

    [Fact]
    public void BuildSelect_WhereWithUnknownField_Throws()
    {
        // Unknown fields are a client validation error (ArgumentException -> HTTP 400),
        // distinct from capability rejections (NotSupportedException).
        Assert.Throws<ArgumentException>(
            () => Builder.BuildSelect(Mapping(), new FeatureQuery { Where = "not_a_column = 1" }));
    }

    [Fact]
    public void BuildSelect_WhereParametersAndObjectIds_DoNotCollide()
    {
        var query = new FeatureQuery { Where = "owner = 'acme'", ObjectIds = [7L] };

        var statement = Builder.BuildSelect(Mapping(), query);

        Assert.Contains(":f0", statement.Sql, StringComparison.Ordinal);
        Assert.Contains(":oid0", statement.Sql, StringComparison.Ordinal);
        Assert.Equal(2, statement.Parameters.Count);
    }

    [Fact]
    public void BuildSelect_WithObjectIds_BindsNamedParameters()
    {
        var statement = Builder.BuildSelect(Mapping(), new FeatureQuery { ObjectIds = [10L, 20L] });

        Assert.Contains("`id` IN (:oid0, :oid1)", statement.Sql, StringComparison.Ordinal);
        Assert.Equal(2, statement.Parameters.Count);
        Assert.Equal("oid0", statement.Parameters[0].Name);
        Assert.Equal("10", statement.Parameters[0].Value);
        Assert.Equal("BIGINT", statement.Parameters[0].Type);
        Assert.Equal("20", statement.Parameters[1].Value);
    }

    [Fact]
    public void BuildSelect_AppliesLimitOffsetAndOrderBy()
    {
        var query = new FeatureQuery
        {
            Limit = 50,
            Offset = 100,
            OrderBy = [OrderByClause.Asc("name"), OrderByClause.Desc("owner")],
        };

        var statement = Builder.BuildSelect(Mapping(), query);

        Assert.Contains("ORDER BY `name` ASC, `owner` DESC", statement.Sql, StringComparison.Ordinal);
        Assert.Contains("LIMIT 50", statement.Sql, StringComparison.Ordinal);
        Assert.Contains("OFFSET 100", statement.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildSelect_WithOutFields_ProjectsOnlyRequestedConfiguredColumns()
    {
        var query = new FeatureQuery { OutFields = ["name"] };

        var statement = Builder.BuildSelect(Mapping(), query);

        Assert.Contains("`name`", statement.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain(", `owner`", statement.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildSelect_ExcludeAttributes_ProjectsNoAttributeColumns()
    {
        var statement = Builder.BuildSelect(Mapping(), new FeatureQuery { ExcludeAttributes = true });

        Assert.DoesNotContain("`name`", statement.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain("`owner`", statement.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildCount_EmitsCountStarWithFilter()
    {
        var statement = Builder.BuildCount(Mapping("name", "owner", "x"), new FeatureQuery { Where = "x > 1" });

        Assert.StartsWith("SELECT COUNT(*) AS __honua_count FROM `main`.`gis`.`parcels`", statement.Sql, StringComparison.Ordinal);
        Assert.Contains("WHERE (`x` > :f0)", statement.Sql, StringComparison.Ordinal);
        Assert.Single(statement.Parameters);
    }

    [Fact]
    public void BuildExtent_EmitsStMinMaxAggregates()
    {
        var statement = Builder.BuildExtent(Mapping(), null);

        Assert.Contains("MIN(st_xmin(`geom`))", statement.Sql, StringComparison.Ordinal);
        Assert.Contains("MAX(st_ymax(`geom`))", statement.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildObjectIds_SelectsPrimaryKeyOnly()
    {
        var statement = Builder.BuildObjectIds(Mapping(), new FeatureQuery());

        Assert.StartsWith("SELECT `id` AS __honua_id FROM", statement.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain("st_asbinary", statement.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildSelect_EnvelopeSpatialFilter_EmitsStIntersects()
    {
        var spatial = SpatialFilter.Create(
            geometry: [1, 2, 3, 4],
            spatialRelationship: SpatialRelationship.Intersects,
            srid: 4326,
            isSimpleEnvelope: true,
            allowEnvelopeOnly: true,
            envelopeMinX: -10,
            envelopeMinY: -5,
            envelopeMaxX: 10,
            envelopeMaxY: 5);

        var statement = Builder.BuildSelect(Mapping(), new FeatureQuery { SpatialFilter = spatial });

        Assert.Contains("st_intersects(`geom`, st_geomfromtext('POLYGON((", statement.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildSelect_NonEnvelopeSpatialFilter_Throws()
    {
        var spatial = SpatialFilter.Create(
            geometry: [1, 2, 3, 4],
            spatialRelationship: SpatialRelationship.Intersects,
            srid: 4326,
            isSimpleEnvelope: false);

        Assert.Throws<NotSupportedException>(() => Builder.BuildSelect(Mapping(), new FeatureQuery { SpatialFilter = spatial }));
    }

    [Fact]
    public void BuildSelect_TemporalFilter_Throws()
    {
        var query = new FeatureQuery
        {
            TemporalFilter = new TemporalFilter { PropertyName = "ts", PropertyType = TemporalPropertyType.DateTime },
        };

        Assert.Throws<NotSupportedException>(() => Builder.BuildSelect(Mapping(), query));
    }

    [Fact]
    public void BuildStatistics_EmitsAggregateColumns()
    {
        var query = new FeatureQuery
        {
            OutStatistics =
            [
                new StatisticDefinition { StatisticType = StatisticType.Count, OnStatisticField = "owner", OutStatisticFieldName = "owner_count" },
                new StatisticDefinition { StatisticType = StatisticType.Sum, OnStatisticField = "pop", OutStatisticFieldName = "pop_sum" },
            ],
        };

        var statement = Builder.BuildStatistics(Mapping("name", "owner", "pop"), query);

        Assert.Contains("COUNT(`owner`) AS `owner_count`", statement.Sql, StringComparison.Ordinal);
        Assert.Contains("SUM(`pop`) AS `pop_sum`", statement.Sql, StringComparison.Ordinal);
        Assert.Contains("FROM `main`.`gis`.`parcels`", statement.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain("GROUP BY", statement.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildStatistics_WithGroupBy_EmitsGroupByAndProjectsGroupColumns()
    {
        var query = new FeatureQuery
        {
            OutStatistics =
            [
                new StatisticDefinition { StatisticType = StatisticType.Avg, OnStatisticField = "pop", OutStatisticFieldName = "pop_avg" },
            ],
            GroupByFields = ["owner"],
        };

        var statement = Builder.BuildStatistics(Mapping("name", "owner", "pop"), query);

        Assert.Contains("SELECT `owner`, AVG(`pop`) AS `pop_avg`", statement.Sql, StringComparison.Ordinal);
        Assert.Contains("GROUP BY `owner`", statement.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildStatistics_StddevAndVar_UseSparkSampleFunctions()
    {
        var query = new FeatureQuery
        {
            OutStatistics =
            [
                new StatisticDefinition { StatisticType = StatisticType.Stddev, OnStatisticField = "pop", OutStatisticFieldName = "pop_sd" },
                new StatisticDefinition { StatisticType = StatisticType.Var, OnStatisticField = "pop", OutStatisticFieldName = "pop_var" },
            ],
        };

        var statement = Builder.BuildStatistics(Mapping("name", "owner", "pop"), query);

        Assert.Contains("STDDEV_SAMP(`pop`) AS `pop_sd`", statement.Sql, StringComparison.Ordinal);
        Assert.Contains("VAR_SAMP(`pop`) AS `pop_var`", statement.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildStatistics_FiltersByWhereClause()
    {
        var query = new FeatureQuery
        {
            Where = "owner = 'acme'",
            OutStatistics =
            [
                new StatisticDefinition { StatisticType = StatisticType.Count, OnStatisticField = "owner", OutStatisticFieldName = "n" },
            ],
        };

        var statement = Builder.BuildStatistics(Mapping(), query);

        Assert.Contains("WHERE (`owner` = :f0)", statement.Sql, StringComparison.Ordinal);
        Assert.Equal("acme", statement.Parameters[0].Value);
    }

    [Fact]
    public void BuildStatistics_NoStatistics_ReturnsEmptyResultQuery()
    {
        var statement = Builder.BuildStatistics(Mapping(), new FeatureQuery());

        Assert.Equal("SELECT 1 WHERE FALSE", statement.Sql);
        Assert.Empty(statement.Parameters);
    }
}
