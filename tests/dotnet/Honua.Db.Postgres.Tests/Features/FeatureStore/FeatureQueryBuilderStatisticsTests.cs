// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Db.Postgres.Features.FeatureStore.Services;
using Microsoft.Extensions.ObjectPool;
using FeatureStoreStringBuilderPooledObjectPolicy = Honua.Db.Postgres.Features.FeatureStore.Services.StringBuilderPooledObjectPolicy;

namespace Honua.Db.Postgres.Tests.Features.FeatureStore;

public sealed class FeatureQueryBuilderStatisticsTests
{
    [Fact]
    public void BuildStatisticsQuery_OnlyCastsNumericAggregatesToNumeric()
    {
        var queryBuilder = CreateQueryBuilder();
        var query = new FeatureQuery
        {
            OutStatistics = ImmutableArray.Create(
                new StatisticDefinition
                {
                    StatisticType = StatisticType.Min,
                    OnStatisticField = "name",
                    OutStatisticFieldName = "min_name"
                },
                new StatisticDefinition
                {
                    StatisticType = StatisticType.Max,
                    OnStatisticField = "created_at",
                    OutStatisticFieldName = "max_created_at"
                },
                new StatisticDefinition
                {
                    StatisticType = StatisticType.Sum,
                    OnStatisticField = "count",
                    OutStatisticFieldName = "sum_count"
                },
                new StatisticDefinition
                {
                    StatisticType = StatisticType.Avg,
                    OnStatisticField = "ratio",
                    OutStatisticFieldName = "avg_ratio"
                })
        };

        var result = queryBuilder.BuildStatisticsQuery(layerId: 1, query);

        result.Sql.Should().Contain("MIN(attributes->>'name') AS \"min_name\"");
        result.Sql.Should().Contain("MAX(attributes->>'created_at') AS \"max_created_at\"");
        result.Sql.Should().Contain("SUM((attributes->>'count')::numeric) AS \"sum_count\"");
        result.Sql.Should().Contain("AVG((attributes->>'ratio')::numeric) AS \"avg_ratio\"");
        result.Sql.Should().NotContain("MIN((attributes->>'name')::numeric)");
        result.Sql.Should().NotContain("MAX((attributes->>'created_at')::numeric)");
    }

    [Fact]
    public void BuildStatisticsQuery_WithNumericFieldTypeHint_CastsMinAndMaxToNumeric()
    {
        var queryBuilder = CreateQueryBuilder();
        var query = new FeatureQuery
        {
            OutStatistics = ImmutableArray.Create(
                new StatisticDefinition
                {
                    StatisticType = StatisticType.Min,
                    OnStatisticField = "bucket",
                    OutStatisticFieldName = "min_bucket",
                    FieldType = MetadataV2FieldType.Integer
                },
                new StatisticDefinition
                {
                    StatisticType = StatisticType.Max,
                    OnStatisticField = "bucket",
                    OutStatisticFieldName = "max_bucket",
                    FieldType = MetadataV2FieldType.Integer
                })
        };

        var result = queryBuilder.BuildStatisticsQuery(layerId: 1, query);

        result.Sql.Should().Contain("MIN((attributes->>'bucket')::numeric) AS \"min_bucket\"");
        result.Sql.Should().Contain("MAX((attributes->>'bucket')::numeric) AS \"max_bucket\"");
    }

    // #3372: an aggregate result set's columns are the statistic aliases and the group-by
    // fields, so ORDER BY must be able to name them. The emitted SQL is rebuilt from the
    // matched declaration (the aggregate expression / the group-by field expression), never
    // from the order-by clause's own text.
    [Fact]
    public void BuildStatisticsQuery_WithOrderByStatisticAlias_OrdersByTheAggregateExpression()
    {
        var queryBuilder = CreateQueryBuilder();
        var query = new FeatureQuery
        {
            OutStatistics = ImmutableArray.Create(
                new StatisticDefinition
                {
                    StatisticType = StatisticType.Count,
                    OnStatisticField = "objectid",
                    OutStatisticFieldName = "feature_count"
                }),
            GroupByFields = ImmutableArray.Create("category"),
            OrderBy = ImmutableArray.Create(new OrderByClause("feature_count", ascending: false))
        };

        var result = queryBuilder.BuildStatisticsQuery(layerId: 1, query);

        result.Sql.Should().Contain("ORDER BY COUNT(objectid) DESC");
    }

    [Fact]
    public void BuildStatisticsQuery_WithOrderByGroupByField_OrdersByTheGroupedExpression()
    {
        var queryBuilder = CreateQueryBuilder();
        var query = new FeatureQuery
        {
            OutStatistics = ImmutableArray.Create(
                new StatisticDefinition
                {
                    StatisticType = StatisticType.Count,
                    OnStatisticField = "objectid",
                    OutStatisticFieldName = "feature_count"
                }),
            GroupByFields = ImmutableArray.Create("category"),
            OrderBy = ImmutableArray.Create(new OrderByClause("category"))
        };

        var result = queryBuilder.BuildStatisticsQuery(layerId: 1, query);

        result.Sql.Should().Contain("ORDER BY attributes->>'category' ASC");
    }

    [Fact]
    public void BuildStatisticsQuery_WithMultipleOrderByTerms_EmitsEachInOrder()
    {
        var queryBuilder = CreateQueryBuilder();
        var query = new FeatureQuery
        {
            OutStatistics = ImmutableArray.Create(
                new StatisticDefinition
                {
                    StatisticType = StatisticType.Count,
                    OnStatisticField = "objectid",
                    OutStatisticFieldName = "feature_count"
                }),
            GroupByFields = ImmutableArray.Create("category"),
            OrderBy = ImmutableArray.Create(
                new OrderByClause("feature_count", ascending: false),
                new OrderByClause("category"))
        };

        var result = queryBuilder.BuildStatisticsQuery(layerId: 1, query);

        result.Sql.Should().Contain(
            "ORDER BY COUNT(objectid) DESC, attributes->>'category' ASC");
    }

    [Fact]
    public void BuildStatisticsQuery_WithoutOrderBy_EmitsNoOrderByClause()
    {
        var queryBuilder = CreateQueryBuilder();
        var query = new FeatureQuery
        {
            OutStatistics = ImmutableArray.Create(
                new StatisticDefinition
                {
                    StatisticType = StatisticType.Count,
                    OnStatisticField = "objectid",
                    OutStatisticFieldName = "feature_count"
                }),
            GroupByFields = ImmutableArray.Create("category")
        };

        var result = queryBuilder.BuildStatisticsQuery(layerId: 1, query);

        result.Sql.Should().NotContain("ORDER BY");
    }

    // The widened order-by surface stays bounded at the SQL boundary too: an order-by
    // field that matches no declared alias or group-by field is refused rather than
    // interpolated into the statement.
    [Fact]
    public void BuildStatisticsQuery_WithOrderByUndeclaredField_Throws()
    {
        var queryBuilder = CreateQueryBuilder();
        var query = new FeatureQuery
        {
            OutStatistics = ImmutableArray.Create(
                new StatisticDefinition
                {
                    StatisticType = StatisticType.Count,
                    OnStatisticField = "objectid",
                    OutStatisticFieldName = "feature_count"
                }),
            GroupByFields = ImmutableArray.Create("category"),
            OrderBy = ImmutableArray.Create(new OrderByClause("name"))
        };

        var act = () => queryBuilder.BuildStatisticsQuery(layerId: 1, query);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void BuildStatisticsQuery_WithOrderByInjectionAttempt_Throws()
    {
        var queryBuilder = CreateQueryBuilder();
        var query = new FeatureQuery
        {
            OutStatistics = ImmutableArray.Create(
                new StatisticDefinition
                {
                    StatisticType = StatisticType.Count,
                    OnStatisticField = "objectid",
                    OutStatisticFieldName = "feature_count"
                }),
            GroupByFields = ImmutableArray.Create("category"),
            OrderBy = ImmutableArray.Create(new OrderByClause("feature_count; DROP TABLE features--"))
        };

        var act = () => queryBuilder.BuildStatisticsQuery(layerId: 1, query);

        act.Should().Throw<ArgumentException>();
    }

    private static FeatureQueryBuilder CreateQueryBuilder()
    {
        var poolProvider = new DefaultObjectPoolProvider();
        var stringBuilderPool = poolProvider.Create(new FeatureStoreStringBuilderPooledObjectPolicy());
        var geometryProcessor = new GeometryProcessor();
        return new FeatureQueryBuilder(stringBuilderPool, geometryProcessor);
    }
}
