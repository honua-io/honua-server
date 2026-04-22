// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Postgres.Features.FeatureStore.Services;
using Microsoft.Extensions.ObjectPool;
using FeatureStoreStringBuilderPooledObjectPolicy = Honua.Postgres.Features.FeatureStore.Services.StringBuilderPooledObjectPolicy;

namespace Honua.Postgres.Tests.Features.FeatureStore;

public sealed class FeatureQueryBuilderSelectProjectionTests
{
    [Fact]
    public void BuildSelectQuery_WithOutFields_ProjectsRequestedAttributes()
    {
        var queryBuilder = CreateQueryBuilder();
        var query = new FeatureQuery
        {
            OutFields = ImmutableArray.Create("population", "objectid", "population")
        };

        var result = queryBuilder.BuildSelectQuery(layerId: 1, query);

        result.Sql.Should().Contain("jsonb_build_object('population', attributes -> $2)::text AS attributes");
        result.Sql.Should().NotContain("SELECT objectid, ST_AsBinary(geometry), attributes FROM");
        result.WhereParameters.Should().Equal("population");
    }

    [Fact]
    public void BuildSelectQuery_WithExcludedAttributes_SelectsNullAttributes()
    {
        var queryBuilder = CreateQueryBuilder();
        var query = new FeatureQuery
        {
            ExcludeAttributes = true
        };

        var result = queryBuilder.BuildSelectQuery(layerId: 1, query);

        result.Sql.Should().Contain("NULL AS attributes");
        result.WhereParameters.Should().BeEmpty();
    }

    [Fact]
    public void BuildOptimizedSelectQuery_WithOutFields_ProjectsRequestedAttributes()
    {
        var queryBuilder = CreateQueryBuilder();
        var query = new FeatureQuery
        {
            Limit = 10,
            OutFields = ImmutableArray.Create("category")
        };

        var result = queryBuilder.BuildOptimizedSelectQuery(layerId: 1, query);

        result.Sql.Should().Contain("jsonb_build_object('category', attributes -> $2)::text AS attributes");
        result.Sql.Should().Contain("COUNT(*) OVER()");
        result.WhereParameters.Should().Equal("category", 10);
    }

    private static FeatureQueryBuilder CreateQueryBuilder()
    {
        var poolProvider = new DefaultObjectPoolProvider();
        var stringBuilderPool = poolProvider.Create(new FeatureStoreStringBuilderPooledObjectPolicy());
        var geometryProcessor = new GeometryProcessor();
        return new FeatureQueryBuilder(stringBuilderPool, geometryProcessor);
    }
}
