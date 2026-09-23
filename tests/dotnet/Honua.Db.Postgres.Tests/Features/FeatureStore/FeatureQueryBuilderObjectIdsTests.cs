// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Queries.Filters;
using Honua.Db.Postgres.Features.FeatureStore.Services;
using Microsoft.Extensions.ObjectPool;
using FeatureStoreStringBuilderPooledObjectPolicy = Honua.Db.Postgres.Features.FeatureStore.Services.StringBuilderPooledObjectPolicy;

namespace Honua.Db.Postgres.Tests.Features.FeatureStore;

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
    public void BuildPreChangeObjectIdsQuery_FiltersRecordedImagesWithTheSameWhereAndEnforcedFilter()
    {
        var poolProvider = new DefaultObjectPoolProvider();
        var stringBuilderPool = poolProvider.Create(new FeatureStoreStringBuilderPooledObjectPolicy());
        var queryBuilder = new FeatureQueryBuilder(stringBuilderPool, new GeometryProcessor());
        var query = new FeatureQuery
        {
            SqlFilter = new SqlFragment("attributes->>'category' = @p0", ["alpha"]),
            EnforcedSqlFilter = new SqlFragment("attributes->>'owner' = @p0", ["west"])
        };

        var result = queryBuilder.BuildPreChangeObjectIdsQuery(layerId: 1, query, changeIds: [11L, 12L]);

        // The images replace the base table behind the usual column shape; the change ids bind first.
        result.Sql.Should().StartWith("SELECT objectid FROM (SELECT c.objectid, c.layer_id, c.pre_geometry AS geometry, c.pre_attributes AS attributes, c.pre_created_at AS created_at, c.pre_updated_at AS updated_at FROM honua.feature_changes c WHERE c.change_id = ANY($2) AND c.pre_attributes IS NOT NULL) AS features WHERE layer_id = $1");
        result.Sql.Should().NotContain("FROM features WHERE").And.NotContain("public.features");
        result.WhereParameters[0].Should().BeEquivalentTo(new[] { 11L, 12L });
        result.WhereParameters.Skip(1).Should().Equal("west", "alpha");
    }

    [Fact]
    public void BuildPreChangeObjectIdsQuery_BranchVersion_IsRejected()
    {
        var poolProvider = new DefaultObjectPoolProvider();
        var stringBuilderPool = poolProvider.Create(new FeatureStoreStringBuilderPooledObjectPolicy());
        var queryBuilder = new FeatureQueryBuilder(stringBuilderPool, new GeometryProcessor());
        var version = new GdbVersion
        {
            VersionId = Guid.NewGuid(),
            VersionName = "sde.QA",
            Owner = "sde",
            Access = VersionAccess.Public,
            State = VersionState.Active,
            CommonAncestorGeneration = 5,
            BranchGeneration = 9,
            CreatedAt = DateTimeOffset.UtcNow,
            ModifiedAt = DateTimeOffset.UtcNow,
        };
        var query = new FeatureQuery { VersionContext = VersionContext.ForVersion(version) };

        var act = () => queryBuilder.BuildPreChangeObjectIdsQuery(layerId: 1, query, changeIds: [11L]);

        act.Should().Throw<NotSupportedException>();
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
