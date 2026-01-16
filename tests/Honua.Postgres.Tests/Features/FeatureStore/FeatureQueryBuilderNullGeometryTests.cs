// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Domain;
using Honua.Postgres.Features.FeatureStore.Services;
using Microsoft.Extensions.ObjectPool;
using FeatureStoreStringBuilderPooledObjectPolicy = Honua.Postgres.Features.FeatureStore.Services.StringBuilderPooledObjectPolicy;

namespace Honua.Postgres.Tests.Features.FeatureStore;

public sealed class FeatureQueryBuilderNullGeometryTests
{
    [Fact]
    public void BuildSelectQuery_WithSpatialFilterAndIncludeNullGeometry_AddsNullGeometryClause()
    {
        var poolProvider = new DefaultObjectPoolProvider();
        var stringBuilderPool = poolProvider.Create(new FeatureStoreStringBuilderPooledObjectPolicy());
        var geometryProcessor = new GeometryProcessor();
        var queryBuilder = new FeatureQueryBuilder(stringBuilderPool, geometryProcessor);

        var query = new FeatureQuery
        {
            SpatialReferenceSrid = 4326,
            SpatialFilter = SpatialFilter.Create(new byte[] { 1, 2, 3 }, SpatialRelationship.Intersects, 4326),
            IncludeNullGeometry = true
        };

        var result = queryBuilder.BuildSelectQuery(layerId: 1, query);

        result.Sql.Should().Contain("OR geometry IS NULL");
    }
}
