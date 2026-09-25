// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Honua.TestKit;
using Xunit.Abstractions;

namespace Honua.Db.Postgres.Tests.Features.FeatureStore;

[Collection("Database")]
public sealed class AeqdPointBatchCandidateTests(PostgresFixture fixture, ITestOutputHelper output)
{
    internal static string PipelineSql => "('+proj=pipeline +step +proj=unitconvert +xy_in=deg +xy_out=rad +step +proj=aeqd +lat_0=' || " +
        "(SELECT to_char(ST_Y(ST_Centroid(ST_Extent(geom))), 'FM999990.999999999') FROM filtered) || ' +lon_0=' || " +
        "(SELECT to_char(ST_X(ST_Centroid(ST_Extent(geom))), 'FM999990.999999999') FROM filtered) || ' +ellps=WGS84 +units=m')";

    internal static string CandidateCtes => $"""
        numbered AS MATERIALIZED (
            SELECT *, row_number() OVER () AS source_ordinal FROM filtered
        ), point_batches AS MATERIALIZED (
            SELECT (source_ordinal - 1) / 256 AS batch_id, ST_Zmflag(geom) AS dimensions,
                array_agg(source_ordinal ORDER BY source_ordinal) AS source_ordinals,
                ST_TransformPipeline(ST_Collect(ST_SetSRID(geom, 0) ORDER BY source_ordinal), {PipelineSql}) AS geom_m
            FROM numbered
            WHERE ST_GeometryType(geom) = 'ST_Point' AND NOT ST_IsEmpty(geom)
            GROUP BY (source_ordinal - 1) / 256, ST_Zmflag(geom)
        ), point_projection AS (
            SELECT ids.source_ordinal, ST_GeometryN(b.geom_m, ids.position::integer) AS geom_m
            FROM point_batches b CROSS JOIN LATERAL
                unnest(b.source_ordinals) WITH ORDINALITY ids(source_ordinal, position)
        ), projected AS MATERIALIZED (
            SELECT n.objectid, n.attributes, n.geom, n.source_ordinal,
                CASE WHEN p.source_ordinal IS NOT NULL THEN p.geom_m
                    ELSE ST_TransformPipeline(n.geom, {PipelineSql}) END AS geom_m
            FROM numbered n LEFT JOIN point_projection p USING (source_ordinal)
            ORDER BY n.source_ordinal
        )
        """;

    [Theory]
    [InlineData(0, 45, 100, false)]
    [InlineData(20, 85, 700, false)]
    [InlineData(179.9, 70, 100, false)]
    [InlineData(20, 85, 9, false)]
    [InlineData(20, 85, 700, true)]
    public async Task Candidate_RestoresCoordinatesAndPartitions_AcrossDimensionsFiltersAndCaps(
        double longitude, double latitude, int cap, bool kmeans)
    {
        var schema = await fixture.CreateIsolatedSchemaAsync("AeqdCandidate");
        try
        {
            var x = longitude.ToString("G17", CultureInfo.InvariantCulture);
            var y = latitude.ToString("G17", CultureInfo.InvariantCulture);
            await fixture.ExecuteAsync($$"""
                CREATE TABLE {{schema}}.features (
                    objectid bigint PRIMARY KEY, geometry geometry, attributes jsonb);
                WITH points AS (
                    SELECT ST_SetSRID(ST_MakePoint({{x}}, {{y}}), 4326) AS p,
                        ST_SetSRID(ST_MakePoint({{x}} + 0.0001, {{y}} + 0.0001), 4326) AS q,
                        ST_SetSRID(ST_MakePoint({{x}} + 0.0001, {{y}}), 4326) AS r
                )
                INSERT INTO {{schema}}.features
                SELECT id, geom, jsonb_build_object('keep', id <> 16)
                FROM points, LATERAL (VALUES
                    (1, p), (2, p), (3, p), (4, q),
                    (5, ST_Force3DZ(p, 10)), (6, ST_Force3DM(p, 20)), (7, ST_Force4D(p, 10, 20)),
                    (8, ST_GeomFromText('POINT EMPTY', 4326)),
                    (9, ST_MakeLine(p, q)),
                    (10, ST_MakePolygon(ST_MakeLine(ARRAY[p, q, r, p]))),
                    (11, ST_Collect(p, ST_MakeLine(p, q))),
                    (12, ST_SetSRID(ST_MakePoint(CASE WHEN {{x}} > 179 THEN -{{x}} ELSE {{x}} + 1 END, {{y}}), 4326)),
                    (13, ST_Collect(p, q)), (14, ST_GeomFromText('POINT Z EMPTY', 4326)),
                    (15, ST_GeomFromText('GEOMETRYCOLLECTION EMPTY', 4326)), (16, p)
                ) v(id, geom);
                INSERT INTO {{schema}}.features
                SELECT i, ST_SetSRID(ST_MakePoint({{x}} + (i % 50) * 0.0001, {{y}}), 4326),
                    jsonb_build_object('keep', true)
                FROM generate_series(100, 699) i;
                """);
            var algorithm = kmeans ? "ST_ClusterKMeans(geom_m, 3)" : "ST_ClusterDBSCAN(geom_m, eps => 1000, minpoints => 2)";
            await using var connection = await fixture.DataSource.OpenConnectionAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"""
                WITH filtered AS MATERIALIZED (
                    SELECT objectid, attributes, geometry AS geom FROM {schema}.features
                    WHERE objectid > 1 AND attributes->>'keep' = 'true'
                    ORDER BY objectid LIMIT $1
                ), {CandidateCtes}, original AS MATERIALIZED (
                    SELECT objectid, source_ordinal, ST_TransformPipeline(geom, {PipelineSql}) AS geom_m
                    FROM numbered
                ), expected AS (
                    SELECT *, {algorithm} OVER (ORDER BY source_ordinal) AS cluster_id FROM original
                ), actual AS (
                    SELECT *, {algorithm} OVER (ORDER BY source_ordinal) AS cluster_id FROM projected
                )
                SELECT e.objectid, ST_AsEWKB(e.geom_m) = ST_AsEWKB(a.geom_m), e.cluster_id, a.cluster_id
                FROM expected e FULL JOIN actual a USING (objectid) ORDER BY e.objectid
                """;
            command.Parameters.AddWithValue(cap + 1);
            await using var reader = await command.ExecuteReaderAsync();
            var rows = new List<(long Id, int? Expected, int? Actual)>();
            while (await reader.ReadAsync())
            {
                reader.GetBoolean(1).Should().BeTrue("the per-feature transformed EWKB must be identical");
                rows.Add((reader.GetInt64(0), reader.IsDBNull(2) ? null : reader.GetInt32(2),
                    reader.IsDBNull(3) ? null : reader.GetInt32(3)));
            }
            rows.Count.Should().Be(Math.Min(cap + 1, 614));
            rows.Select(row => row.Id).Should().OnlyHaveUniqueItems().And.NotContain(1).And.NotContain(16);
            rows.Where(row => row.Expected is null).Select(row => row.Id)
                .Should().Equal(rows.Where(row => row.Actual is null).Select(row => row.Id));
            var expectedPartitions = rows.Where(row => row.Expected is not null).GroupBy(row => row.Expected)
                .Select(group => string.Join(',', group.Select(row => row.Id).Order())).Order();
            var actualPartitions = rows.Where(row => row.Actual is not null).GroupBy(row => row.Actual)
                .Select(group => string.Join(',', group.Select(row => row.Id).Order())).Order();
            actualPartitions.Should().Equal(expectedPartitions);
            output.WriteLine($"Compared {rows.Count} identities, exact projected EWKB, noise identities and clustering partitions.");
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }
}
