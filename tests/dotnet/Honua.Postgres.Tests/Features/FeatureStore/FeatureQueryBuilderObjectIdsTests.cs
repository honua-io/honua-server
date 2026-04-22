// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Domain;
using Honua.Postgres.Features.FeatureStore.Services;
using Microsoft.Extensions.ObjectPool;
using FeatureStoreStringBuilderPooledObjectPolicy = Honua.Postgres.Features.FeatureStore.Services.StringBuilderPooledObjectPolicy;

namespace Honua.Postgres.Tests.Features.FeatureStore;

public sealed class FeatureQueryBuilderObjectIdsTests
{
    [Fact]
    public void BuildObjectIdsQuery_SelectsObjectIdOnly()
    {
        var poolProvider = new DefaultObjectPoolProvider();
        var stringBuilderPool = poolProvider.Create(new FeatureStoreStringBuilderPooledObjectPolicy());
        var geometryProcessor = new GeometryProcessor();
        var queryBuilder = new FeatureQueryBuilder(stringBuilderPool, geometryProcessor);

        var result = queryBuilder.BuildObjectIdsQuery(layerId: 1, query: new FeatureQuery());

        result.Sql.Should().Contain("SELECT objectid FROM");
        result.Sql.Should().NotContain("attributes");
        result.Sql.Should().NotContain("COUNT(*) OVER()");
    }

    [Fact]
    public void BuildObjectIdsQuery_WithKnnFilter_AppliesNearestNeighborOrdering()
    {
        var poolProvider = new DefaultObjectPoolProvider();
        var stringBuilderPool = poolProvider.Create(new FeatureStoreStringBuilderPooledObjectPolicy());
        var geometryProcessor = new GeometryProcessor();
        var queryBuilder = new FeatureQueryBuilder(stringBuilderPool, geometryProcessor);

        var query = new FeatureQuery
        {
            SpatialReferenceSrid = 4326,
            SpatialFilter = SpatialFilter.CreateKnnFilter(new byte[] { 1, 2, 3 }, count: 5, srid: 4326)
        };

        var result = queryBuilder.BuildObjectIdsQuery(layerId: 1, query);

        result.Sql.Should().Contain("ORDER BY");
    }
}

