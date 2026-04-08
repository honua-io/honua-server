// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.SpatialAnalytics.Domain;
using Honua.Postgres.Features.FeatureStore.Services;
using Microsoft.Extensions.ObjectPool;
using FeatureStoreStringBuilderPooledObjectPolicy = Honua.Postgres.Features.FeatureStore.Services.StringBuilderPooledObjectPolicy;

namespace Honua.Postgres.Tests.Features.FeatureStore;

/// <summary>
/// SQL-shape tests for the spatial analytics query builders. These assertions
/// intentionally look for substrings that guard the review-driven fixes in
/// <see cref="FeatureQueryBuilder"/>.Analytics.* so regressions (e.g. raw
/// <c>geometry</c> references under bytea storage or a trailing LIMIT after
/// buffer aggregation) fail loudly at unit-test time.
/// </summary>
public sealed class FeatureQueryBuilderAnalyticsTests
{
    [Fact]
    public void BuildBufferAggregateQuery_DissolveTrue_AppliesInputLimitInsideSrcCte()
    {
        var queryBuilder = CreateQueryBuilder();
        var query = new FeatureQuery { SpatialReferenceSrid = 3857 };
        var bufferQuery = new BufferAggregateQuery
        {
            Distance = 100d,
            Unit = DistanceUnit.Meters,
            Dissolve = true,
            MaxInputFeatures = 5_000
        };

        var result = queryBuilder.BuildBufferAggregateQuery(layerId: 1, query, bufferQuery);

        // LIMIT must live inside the src CTE so ST_Buffer / ST_Union only see the
        // bounded input set. A trailing LIMIT (after the aggregation) is exactly
        // what the review flagged.
        result.Sql.Should().Contain("WITH src AS (");
        result.Sql.Should().Contain("LIMIT $3)");
        // The aggregation must reference the decoded CTE alias rather than a raw
        // `{column}` expression.
        result.Sql.Should().Contain("ST_Union(ST_Buffer(geom_m");
        result.Sql.Should().Contain("FROM src");
        // No stray trailing LIMIT on the outer query — the only LIMIT in the SQL
        // must be the one that lives inside the src CTE.
        var sqlText = result.Sql;
        sqlText.LastIndexOf("LIMIT", StringComparison.Ordinal)
            .Should().Be(sqlText.IndexOf("LIMIT", StringComparison.Ordinal));
        sqlText.TrimEnd().EndsWith("FROM src").Should().BeTrue();
    }

    [Fact]
    public void BuildBufferAggregateQuery_WithByteaStorage_DecodesGeometryInsideSrcCte()
    {
        var queryBuilder = CreateQueryBuilder();
        var query = new FeatureQuery { SpatialReferenceSrid = 4326 };
        var bufferQuery = new BufferAggregateQuery
        {
            Distance = 250d,
            Unit = DistanceUnit.Meters,
            Dissolve = true,
            MaxInputFeatures = 1_000
        };

        var result = queryBuilder.BuildBufferAggregateQuery(
            layerId: 1, query, bufferQuery, GeometryStorageType.Bytea);

        // The decoded operand must appear in the CTE (not a raw `geometry` reference).
        result.Sql.Should().Contain("ST_SetSRID(ST_GeomFromEWKB(geometry), 4326)");
        result.Sql.Should().Contain("AS geom_m");
        // The outer aggregation should reference the decoded column alias, not raw geometry.
        result.Sql.Should().Contain("ST_Union(ST_Buffer(geom_m");
    }

    [Fact]
    public void BuildClusterQuery_WithByteaStorage_PerFeatureModeUsesTypedGeomInOutput()
    {
        var queryBuilder = CreateQueryBuilder();
        var query = new FeatureQuery { SpatialReferenceSrid = 4326 };
        var clusterQuery = new ClusterQuery
        {
            Algorithm = ClusterAlgorithm.DbScan,
            Eps = 50d,
            MinPoints = 5,
            DistanceUnit = DistanceUnit.Meters,
            ReturnHullPerCluster = false,
            MaxInputFeatures = 10_000
        };

        var result = queryBuilder.BuildClusterQuery(
            layerId: 1, query, clusterQuery, GeometryStorageType.Bytea);

        // The CTE must materialise the decoded geometry under the `geom` alias
        // so the outer SELECT does not call ST_AsGeoJSON on a bytea.
        result.Sql.Should().Contain("ST_SetSRID(ST_GeomFromEWKB(geometry), 4326) AS geom");
        result.Sql.Should().Contain("ST_AsGeoJSON(geom) AS \"geometry\"");
        result.Sql.Should().NotContain("ST_AsGeoJSON(geometry) AS \"geometry\"");
    }

    [Fact]
    public void BuildClusterQuery_WithByteaStorage_HullModeUsesTypedGeomInStCollect()
    {
        var queryBuilder = CreateQueryBuilder();
        var query = new FeatureQuery { SpatialReferenceSrid = 4326 };
        var clusterQuery = new ClusterQuery
        {
            Algorithm = ClusterAlgorithm.DbScan,
            Eps = 50d,
            MinPoints = 5,
            DistanceUnit = DistanceUnit.Meters,
            ReturnHullPerCluster = true,
            MaxInputFeatures = 10_000
        };

        var result = queryBuilder.BuildClusterQuery(
            layerId: 1, query, clusterQuery, GeometryStorageType.Bytea);

        // Hull mode collects the decoded geometry via the `geom` alias — ST_Collect
        // against a raw bytea column would fail at execution time.
        result.Sql.Should().Contain("ST_ConvexHull(ST_Collect(geom))");
        result.Sql.Should().NotContain("ST_Collect(geometry)");
    }

    [Fact]
    public void BuildSpatialJoinQuery_WithByteaStorage_UsesDecodedOperandsInPredicateAndOutput()
    {
        var queryBuilder = CreateQueryBuilder();
        var targetQuery = new FeatureQuery { SpatialReferenceSrid = 4326 };
        var joinQuery = new SpatialJoinQuery
        {
            JoinLayerId = 7,
            Predicate = SpatialJoinPredicate.Intersects,
            MaxInputFeatures = 1_000
        };

        var result = queryBuilder.BuildSpatialJoinQuery(
            targetLayerId: 1, targetQuery, joinQuery, GeometryStorageType.Bytea);

        // Target CTE must carry the decoded geometry under `geom` so the outer
        // SELECT / GROUP BY / predicate can use a typed geometry expression.
        result.Sql.Should().Contain("ST_SetSRID(ST_GeomFromEWKB(geometry), 4326) AS geom");
        result.Sql.Should().Contain("ST_AsGeoJSON(t.geom) AS \"geometry\"");
        result.Sql.Should().Contain("GROUP BY t.objectid, t.attributes, t.geom");

        // Predicate must reference `t.geom` and the decoded j-side expression —
        // raw `t.geometry && j.geometry` would fail under bytea storage.
        result.Sql.Should().Contain(
            "t.geom && ST_SetSRID(ST_GeomFromEWKB(j.geometry), 4326) AND " +
            "ST_Intersects(t.geom, ST_SetSRID(ST_GeomFromEWKB(j.geometry), 4326))");
        result.Sql.Should().NotContain("t.geometry && j.geometry");
    }

    [Fact]
    public void BuildSpatialJoinQuery_WithDWithinByteaStorage_CastsDecodedOperandsToGeography()
    {
        var queryBuilder = CreateQueryBuilder();
        var targetQuery = new FeatureQuery { SpatialReferenceSrid = 4326 };
        var joinQuery = new SpatialJoinQuery
        {
            JoinLayerId = 7,
            Predicate = SpatialJoinPredicate.DWithin,
            DistanceMeters = 500d,
            MaxInputFeatures = 1_000
        };

        var result = queryBuilder.BuildSpatialJoinQuery(
            targetLayerId: 1, targetQuery, joinQuery, GeometryStorageType.Bytea);

        // DWithin now casts the decoded operands to geography, not the raw bytea column.
        result.Sql.Should().Contain(
            "ST_DWithin(t.geom::geography, ST_SetSRID(ST_GeomFromEWKB(j.geometry), 4326)::geography");
        result.Sql.Should().NotContain("t.geometry::geography");
    }

    [Fact]
    public void BuildSpatialJoinQuery_WithGeometryStorage_KeepsRawJoinColumnForIndexFriendlyBbox()
    {
        var queryBuilder = CreateQueryBuilder();
        var targetQuery = new FeatureQuery { SpatialReferenceSrid = 4326 };
        var joinQuery = new SpatialJoinQuery
        {
            JoinLayerId = 7,
            Predicate = SpatialJoinPredicate.Intersects,
            MaxInputFeatures = 1_000
        };

        var result = queryBuilder.BuildSpatialJoinQuery(
            targetLayerId: 1, targetQuery, joinQuery, GeometryStorageType.Geometry);

        // Under Geometry storage the decoded operand is just the raw column, so the
        // GIST-friendly `j.geometry && ...` fast path is preserved.
        result.Sql.Should().Contain("t.geom && j.geometry AND ST_Intersects(t.geom, j.geometry)");
    }

    private static FeatureQueryBuilder CreateQueryBuilder()
    {
        var poolProvider = new DefaultObjectPoolProvider();
        var stringBuilderPool = poolProvider.Create(new FeatureStoreStringBuilderPooledObjectPolicy());
        var geometryProcessor = new GeometryProcessor();
        return new FeatureQueryBuilder(stringBuilderPool, geometryProcessor);
    }
}
