// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.SpatialAnalytics.Domain;
using Honua.Db.Postgres.Features.FeatureStore.Services;
using Honua.TestKit;
using Microsoft.Extensions.ObjectPool;
using Xunit.Abstractions;

namespace Honua.Db.Postgres.Tests.Features.FeatureStore;

[Collection("Database")]
public sealed class AeqdProjectionProfilingTests(PostgresFixture fixture, ITestOutputHelper output)
{
    private const string Pipeline = "+proj=pipeline +step +proj=unitconvert +xy_in=deg +xy_out=rad +step +proj=aeqd +lat_0=39.15 +lon_0=-110.85 +ellps=WGS84 +units=m";

    [Fact]
    public async Task ProjectionAndClusterPlans_ReportActualCostsFor100KPoints()
    {
        var schema = await fixture.CreateIsolatedSchemaAsync("AeqdProfile");
        try
        {
            await fixture.ExecuteAsync($$"""
                CREATE TABLE {{schema}}.features (
                    objectid bigint PRIMARY KEY, layer_id integer, geometry geometry,
                    attributes jsonb);
                INSERT INTO {{schema}}.features
                SELECT i, 1, ST_SetSRID(ST_MakePoint(
                    -100.0 + (((i::bigint * 2654435761) % 1000000)::double precision / 1000000.0),
                    40.0 + (((i::bigint * 40503) % 1000000)::double precision / 1000000.0)), 4326),
                    jsonb_build_object('name', 'perf-' || i, 'bucket', i % 50)
                FROM generate_series(1, 100000) i;
                ANALYZE {{schema}}.features;
                """);

            await ExplainAsync("per-row projection", $"SELECT SUM(ST_X(ST_TransformPipeline(geometry, '{Pipeline}'))) FROM {schema}.features");
            await ExplainAsync("collection projection", $"SELECT ST_TransformPipeline(ST_Collect(geometry ORDER BY objectid), '{Pipeline}') FROM {schema}.features");
            var pool = new DefaultObjectPoolProvider().Create(
                new Honua.Db.Postgres.Features.FeatureStore.Services.StringBuilderPooledObjectPolicy());
            var builder = new FeatureQueryBuilder(pool, new GeometryProcessor(), schema);
            var query = builder.BuildClusterQuery(1, new FeatureQuery { SpatialReferenceSrid = 4326 }, new ClusterQuery
            {
                Algorithm = ClusterAlgorithm.DbScan,
                Eps = 1000,
                MinPoints = 5,
                MaxInputFeatures = 100000,
                MaxClusters = 100000
            });
            await ExplainAsync("current generated cluster query", query.Sql, [1, .. query.WhereParameters]);
            var candidateSource = $"""
                WITH filtered AS MATERIALIZED (
                    SELECT objectid, attributes, geometry AS geom FROM {schema}.features
                    WHERE layer_id = 1 AND geometry IS NOT NULL LIMIT 100001
                ), {AeqdPointBatchCandidateTests.CandidateCtes}
                """;
            await ExplainAsync("candidate point-batched cluster query", candidateSource + """
                , src AS (
                    SELECT objectid, attributes, geom,
                        ST_ClusterDBSCAN(geom_m, eps => 1000, minpoints => 5)
                            OVER (ORDER BY source_ordinal) AS cluster_id FROM projected
                )
                SELECT objectid AS "objectId", cluster_id::bigint AS "clusterId", attributes,
                    ST_AsGeoJSON(ST_Transform(geom, 4326)) AS geometry
                FROM src ORDER BY cluster_id NULLS LAST, "objectId"
                """);
            await using var connection = await fixture.DataSource.OpenConnectionAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = candidateSource + " SELECT MAX(cardinality(source_ordinals)), SUM(cardinality(source_ordinals))::bigint FROM point_batches";
            await using var reader = await command.ExecuteReaderAsync();
            (await reader.ReadAsync()).Should().BeTrue();
            reader.GetInt32(0).Should().Be(256);
            reader.GetInt64(1).Should().Be(100000);
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    [Theory]
    [InlineData("POINT EMPTY", "POINT(-100 40)", "POINT(-100 40)")]
    [InlineData("MULTIPOINT((-100 40),(-100.1 40.1))", "POINT(-100 40)", "POINT EMPTY")]
    [InlineData("POLYGON((-100 40,-99.9 40,-99.9 40.1,-100 40))", "LINESTRING(-100 40,-99.9 40.1)", "POLYGON EMPTY")]
    [InlineData("GEOMETRYCOLLECTION(POINT(-100 40),LINESTRING(-100 40,-99.9 40.1))", "GEOMETRYCOLLECTION EMPTY", "POINT(-100 40)")]
    [InlineData("POINT Z(-100 40 10)", "POINT Z EMPTY", "POINT Z(-100 40 20)")]
    public async Task CollectionProjection_PreservesOrderedTopLevelFeatures(string first, string second, string third)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            WITH source AS (
                SELECT id, ST_GeomFromText(wkt, 4326) AS geom
                FROM (VALUES (1, $1::text), (2, $2::text), (3, $3::text)) v(id, wkt)
            ), projected AS (
                SELECT ST_TransformPipeline(ST_Collect(geom ORDER BY id), '{Pipeline}') AS geom
                FROM source
            )
            SELECT id,
                ST_AsEWKT(ST_TransformPipeline(source.geom, '{Pipeline}')),
                ST_AsEWKT(ST_GeometryN(projected.geom, id)),
                ST_NumGeometries(projected.geom)
            FROM source CROSS JOIN projected ORDER BY id
            """;
        command.Parameters.AddWithValue(first);
        command.Parameters.AddWithValue(second);
        command.Parameters.AddWithValue(third);
        await using var reader = await command.ExecuteReaderAsync();
        var count = 0;
        while (await reader.ReadAsync())
        {
            reader.GetInt32(3).Should().Be(3);
            reader.GetString(2).Should().Be(reader.GetString(1));
            count++;
        }
        count.Should().Be(3);
    }

    private async Task ExplainAsync(string name, string sql, List<object>? parameters = null)
    {
        output.WriteLine(name);
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "EXPLAIN (ANALYZE, BUFFERS, FORMAT TEXT) " + sql;
        command.CommandTimeout = 120;
        if (parameters is not null)
        {
            foreach (var parameter in parameters)
            {
                command.Parameters.AddWithValue(parameter);
            }
        }
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            output.WriteLine(reader.GetString(0));
        }
    }
}
