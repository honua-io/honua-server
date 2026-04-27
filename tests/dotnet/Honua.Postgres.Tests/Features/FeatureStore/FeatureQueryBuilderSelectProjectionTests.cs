// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Queries.Filters;
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

    [Fact]
    public void BuildSelectQuery_WithLimitOnly_AddsImplicitObjectIdOrderBy()
    {
        var queryBuilder = CreateQueryBuilder();
        var query = new FeatureQuery
        {
            Limit = 10
        };

        var result = queryBuilder.BuildSelectQuery(layerId: 1, query);

        result.Sql.Should().Contain("ORDER BY objectid ASC");
        result.Sql.Should().Contain("LIMIT $2");
        result.WhereParameters.Should().BeEmpty();
    }

    [Fact]
    public void BuildSelectQuery_WithFirstPageLikeFilter_DoesNotAddImplicitObjectIdOrderBy()
    {
        var queryBuilder = CreateQueryBuilder();
        var query = new FeatureQuery
        {
            Limit = 10,
            SqlFilter = new SqlFragment("attributes->>'feature_name' LIKE @p0", new object?[] { "feature\\_999%" })
        };

        var result = queryBuilder.BuildSelectQuery(layerId: 1, query);

        result.Sql.Should().NotContain("ORDER BY objectid ASC");
        result.Sql.Should().Contain("LIMIT $3");
        result.WhereParameters.Should().Equal("feature\\_999%");
    }

    [Fact]
    public void BuildSelectQuery_WithOffset_AddsImplicitObjectIdOrderBy()
    {
        var queryBuilder = CreateQueryBuilder();
        var query = new FeatureQuery
        {
            Limit = 10,
            Offset = 20
        };

        var result = queryBuilder.BuildSelectQuery(layerId: 1, query);

        result.Sql.Should().Contain("ORDER BY objectid ASC");
        result.Sql.Should().Contain("LIMIT $2 OFFSET $3");
        result.WhereParameters.Should().BeEmpty();
    }

    [Fact]
    public void BuildSelectQuery_WithSpatialFilter_AddsImplicitObjectIdOrderBy()
    {
        var queryBuilder = CreateQueryBuilder();
        var query = new FeatureQuery
        {
            SpatialReferenceSrid = 4326,
            Limit = 10,
            SpatialFilter = SpatialFilter.Create(
                new byte[] { 1, 2, 3 },
                SpatialRelationship.EnvelopeIntersects,
                srid: 4326)
        };

        var result = queryBuilder.BuildSelectQuery(layerId: 1, query);

        result.Sql.Should().Contain("ORDER BY objectid ASC");
        result.Sql.Should().Contain("LIMIT $3");
        result.WhereParameters.Should().ContainSingle().Which.Should().BeEquivalentTo(new byte[] { 1, 2, 3 });
    }

    [Fact]
    public void BuildSelectQuery_WithTypedIdOrderBy_OrdersByPublicIdAttribute()
    {
        var queryBuilder = CreateQueryBuilder();
        var query = new FeatureQuery
        {
            Limit = 10,
            OrderBy = ImmutableArray.Create(new OrderByClause("id", ascending: true, fieldType: FieldType.String))
        };

        var result = queryBuilder.BuildSelectQuery(layerId: 1, query);

        result.Sql.Should().Contain("ORDER BY attributes->> $2 ASC, objectid ASC");
        result.Sql.Should().Contain("LIMIT $3");
        result.WhereParameters.Should().Contain("id");
    }

    private static FeatureQueryBuilder CreateQueryBuilder()
    {
        var poolProvider = new DefaultObjectPoolProvider();
        var stringBuilderPool = poolProvider.Create(new FeatureStoreStringBuilderPooledObjectPolicy());
        var geometryProcessor = new GeometryProcessor();
        return new FeatureQueryBuilder(stringBuilderPool, geometryProcessor);
    }
}
