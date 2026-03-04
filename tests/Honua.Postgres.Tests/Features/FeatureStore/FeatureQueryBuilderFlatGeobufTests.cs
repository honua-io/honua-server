// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Postgres.Features.FeatureStore.Services;
using Microsoft.Extensions.ObjectPool;
using FeatureStoreStringBuilderPooledObjectPolicy = Honua.Postgres.Features.FeatureStore.Services.StringBuilderPooledObjectPolicy;

namespace Honua.Postgres.Tests.Features.FeatureStore;

public sealed class FeatureQueryBuilderFlatGeobufTests
{
    [Fact]
    public void BuildSelectFlatGeobufQuery_UsesPostGisEncoder()
    {
        var poolProvider = new DefaultObjectPoolProvider();
        var stringBuilderPool = poolProvider.Create(new FeatureStoreStringBuilderPooledObjectPolicy());
        var geometryProcessor = new GeometryProcessor();
        var queryBuilder = new FeatureQueryBuilder(stringBuilderPool, geometryProcessor);

        var result = queryBuilder.BuildSelectFlatGeobufQuery(layerId: 1, query: new FeatureQuery());

        result.Sql.Should().Contain("SELECT ST_AsFlatGeobuf(q, true, 'geometry') FROM (");
        result.Sql.Should().Contain("AS geometry");
        result.Sql.Should().Contain("attributes::text AS attributes");
    }

    [Fact]
    public void BuildSelectFlatGeobufQuery_WithOutFields_ProjectsRequestedAttributes()
    {
        var poolProvider = new DefaultObjectPoolProvider();
        var stringBuilderPool = poolProvider.Create(new FeatureStoreStringBuilderPooledObjectPolicy());
        var geometryProcessor = new GeometryProcessor();
        var queryBuilder = new FeatureQueryBuilder(stringBuilderPool, geometryProcessor);
        var query = new FeatureQuery
        {
            OutFields = ImmutableArray.Create("name", "category")
        };

        var result = queryBuilder.BuildSelectFlatGeobufQuery(layerId: 1, query: query);

        result.Sql.Should().Contain("attributes->> $2 AS \"name\"");
        result.Sql.Should().Contain("attributes->> $3 AS \"category\"");
        result.WhereParameters.Should().Equal("name", "category");
    }
}
