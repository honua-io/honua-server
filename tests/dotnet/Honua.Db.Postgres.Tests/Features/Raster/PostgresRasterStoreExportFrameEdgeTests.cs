// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Raster.Domain;
using Honua.Db.Postgres.Features.Raster;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using NetTopologySuite.Geometries;

namespace Honua.Db.Postgres.Tests.Features.Raster;

/// <summary>Checks projected export frames against independent point samples of a synthetic raster.</summary>
[Collection("Database")]
public sealed class PostgresRasterStoreExportFrameEdgeTests(PostgresFixture fixture)
{
    [IntegrationTest]
    public async Task NearestProjectedFrame_PreservesEveryPixelIncludingZeroAndRightEdge()
    {
        var schema = await fixture.CreateIsolatedSchemaAsync(nameof(PostgresRasterStoreExportFrameEdgeTests));
        try
        {
            await using var connection = await fixture.GetConnectionAsync(schema);
            await using var command = connection.CreateCommand();
            var clip = new RasterClipRegion
            {
                Geometry = new GeometryFactory().ToGeometry(new Envelope(-10.5, -10.28, 47.72, 47.84)).AsBinary(),
                Srid = 4326
            };
            // Distinct column/row bands detect a displaced warp as well as a missing edge.
            // Zero is valid data; the independent oracle uses point sampling, not the renderer SQL.
            command.CommandText = """
                WITH source_grid AS (
                    SELECT ST_MakeEmptyRaster(64, 64, -10.5, 47.84,
                        0.22 / 64, -0.12 / 64, 0, 0, 4326) AS rast
                ), source_values AS (
                    SELECT ARRAY(SELECT ARRAY(SELECT x::double precision
                        FROM generate_series(0, 63) x) FROM generate_series(0, 63) y) AS columns,
                        ARRAY(SELECT ARRAY(SELECT (y + 64)::double precision
                        FROM generate_series(0, 63) x) FROM generate_series(0, 63) y) AS rows
                ), frame_source AS (
                    SELECT ST_SetValues(ST_SetValues(
                        ST_AddBand(ST_AddBand(ST_AddBand(g.rast,
                            '8BUI'::text, 0, 255), '8BUI'::text, 0, 255), '8BUI'::text, 141, 255),
                        1, 1, 1, v.columns), 2, 1, 1, v.rows) AS rast
                    FROM source_grid g, source_values v
                )
                """ + PostgresRasterStore.BuildClipExtentFrameCtes("frame_source", clip, 3857, "NearestNeighbor") + """
                , samples AS (
                    SELECT x, y, band,
                        ST_Value(t.rast, band, x, y, FALSE) AS actual,
                        ST_Value(s.rast, band, ST_Transform(ST_SetSRID(ST_MakePoint(
                            ST_UpperLeftX(g.rast) + (x - 0.5) * ST_ScaleX(g.rast),
                            ST_UpperLeftY(g.rast) + (y - 0.5) * ST_ScaleY(g.rast)), 3857), 4326),
                            FALSE) AS expected
                    FROM transformed t, frame_source s, frame_grid g,
                        generate_series(1, 64) x, generate_series(1, 64) y,
                        generate_series(1, 3) band
                )
                SELECT count(*), bool_and(actual IS NOT DISTINCT FROM expected),
                    count(*) FILTER (WHERE x = 64 AND actual IS NOT NULL AND actual <> 255),
                    count(*) FILTER (WHERE band = 1 AND x = 1 AND actual = 0)
                FROM samples;
                """;
            command.Parameters.AddWithValue("clipGeom", clip.Geometry);
            command.Parameters.AddWithValue("clipSrid", 4326);
            command.Parameters.AddWithValue("outputSrid", 3857);
            command.Parameters.AddWithValue("outputWidth", 64);
            command.Parameters.AddWithValue("outputHeight", 64);
            await using var reader = await command.ExecuteReaderAsync();
            (await reader.ReadAsync()).Should().BeTrue();
            reader.GetInt64(0).Should().Be(64 * 64 * 3);
            reader.GetBoolean(1).Should().BeTrue("every projected cell must match its independent source point");
            reader.GetInt64(2).Should().Be(64 * 3, "the entire right edge remains covered");
            reader.GetInt64(3).Should().Be(64, "valid zero must not become NoData");
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }
}
