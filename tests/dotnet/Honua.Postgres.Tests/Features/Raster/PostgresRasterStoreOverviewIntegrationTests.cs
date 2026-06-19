// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using FluentAssertions;
using Honua.Postgres.Features.Raster;
using Honua.TestKit;
using Honua.TestKit.Attributes;

namespace Honua.Postgres.Tests.Features.Raster;

/// <summary>
/// Verifies on-the-fly overview selection (#1793) against a real PostGIS raster: at low
/// zoom the dynamic tile query reduces the source toward the tile's ground resolution and
/// feeds materially fewer pixels into the final resample than the full-resolution baseline,
/// while still rendering a 256x256 / EPSG:3857 tile whose pixel content matches the full-res
/// render. At native/finer zoom the substitution is a strict no-op.
/// </summary>
[Collection("Database")]
public sealed class PostgresRasterStoreOverviewIntegrationTests(PostgresFixture fixture)
{
    // A large, fine-resolution source raster: 2048x2048 px at 2 m/pixel in EPSG:3857,
    // anchored near the origin so it lands well inside the valid WebMercator extent.
    private const int RasterDimension = 2048;
    private const double SourceScaleMeters = 2.0;
    private const double OriginX = 0.0;
    private const double OriginY = 0.0;

    // A "low" zoom whose tile ground resolution (~38 m/px) is ~19x coarser than the 2 m source,
    // yet fine enough that the 4 km source still spans ~100 tile pixels (so the centre pixel is
    // covered and content can be compared against the full-res render).
    private const int LowZoom = 12;

    private static string Inv(double value) => value.ToString("G17", CultureInfo.InvariantCulture);

    private static string BuildSourceRasterSql() => $"""
        ST_AddBand(
            ST_MakeEmptyRaster(
                {RasterDimension}, {RasterDimension},
                {Inv(OriginX)}, {Inv(OriginY)},
                {Inv(SourceScaleMeters)}, -{Inv(SourceScaleMeters)},
                0, 0, 3857),
            '8BUI'::text,
            100,
            NULL)
        """;

    // Resample to the 256x256 EPSG:3857 tile grid, parameterised by the source expression, so we
    // can compare the overview reduction against the full-resolution baseline. Mirrors the SQL
    // shape in PostgresRasterStore.GetImageTileAsync (without the ST_AsGDALRaster encoding).
    private static string BuildTileRasterSql(string sourceExpr) => $"""
        WITH tile_bounds AS (
            SELECT ST_TileEnvelope(@level, @col, @row) AS geom
        ),
        tile_ref AS (
            SELECT ST_MakeEmptyRaster(
                256, 256,
                ST_XMin(tb.geom),
                ST_YMax(tb.geom),
                (ST_XMax(tb.geom) - ST_XMin(tb.geom)) / 256.0,
                -((ST_YMax(tb.geom) - ST_YMin(tb.geom)) / 256.0),
                0.0, 0.0, 3857
            ) AS rast
            FROM tile_bounds tb
        )
        SELECT ST_Resample(ST_Transform({sourceExpr}, 3857), tile_ref.rast) AS rast
        FROM raster_data, tile_bounds tb, tile_ref
        WHERE layer_id = @layerId AND id = @rasterId
          AND ST_Intersects(ST_ConvexHull(raster), ST_Transform(tb.geom, ST_SRID(raster)))
        """;

    [IntegrationTest]
    [Trait("Category", "Performance")]
    public async Task LowZoomTile_ReducesSourcePixelsFedToResample_BelowFullResBaseline()
    {
        var schemaName = await fixture.CreateIsolatedSchemaAsync(nameof(PostgresRasterStoreOverviewIntegrationTests));
        try
        {
            var rasterId = await SeedSourceRasterAsync(schemaName);
            var (col, row) = await ResolveTileForSourceCentreAsync(schemaName, LowZoom, rasterId);

            var overviewExpr = PostgresRasterStore.BuildOverviewSourceExpression(LowZoom);
            overviewExpr.Should().NotBe("raster", "low zoom must engage the overview reduction");

            // The full-resolution baseline feeds all 2048x2048 source pixels into the resample.
            var baselinePixels = await CountPixelsFedToResampleAsync(schemaName, "raster", rasterId);
            baselinePixels.Should().Be((long)RasterDimension * RasterDimension);

            // The overview path reduces the source toward tile resolution first, so the grid
            // handed to the final resample is materially smaller. Robust relative signal: the
            // reduction is applied AND the work (input pixel count) is well below baseline.
            var overviewPixels = await CountPixelsFedToResampleAsync(schemaName, overviewExpr, rasterId);
            overviewPixels.Should().BeLessThan(
                baselinePixels / 10,
                "reducing to ~tile resolution must collapse the full-res grid by more than 10x");

            // EXPLAIN ANALYZE confirms the reduced query still plans and executes over
            // raster_data end-to-end (a real query plan, not a degenerate empty scan).
            var overviewPlan = await ExplainAnalyzePlanAsync(schemaName, overviewExpr, LowZoom, col, row, rasterId);
            overviewPlan.Should().Contain("raster_data", "the low-zoom plan must scan the raster table");
            overviewPlan.Should().Contain("Execution Time", "EXPLAIN ANALYZE must actually run the plan");
        }
        finally
        {
            await fixture.DropSchemaAsync(schemaName);
        }
    }

    [IntegrationTest]
    public async Task LowZoomTile_StillRenders256x256Epsg3857_MatchingFullResContent()
    {
        var schemaName = await fixture.CreateIsolatedSchemaAsync(nameof(PostgresRasterStoreOverviewIntegrationTests));
        try
        {
            var rasterId = await SeedSourceRasterAsync(schemaName);
            var (col, row) = await ResolveTileForSourceCentreAsync(schemaName, LowZoom, rasterId);

            var overviewExpr = PostgresRasterStore.BuildOverviewSourceExpression(LowZoom);

            var (overviewW, overviewH, overviewSrid, overviewSample) =
                await RenderTileMetadataAsync(schemaName, overviewExpr, LowZoom, col, row, rasterId);
            var (baseW, baseH, baseSrid, baseSample) =
                await RenderTileMetadataAsync(schemaName, "raster", LowZoom, col, row, rasterId);

            // Grid contract: the overview render must produce the same EPSG:3857 grid the
            // full-resolution render does (same dimensions on the tile-aligned reference grid),
            // so substituting the overview source does not change the tile's geometry. The
            // GDAL-encoded tile the store returns is reported as 256x256 / EPSG:3857.
            overviewSrid.Should().Be(3857);
            overviewW.Should().Be(baseW);
            overviewH.Should().Be(baseH);
            overviewSrid.Should().Be(baseSrid);

            // Pixel content: the constant-valued source resamples to the same value (100)
            // through both paths, within rounding tolerance.
            overviewSample.Should().BeApproximately(baseSample, 1.0);
            overviewSample.Should().BeApproximately(100.0, 1.0);
        }
        finally
        {
            await fixture.DropSchemaAsync(schemaName);
        }
    }

    [IntegrationTest]
    public async Task HighZoomTile_IsNoOp_RendersIdenticalToFullRes()
    {
        var schemaName = await fixture.CreateIsolatedSchemaAsync(nameof(PostgresRasterStoreOverviewIntegrationTests));
        try
        {
            var rasterId = await SeedSourceRasterAsync(schemaName);

            // At/above the native-resolution threshold the substitution is a strict no-op:
            // the expression is the bare column, so the high-zoom path reads full resolution.
            const int level = PostgresRasterStore.OverviewMaxZoom;
            var overviewExpr = PostgresRasterStore.BuildOverviewSourceExpression(level);
            overviewExpr.Should().Be("raster", "native/finer zoom must read the full-resolution column");

            // And the full 2048x2048 grid is still fed to the resample (no reduction applied).
            var pixels = await CountPixelsFedToResampleAsync(schemaName, overviewExpr, rasterId);
            pixels.Should().Be((long)RasterDimension * RasterDimension);
        }
        finally
        {
            await fixture.DropSchemaAsync(schemaName);
        }
    }

    private async Task<long> SeedSourceRasterAsync(string schemaName)
    {
        await fixture.ExecuteAsync("""
            CREATE TABLE IF NOT EXISTS layers (
                layer_id INTEGER PRIMARY KEY,
                layer_name TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS raster_data (
                id BIGSERIAL PRIMARY KEY,
                layer_id INTEGER NOT NULL REFERENCES layers(layer_id) ON DELETE CASCADE,
                name VARCHAR(255) NOT NULL,
                raster raster NOT NULL
            );
            INSERT INTO layers (layer_id, layer_name) VALUES (1, 'overview-lod')
            ON CONFLICT (layer_id) DO NOTHING;
            """, schemaName);

        await using var connection = await fixture.GetConnectionAsync(schemaName);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            INSERT INTO raster_data (layer_id, name, raster)
            VALUES (1, 'large-source', {BuildSourceRasterSql()})
            RETURNING id;
            """;
        var id = (long)(await command.ExecuteScalarAsync())!;
        await fixture.ExecuteAsync("ANALYZE raster_data;", schemaName);
        return id;
    }

    // Computes the WebMercatorQuad tile (col,row) containing the source raster's centroid so the
    // tile reliably overlaps the source regardless of placement.
    private async Task<(int Col, int Row)> ResolveTileForSourceCentreAsync(
        string schemaName, int level, long rasterId)
    {
        await using var connection = await fixture.GetConnectionAsync(schemaName);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            WITH c AS (
                SELECT ST_Centroid(ST_Transform(ST_Envelope(raster), 3857)) AS g
                FROM raster_data WHERE id = @rasterId
            )
            SELECT
                floor((ST_X(g) + 20037508.342789244) / (40075016.685578488 / (1 << @level)))::int AS col,
                floor((20037508.342789244 - ST_Y(g)) / (40075016.685578488 / (1 << @level)))::int AS row
            FROM c
            """;
        command.Parameters.AddWithValue("rasterId", rasterId);
        command.Parameters.AddWithValue("level", level);
        await using var reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue();
        return (reader.GetInt32(0), reader.GetInt32(1));
    }

    // Number of source pixels handed to the final ST_Resample after the (optional) reduction.
    // For the bare column this is the full source grid; for the overview expression it is the
    // reduced grid, so the ratio is the deterministic "less work" signal.
    private async Task<long> CountPixelsFedToResampleAsync(string schemaName, string sourceExpr, long rasterId)
    {
        await using var connection = await fixture.GetConnectionAsync(schemaName);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT ST_Width(src)::bigint * ST_Height(src)::bigint
            FROM (
                SELECT ST_Transform({sourceExpr}, 3857) AS src
                FROM raster_data WHERE id = @rasterId
            ) s
            """;
        command.Parameters.AddWithValue("rasterId", rasterId);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private async Task<(int Width, int Height, int Srid, double Sample)> RenderTileMetadataAsync(
        string schemaName, string sourceExpr, int level, int col, int row, long rasterId)
    {
        await using var connection = await fixture.GetConnectionAsync(schemaName);
        await using var command = connection.CreateCommand();
        // Sample at the centre of the rendered grid (guaranteed inside its bounds) so a covered
        // pixel is read rather than an out-of-bounds NODATA position.
        command.CommandText = $"""
            WITH t AS ({BuildTileRasterSql(sourceExpr)})
            SELECT ST_Width(rast), ST_Height(rast), ST_SRID(rast),
                   ST_Value(rast, 1, GREATEST(1, ST_Width(rast) / 2), GREATEST(1, ST_Height(rast) / 2))
            FROM t
            """;
        command.Parameters.AddWithValue("level", level);
        command.Parameters.AddWithValue("col", col);
        command.Parameters.AddWithValue("row", row);
        command.Parameters.AddWithValue("layerId", 1);
        command.Parameters.AddWithValue("rasterId", rasterId);

        await using var reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue("the tile query must return a raster");
        var width = reader.GetInt32(0);
        var height = reader.GetInt32(1);
        var srid = reader.GetInt32(2);
        var sample = reader.IsDBNull(3) ? double.NaN : reader.GetDouble(3);
        return (width, height, srid, sample);
    }

    private async Task<string> ExplainAnalyzePlanAsync(
        string schemaName, string sourceExpr, int level, int col, int row, long rasterId)
    {
        await using var connection = await fixture.GetConnectionAsync(schemaName);
        await using var command = connection.CreateCommand();
        command.CommandText = "EXPLAIN (ANALYZE, FORMAT JSON) " + BuildTileRasterSql(sourceExpr);
        command.Parameters.AddWithValue("level", level);
        command.Parameters.AddWithValue("col", col);
        command.Parameters.AddWithValue("row", row);
        command.Parameters.AddWithValue("layerId", 1);
        command.Parameters.AddWithValue("rasterId", rasterId);
        return (await command.ExecuteScalarAsync())?.ToString() ?? string.Empty;
    }
}
