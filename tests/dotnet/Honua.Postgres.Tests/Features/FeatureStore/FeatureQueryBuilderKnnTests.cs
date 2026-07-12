// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Domain;
using Honua.Postgres.Features.FeatureStore.Services;
using Microsoft.Extensions.ObjectPool;
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

        var result = queryBuilder.BuildSelectQuery(layerId: 1, query);

        result.Sql.Should().Contain("ORDER BY ST_Distance(");
        result.Sql.Should().Contain("::geography");
        result.Sql.Should().Contain("ST_Transform(");
        result.Sql.Should().NotContain("<->");
    }

    [Fact]
    public void BuildSelectQuery_WithUnlistedGeographic4xxxKnn_UsesGeodesicOrdering()
    {
        // #2740/#2731: EPSG:4674 (SIRGAS 2000) is a geographic degree CRS but is not on the curated
        // allowlist. Planar degree KNN (the <-> operator) would produce meaningless distances, so
        // the 4000-4999 safety net must route it through the same geodesic path as 4326 (#2732).
        var poolProvider = new DefaultObjectPoolProvider();
        var stringBuilderPool = poolProvider.Create(new FeatureStoreStringBuilderPooledObjectPolicy());
        var geometryProcessor = new GeometryProcessor();
        var queryBuilder = new FeatureQueryBuilder(stringBuilderPool, geometryProcessor);

        var query = new FeatureQuery
        {
            SpatialReferenceSrid = 4674,
            SpatialFilter = SpatialFilter.CreateKnnFilter(new byte[] { 1, 2, 3 }, count: 5, srid: 4674)
        };

        var result = queryBuilder.BuildSelectQuery(layerId: 1, query);

        result.Sql.Should().Contain("ORDER BY ST_Distance(");
        result.Sql.Should().Contain("::geography");
        result.Sql.Should().Contain("ST_Transform(");
        result.Sql.Should().NotContain("<->");
    }
}
