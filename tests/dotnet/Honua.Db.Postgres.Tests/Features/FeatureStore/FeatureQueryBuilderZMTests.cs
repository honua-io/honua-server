// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Postgres.Features.FeatureStore.Services;
using Microsoft.Extensions.ObjectPool;
using FeatureStoreStringBuilderPooledObjectPolicy = Honua.Postgres.Features.FeatureStore.Services.StringBuilderPooledObjectPolicy;

namespace Honua.Postgres.Tests.Features.FeatureStore;

/// <summary>
/// Unit tests for the Z/M (3D/measured) read-path SQL generation (#1877 Part A): a query that asks to
/// preserve Z/M must read extended WKB (ST_AsEWKB) so the higher ordinates survive to the converter;
/// the default read stays 2D OGC WKB (ST_AsBinary), byte-identical to the historical behavior.
/// </summary>
public sealed class FeatureQueryBuilderZMTests
{
    private static FeatureQueryBuilder CreateQueryBuilder()
    {
        var poolProvider = new DefaultObjectPoolProvider();
        var stringBuilderPool = poolProvider.Create(new FeatureStoreStringBuilderPooledObjectPolicy());
        return new FeatureQueryBuilder(stringBuilderPool, new GeometryProcessor());
    }

    [Fact]
    public void GeometrySelect_WithoutZorM_UsesPlainWkb()
    {
        var result = new GeometryProcessor().GetGeometrySelectExpression(
            Honua.Core.Features.FeatureStore.Abstractions.GeometryStorageType.Geometry,
            new FeatureQuery());

        result.Should().Contain("ST_AsBinary(");
        result.Should().NotContain("ST_AsEWKB(");
    }

    [Fact]
    public void GeometrySelect_WithIncludeZ_UsesExtendedWkb()
    {
        var result = new GeometryProcessor().GetGeometrySelectExpression(
            Honua.Core.Features.FeatureStore.Abstractions.GeometryStorageType.Geometry,
            new FeatureQuery { IncludeZ = true });

        result.Should().Contain("ST_AsEWKB(");
        result.Should().NotContain("ST_AsBinary(");
    }

    [Fact]
    public void GeometrySelect_WithIncludeM_UsesExtendedWkb()
    {
        var result = new GeometryProcessor().GetGeometrySelectExpression(
            Honua.Core.Features.FeatureStore.Abstractions.GeometryStorageType.Geometry,
            new FeatureQuery { IncludeM = true });

        result.Should().Contain("ST_AsEWKB(");
    }

    [Fact]
    public void BuildOptimizedSelectQuery_WithIncludeZ_EmitsExtendedWkb()
    {
        var queryBuilder = CreateQueryBuilder();

        var result = queryBuilder.BuildOptimizedSelectQuery(
            layerId: 1,
            query: new FeatureQuery { IncludeZ = true });

        result.Sql.Should().Contain("ST_AsEWKB(");
    }

    [Fact]
    public void BuildOptimizedSelectQuery_Default_EmitsPlainWkb()
    {
        var queryBuilder = CreateQueryBuilder();

        var result = queryBuilder.BuildOptimizedSelectQuery(
            layerId: 1,
            query: new FeatureQuery());

        result.Sql.Should().Contain("ST_AsBinary(");
        result.Sql.Should().NotContain("ST_AsEWKB(");
    }

    [Fact]
    public void BuildTopFeaturesQuery_WithIncludeZ_EmitsExtendedWkb()
    {
        var queryBuilder = CreateQueryBuilder();

        var query = new FeatureQuery
        {
            IncludeZ = true,
            TopFilter = new TopFilter
            {
                TopCount = 1,
                GroupByFields = ImmutableArray.Create("category"),
                OrderByFields = ImmutableArray.Create(new OrderByClause("category", ascending: true)),
            },
        };

        var result = queryBuilder.BuildTopFeaturesQuery(layerId: 1, query);

        result.Sql.Should().Contain("ST_AsEWKB(");
    }
}
