// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Queries.Filters;
using Honua.Db.Postgres.Features.FeatureStore.Services;
using Microsoft.Extensions.ObjectPool;
using FeatureStoreStringBuilderPooledObjectPolicy = Honua.Db.Postgres.Features.FeatureStore.Services.StringBuilderPooledObjectPolicy;

namespace Honua.Db.Postgres.Tests.Features.FeatureStore;

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

        // The jsonb key is bound as a parameter alongside the value accessor (both reuse
        // $2), so no part of a field name is interpolated into the SQL text.
        result.Sql.Should().Contain("jsonb_build_object($2::text, attributes -> $2::text)::text AS attributes");
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

        result.Sql.Should().Contain("jsonb_build_object($2::text, attributes -> $2::text)::text AS attributes");
        result.Sql.Should().Contain("COUNT(*) OVER()");
        result.Sql.Should().Contain("LIMIT $");
        // The optimized builder emits the LIMIT placeholder but does not append the pagination
        // value to WhereParameters — pagination is bound by AddQueryParameters, matching the
        // non-optimized BuildSelectQuery convention. Appending it here previously double-bound
        // pagination at execution (honua-server#1749). Only the OutFields projection parameter
        // ("category") belongs in WhereParameters.
        result.WhereParameters.Should().Equal("category");
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
    public void BuildSelectQuery_WithFirstPageLikeFilter_AddsImplicitObjectIdOrderBy()
    {
        // A first page (Limit, no Offset) with a LIKE filter must still get a stable tiebreaker
        // ORDER BY objectid. Without it the database returns an arbitrary heap/plan order while
        // the next page (which injects ORDER BY objectid because it carries an Offset) is drawn
        // from a different ordering, silently skipping and duplicating rows across the page
        // boundary (#2070). Mirrors the sibling MySQL provider, which has no LIKE carve-out.
        var queryBuilder = CreateQueryBuilder();
        var query = new FeatureQuery
        {
            Limit = 10,
            SqlFilter = new SqlFragment("attributes->>'feature_name' LIKE @p0", new object?[] { "feature\\_999%" })
        };

        var result = queryBuilder.BuildSelectQuery(layerId: 1, query);

        result.Sql.Should().Contain("ORDER BY objectid ASC");
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
            OrderBy = ImmutableArray.Create(new OrderByClause("id", ascending: true, fieldType: MetadataV2FieldType.String))
        };

        var result = queryBuilder.BuildSelectQuery(layerId: 1, query);

        result.Sql.Should().Contain("ORDER BY attributes->> $2 ASC, objectid ASC");
        result.Sql.Should().Contain("LIMIT $3");
        result.WhereParameters.Should().Contain("id");
    }

    [Fact]
    public void BuildOptimizedSelectGmlQuery_WithCountAndStartIndex_DoesNotDoubleBindPagination()
    {
        // Regression for honua-server#1749/#1750: WFS GetFeature (and OGC Features/KML) reads with a
        // count + startIndex route through the optimized window-count GML builder
        // (PostgresFeatureStore.BuildGmlFeatureCollection -> BuildOptimizedSelectGmlQuery). It
        // previously appended the LIMIT/OFFSET *values* to WhereParameters in addition to emitting
        // their placeholders, while FeatureDataAccess.AddQueryParameters binds pagination too —
        // leaving more bound parameters than SQL placeholders. On the prepared-statement cache path
        // the misalignment left a pagination placeholder unbound -> "Parameter cannot be null" ->
        // HTTP 500 on the advertised WFS type. The sibling Esri-JSON builder is covered above; this
        // locks the GML path (the one the WFS JS suite hits) against the same re-regression.
        var queryBuilder = CreateQueryBuilder();
        var query = new FeatureQuery
        {
            Limit = 5,
            Offset = 10,
        };

        var result = queryBuilder.BuildOptimizedSelectGmlQuery(layerId: 1, query);

        result.Sql.Should().Contain("COUNT(*) OVER()");
        result.Sql.Should().Contain("LIMIT $").And.Contain("OFFSET $");
        // No OutFields/WHERE/spatial params here, so the LIMIT/OFFSET values must be the only thing
        // that could land in WhereParameters — and they must NOT, since AddQueryParameters binds
        // them positionally. An empty list proves the double-bind is gone.
        result.WhereParameters.Should().BeEmpty();
    }

    [Fact]
    public void BuildSelectQuery_WithPrefixedExtensionField_ProjectsFieldWithParameterBoundKey()
    {
        // honua-server#3392: a declared, queryable STAC/EO extension property such as
        // `eo:cloud_cover` was rejected by the identifier-shaped validator and never
        // reached the projection, so the server advertised the field in /queryables, the
        // CSV header and the GeoServices `fields` array and then omitted it from every
        // feature payload. A jsonb key is not a SQL identifier; it is bound as a
        // parameter, so the colon is legal here.
        var queryBuilder = CreateQueryBuilder();
        var query = new FeatureQuery
        {
            OutFields = ImmutableArray.Create("eo:cloud_cover")
        };

        var result = queryBuilder.BuildSelectQuery(layerId: 1, query);

        result.Sql.Should().Contain("jsonb_build_object($2::text, attributes -> $2::text)::text AS attributes");
        result.Sql.Should().NotContain("eo:cloud_cover");
        result.WhereParameters.Should().Equal("eo:cloud_cover");
    }

    [Theory]
    [InlineData("name'); DROP TABLE features; --")]
    [InlineData("name; DELETE FROM features")]
    [InlineData("name' || (SELECT 1) || '")]
    [InlineData("field name")]
    [InlineData("\"quoted\"")]
    [InlineData("field\n")]
    public void BuildSelectQuery_WithInjectionShapedOutField_ThrowsAndNeverEmitsTheName(string fieldName)
    {
        var queryBuilder = CreateQueryBuilder();
        var query = new FeatureQuery
        {
            OutFields = ImmutableArray.Create(fieldName)
        };

        var act = () => queryBuilder.BuildSelectQuery(layerId: 1, query);

        act.Should().Throw<ArgumentException>()
            .WithMessage("*Invalid field name for projection*");
    }

    [Fact]
    public void BuildSelectQuery_WithPrefixedExtensionOrderByField_OrdersByParameterBoundAttribute()
    {
        // The OGC adapter accepts `sortby=eo:cloud_cover` and resolves it against the
        // declared schema; the ORDER BY builder binds the jsonb key as a parameter, so it
        // must accept the same name instead of raising an unhandled ArgumentException
        // (which surfaces as HTTP 500).
        var queryBuilder = CreateQueryBuilder();
        var query = new FeatureQuery
        {
            Limit = 10,
            OrderBy = ImmutableArray.Create(
                new OrderByClause("eo:cloud_cover", ascending: false, fieldType: MetadataV2FieldType.Double))
        };

        var result = queryBuilder.BuildSelectQuery(layerId: 1, query);

        result.Sql.Should().Contain("attributes->> $2");
        result.Sql.Should().NotContain("eo:cloud_cover");
        result.WhereParameters.Should().Contain("eo:cloud_cover");
    }

    private static FeatureQueryBuilder CreateQueryBuilder()
    {
        var poolProvider = new DefaultObjectPoolProvider();
        var stringBuilderPool = poolProvider.Create(new FeatureStoreStringBuilderPooledObjectPolicy());
        var geometryProcessor = new GeometryProcessor();
        return new FeatureQueryBuilder(stringBuilderPool, geometryProcessor);
    }
}
