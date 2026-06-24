// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Queries.Filters;
using Honua.Postgres.Features.FeatureStore.Services;
using Microsoft.Extensions.ObjectPool;
using FeatureStoreStringBuilderPooledObjectPolicy = Honua.Postgres.Features.FeatureStore.Services.StringBuilderPooledObjectPolicy;

namespace Honua.Postgres.Tests.Features.FeatureStore;

public sealed class FeatureQueryBuilderOrderingTests
{
    private static FeatureQueryBuilder CreateBuilder()
    {
        var poolProvider = new DefaultObjectPoolProvider();
        var stringBuilderPool = poolProvider.Create(new FeatureStoreStringBuilderPooledObjectPolicy());
        return new FeatureQueryBuilder(stringBuilderPool, new GeometryProcessor());
    }

    [Fact]
    public void BuildQuery_OrderByWithoutNullOrdering_EmitsNoNullsQualifier()
    {
        // Protocols that do not request explicit null placement (CQL2/WFS/GeoServices)
        // must keep the PostgreSQL defaults — no NULLS FIRST/LAST is emitted.
        var queryBuilder = CreateBuilder();
        var query = new FeatureQuery
        {
            OrderBy = ImmutableArray.Create(
                new OrderByClause("population", ascending: true, MetadataV2FieldType.Integer),
                new OrderByClause("name", ascending: false))
        };

        var result = queryBuilder.BuildSelectRawGeoJsonQuery(layerId: 1, query);

        result.Sql.Should().Contain("ORDER BY");
        result.Sql.Should().NotContain("NULLS");
    }

    [Fact]
    public void BuildQuery_OrderByWithNullsFirst_EmitsNullsFirstQualifier()
    {
        // OData v4 $orderby ascending requests NULLS FIRST (inverse of the PG default).
        var queryBuilder = CreateBuilder();
        var query = new FeatureQuery
        {
            OrderBy = ImmutableArray.Create(
                new OrderByClause("population", ascending: true, MetadataV2FieldType.Integer)
                {
                    NullOrdering = NullOrdering.NullsFirst
                })
        };

        var result = queryBuilder.BuildSelectRawGeoJsonQuery(layerId: 1, query);

        result.Sql.Should().Contain("ASC NULLS FIRST");
    }

    [Fact]
    public void BuildQuery_OrderByWithNullsLast_EmitsNullsLastQualifier()
    {
        // OData v4 $orderby descending requests NULLS LAST (inverse of the PG default).
        var queryBuilder = CreateBuilder();
        var query = new FeatureQuery
        {
            OrderBy = ImmutableArray.Create(
                new OrderByClause("population", ascending: false, MetadataV2FieldType.Integer)
                {
                    NullOrdering = NullOrdering.NullsLast
                })
        };

        var result = queryBuilder.BuildSelectRawGeoJsonQuery(layerId: 1, query);

        result.Sql.Should().Contain("DESC NULLS LAST");
    }

    [Fact]
    public void BuildQuery_MixedNullOrderingClauses_EmitsQualifierPerClause()
    {
        var queryBuilder = CreateBuilder();
        var query = new FeatureQuery
        {
            OrderBy = ImmutableArray.Create(
                new OrderByClause("population", ascending: true, MetadataV2FieldType.Integer)
                {
                    NullOrdering = NullOrdering.NullsFirst
                },
                new OrderByClause("name", ascending: false))
        };

        var result = queryBuilder.BuildSelectRawGeoJsonQuery(layerId: 1, query);

        result.Sql.Should().Contain("ASC NULLS FIRST");
        result.Sql.Should().Contain("DESC");
        result.Sql.Should().NotContain("DESC NULLS");
    }

    [Fact]
    public void BuildQuery_FirstPageLikeFilter_InjectsStableObjectIdOrder()
    {
        // A first page (Limit, no Offset, no OrderBy) with a LIKE filter must still receive a
        // stable tiebreaker ORDER BY objectid. Without it, page 1 returns an arbitrary
        // heap/plan order while page 2 (which carries an Offset and therefore injects
        // ORDER BY objectid) is drawn from a different ordering, silently skipping and
        // duplicating rows across the page boundary (#2070).
        var queryBuilder = CreateBuilder();
        var query = new FeatureQuery
        {
            Limit = 10,
            SqlFilter = new SqlFragment("attributes->>'name' LIKE @p0", new object?[] { "A%" })
        };

        var result = queryBuilder.BuildSelectRawGeoJsonQuery(layerId: 1, query);

        result.Sql.Should().Contain("ORDER BY objectid ASC");
    }
}
