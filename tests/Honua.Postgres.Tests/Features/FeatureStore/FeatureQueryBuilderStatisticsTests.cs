// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Postgres.Features.FeatureStore.Services;
using Microsoft.Extensions.ObjectPool;
using FeatureStoreStringBuilderPooledObjectPolicy = Honua.Postgres.Features.FeatureStore.Services.StringBuilderPooledObjectPolicy;

namespace Honua.Postgres.Tests.Features.FeatureStore;

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

    private static FeatureQueryBuilder CreateQueryBuilder()
    {
        var poolProvider = new DefaultObjectPoolProvider();
        var stringBuilderPool = poolProvider.Create(new FeatureStoreStringBuilderPooledObjectPolicy());
        var geometryProcessor = new GeometryProcessor();
        return new FeatureQueryBuilder(stringBuilderPool, geometryProcessor);
    }
}
