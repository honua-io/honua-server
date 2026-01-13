// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Domain;
using Honua.Postgres.Features.FeatureStore.Services;
using Microsoft.Extensions.ObjectPool;
using CoreGeometryStorageType = Honua.Core.Features.FeatureStore.Abstractions.GeometryStorageType;
using FeatureStoreStringBuilderPooledObjectPolicy = Honua.Postgres.Features.FeatureStore.Services.StringBuilderPooledObjectPolicy;

namespace Honua.Postgres.Tests.Features.FeatureStore;

public sealed class FeatureQueryBuilderKnnTests
{
    [Fact]
    public void BuildSelectQuery_WithGeographicNonWgs84Knn_UsesGeodesicOrdering()
    {
        var poolProvider = new DefaultObjectPoolProvider();
        var stringBuilderPool = poolProvider.Create(new FeatureStoreStringBuilderPooledObjectPolicy());
        var geometryProcessor = new GeometryProcessor();
        var queryBuilder = new FeatureQueryBuilder(stringBuilderPool, geometryProcessor);

        var query = new FeatureQuery
        {
            SpatialReferenceSrid = 4269,
            SpatialFilter = SpatialFilter.CreateKnnFilter(new byte[] { 1, 2, 3 }, count: 5, srid: 4269)
        };

        var result = queryBuilder.BuildSelectQuery(layerId: 1, query, CoreGeometryStorageType.Geometry);

        result.Sql.Should().Contain("ORDER BY ST_Distance(");
        result.Sql.Should().Contain("::geography");
        result.Sql.Should().Contain("ST_Transform(");
        result.Sql.Should().NotContain("<->");
    }
}
