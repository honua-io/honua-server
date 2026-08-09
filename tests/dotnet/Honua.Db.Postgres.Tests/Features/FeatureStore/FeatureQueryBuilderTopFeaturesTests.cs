// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Queries.Filters;
using Honua.Postgres.Features.FeatureStore.Services;
using Microsoft.Extensions.ObjectPool;
using FeatureStoreStringBuilderPooledObjectPolicy = Honua.Postgres.Features.FeatureStore.Services.StringBuilderPooledObjectPolicy;

namespace Honua.Postgres.Tests.Features.FeatureStore;

public sealed class FeatureQueryBuilderTopFeaturesTests
{
    [Fact]
    public void BuildTopFeaturesQuery_ParameterizesTopCount()
    {
        var queryBuilder = CreateQueryBuilder();
        var query = new FeatureQuery
        {
            TopFilter = CreateTopFilter(topCount: 3)
        };

        var result = queryBuilder.BuildTopFeaturesQuery(layerId: 1, query);

        result.Sql.Should().Contain(") sub WHERE sub.rn <= $2");
        result.Sql.Should().NotContain(") sub WHERE sub.rn <= 3");
        result.WhereParameters.Should().ContainSingle().Which.Should().Be(3);
    }

    [Fact]
    public void BuildTopFeaturesQuery_AppendsTopCountAfterExistingWhereParameters()
    {
        var queryBuilder = CreateQueryBuilder();
        var query = new FeatureQuery
        {
            SqlFilter = new SqlFragment("attributes->>'category' = @p0", new object?[] { "alpha" }),
            TopFilter = CreateTopFilter(topCount: 5)
        };

        var result = queryBuilder.BuildTopFeaturesQuery(layerId: 1, query);

        result.Sql.Should().Contain("AND (attributes->>'category' = $2)");
        result.Sql.Should().Contain(") sub WHERE sub.rn <= $3");
        result.WhereParameters.Should().ContainInOrder("alpha", 5);
    }

    [Fact]
    public void BuildTopFeaturesQuery_WithLimitAndOffset_AppendsTrailingPagingPlaceholders()
    {
        // resultRecordCount/resultOffset must page the top-feature result; the LIMIT/OFFSET
        // placeholders are bound after the where/topCount parameters, so they must be the
        // trailing placeholders in numeric order (#1906).
        var queryBuilder = CreateQueryBuilder();
        var query = new FeatureQuery
        {
            TopFilter = CreateTopFilter(topCount: 5),
            Limit = 1,
            Offset = 2
        };

        var result = queryBuilder.BuildTopFeaturesQuery(layerId: 1, query);

        // $1 = layerId, $2 = topCount, $3 = LIMIT, $4 = OFFSET.
        result.Sql.Should().Contain(") sub WHERE sub.rn <= $2");
        result.Sql.Should().Contain("LIMIT $3");
        result.Sql.Should().Contain("OFFSET $4");
    }

    [Fact]
    public void BuildTopFeaturesQuery_WithoutPaging_OmitsLimitAndOffset()
    {
        var queryBuilder = CreateQueryBuilder();
        var query = new FeatureQuery
        {
            TopFilter = CreateTopFilter(topCount: 3)
        };

        var result = queryBuilder.BuildTopFeaturesQuery(layerId: 1, query);

        result.Sql.Should().NotContain("LIMIT");
        result.Sql.Should().NotContain("OFFSET");
    }

    [Fact]
    public void BuildTopFeaturesQuery_WithOutputSrid_TransformsGeometry()
    {
        // outSR (OutputSrid) must reproject output geometry via ST_Transform, mirroring
        // the normal query path (#1906).
        var queryBuilder = CreateQueryBuilder();
        var query = new FeatureQuery
        {
            TopFilter = CreateTopFilter(topCount: 3),
            SpatialReferenceSrid = 4326,
            OutputSrid = 3857
        };

        var result = queryBuilder.BuildTopFeaturesQuery(layerId: 1, query);

        result.Sql.Should().Contain("ST_Transform");
        result.Sql.Should().Contain("3857");
    }

    private static TopFilter CreateTopFilter(int topCount)
        => new()
        {
            GroupByFields = ImmutableArray.Create("category"),
            TopCount = topCount,
            OrderByFields = ImmutableArray.Create(OrderByClause.Desc("score"))
        };

    private static FeatureQueryBuilder CreateQueryBuilder()
    {
        var poolProvider = new DefaultObjectPoolProvider();
        var stringBuilderPool = poolProvider.Create(new FeatureStoreStringBuilderPooledObjectPolicy());
        var geometryProcessor = new GeometryProcessor();
        return new FeatureQueryBuilder(stringBuilderPool, geometryProcessor);
    }
}
