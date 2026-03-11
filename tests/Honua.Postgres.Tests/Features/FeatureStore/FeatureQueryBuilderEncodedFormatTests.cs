// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Domain;
using Honua.Postgres.Features.FeatureStore.Services;
using Microsoft.Extensions.ObjectPool;
using FeatureStoreStringBuilderPooledObjectPolicy = Honua.Postgres.Features.FeatureStore.Services.StringBuilderPooledObjectPolicy;

namespace Honua.Postgres.Tests.Features.FeatureStore;

public sealed class FeatureQueryBuilderEncodedFormatTests
{
    [Fact]
    public void BuildSelectGeoJsonQuery_UsesPostGisEncoder()
    {
        var queryBuilder = CreateQueryBuilder();

        var result = queryBuilder.BuildSelectGeoJsonQuery(layerId: 1, query: new FeatureQuery());

        result.Sql.Should().Contain("ST_AsGeoJSON(");
        result.Sql.Should().Contain("AS geometry");
        result.Sql.Should().NotContain("ST_AsBinary(");
    }

    [Fact]
    public void BuildSelectKmlQuery_UsesPostGisEncoder()
    {
        var queryBuilder = CreateQueryBuilder();

        var result = queryBuilder.BuildSelectKmlQuery(layerId: 1, query: new FeatureQuery());

        result.Sql.Should().Contain("ST_AsKML(");
        result.Sql.Should().Contain("AS geometry");
        result.Sql.Should().NotContain("ST_AsBinary(");
    }

    [Fact]
    public void BuildSelectGeobufQuery_UsesPostGisEncoder()
    {
        var queryBuilder = CreateQueryBuilder();

        var result = queryBuilder.BuildSelectGeobufQuery(layerId: 1, query: new FeatureQuery());

        result.Sql.Should().Contain("SELECT ST_AsGeobuf(q, 'geometry') FROM (");
        result.Sql.Should().Contain("AS geometry");
        result.Sql.Should().NotContain("ST_AsBinary(");
    }

    private static FeatureQueryBuilder CreateQueryBuilder()
    {
        var poolProvider = new DefaultObjectPoolProvider();
        var stringBuilderPool = poolProvider.Create(new FeatureStoreStringBuilderPooledObjectPolicy());
        var geometryProcessor = new GeometryProcessor();
        return new FeatureQueryBuilder(stringBuilderPool, geometryProcessor);
    }
}
