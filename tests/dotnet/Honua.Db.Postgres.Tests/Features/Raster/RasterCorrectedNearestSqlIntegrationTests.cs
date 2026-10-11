// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Raster.Domain;
using Honua.Db.Postgres.Features.Raster;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Xunit.Abstractions;

namespace Honua.Db.Postgres.Tests.Features.Raster;

/// <summary>
/// Verifies canonical separable sampling, target centers and original typed coverage masks.
/// </summary>
[Collection("Database")]
public sealed class RasterCorrectedNearestSqlIntegrationTests(PostgresFixture fixture, ITestOutputHelper output)
{
    [IntegrationTheory]
    [InlineData(81, 195, -123.02, 39.20, -9.0, 700.0)]
    [InlineData(80, 196, -123.5, 38.52, 12.0, 1000.0)]
    public async Task TrySample_AnisotropicSource_PreservesOriginalEdgeValues(
        int col, int row, double longitude, double latitude, double band1, double band2)
    {
        await using var connection = await fixture.GetConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandTimeout = 20;
        command.CommandText = $"""
            SET LOCAL statement_timeout = '15s';
            WITH source AS MATERIALIZED (
                SELECT ST_SetValues(ST_SetValues(ST_AddBand(ST_AddBand(
                    ST_MakeEmptyRaster(4, 3, -124, 40, 0.25, -0.5, 0, 0, 4326),
                    '32BF'::text, -9999, -9999), '32BF'::text, -9999, -9999),
                    1, 1, 1, ARRAY[[-17.25, 0, 33.5, 8], [101, 2, -9999, -9], [10, 11, 12, 13]]::double precision[][]),
                    2, 1, 1, ARRAY[[200, 210, 220, -9999], [-4.5, 500, 600, 700], [800, 900, 1000, 1100]]::double precision[][]) AS rast
            ), bounds AS (
                SELECT ST_TileEnvelope(9, @col, @row) AS geom
            ), grid AS MATERIALIZED (
                SELECT ST_MakeEmptyRaster(256, 256, ST_XMin(geom), ST_YMax(geom),
                    (ST_XMax(geom) - ST_XMin(geom)) / 256.0,
                    -(ST_YMax(geom) - ST_YMin(geom)) / 256.0, 0, 0, 3857) AS rast FROM bounds
            ), result AS MATERIALIZED (
                SELECT {RasterCorrectedNearestSql.Sample("s.rast", "g.rast")} AS rast,
                       ST_Transform(s.rast, g.rast, 'NearestNeighbor', 0.0) AS old_rast,
                       g.rast AS grid
                FROM source s, grid g
            ), point AS (
                SELECT ST_Transform(ST_SetSRID(ST_MakePoint(@lon, @lat), 4326), 3857) AS geom
            )
            SELECT ST_Width(rast), ST_Height(rast), ST_SRID(rast),
                   ST_SameAlignment(rast, grid), ST_Equals(ST_ConvexHull(rast), ST_ConvexHull(grid)),
                   ST_NumBands(rast), ST_BandPixelType(rast, 1), ST_BandPixelType(rast, 2),
                   ST_BandNoDataValue(rast, 1), ST_BandNoDataValue(rast, 2),
                   ST_Value(rast, 1, geom), ST_Value(rast, 2, geom),
                   ST_Value(old_rast, 1, geom), ST_Value(old_rast, 2, geom)
            FROM result, point;
            """;
        command.Parameters.AddWithValue("col", col);
        command.Parameters.AddWithValue("row", row);
        command.Parameters.AddWithValue("lon", longitude);
        command.Parameters.AddWithValue("lat", latitude);
        await using var reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue();
        reader.GetInt32(0).Should().Be(256);
        reader.GetInt32(1).Should().Be(256);
        reader.GetInt32(2).Should().Be(3857);
        reader.GetBoolean(3).Should().BeTrue();
        reader.GetBoolean(4).Should().BeTrue();
        reader.GetInt32(5).Should().Be(2);
        reader.GetString(6).Should().Be("32BF");
        reader.GetString(7).Should().Be("32BF");
        reader.GetDouble(8).Should().Be(-9999);
        reader.GetDouble(9).Should().Be(-9999);
        reader.GetDouble(10).Should().Be(band1);
        reader.GetDouble(11).Should().Be(band2);
        reader.IsDBNull(12).Should().BeTrue("the retained original warp clips this source edge");
        reader.IsDBNull(13).Should().BeTrue();
    }

    [IntegrationTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TrySample_Identity_PreservesTypesZeroNaNAndDeclaredMasks(bool declaredNoData)
    {
        await using var connection = await fixture.GetConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            WITH source AS MATERIALIZED (
                SELECT ST_SetValue(ST_SetValue(ST_AddBand(ST_AddBand(ST_AddBand(
                    ST_MakeEmptyRaster(2, 1, 1, 1, 1, -1, 0, 0, 4326),
                    '8BUI'::text, 0, @byteNoData), '16BSI'::text, 0, -9999),
                    '64BF'::text, 0, NULL), 1, 2, 1, 200), 3, 2, 1, 'NaN'::double precision) AS rast
            ), grid AS (
                SELECT ST_MakeEmptyRaster(4, 1, 0, 1, 1, -1, 0, 0, 4326) AS rast
            ), result AS MATERIALIZED (
                SELECT {RasterCorrectedNearestSql.Sample("s.rast", "g.rast")} AS rast FROM source s, grid g
            )
            SELECT ST_NumBands(rast), ST_BandPixelType(rast, 1), ST_BandPixelType(rast, 2),
                   ST_BandPixelType(rast, 3), ST_BandNoDataValue(rast, 1),
                   ST_BandNoDataValue(rast, 2), ST_BandNoDataValue(rast, 3),
                   ST_Value(rast, 1, 2, 1), ST_Value(rast, 1, 3, 1), ST_Value(rast, 1, 1, 1, FALSE),
                   ST_Value(rast, 2, 2, 1), ST_Value(rast, 2, 1, 1),
                   ST_Value(rast, 3, 3, 1), ST_Value(rast, 3, 1, 1)
            FROM result;
            """;
        command.Parameters.AddWithValue("byteNoData", NpgsqlTypes.NpgsqlDbType.Double,
            declaredNoData ? (object)255.0 : DBNull.Value);
        await using var reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue();
        reader.GetInt32(0).Should().Be(3);
        reader.GetString(1).Should().Be("8BUI");
        reader.GetString(2).Should().Be("16BSI");
        reader.GetString(3).Should().Be("64BF");
        reader.IsDBNull(4).Should().Be(!declaredNoData);
        reader.GetDouble(5).Should().Be(-9999);
        reader.IsDBNull(6).Should().BeTrue();
        reader.GetDouble(7).Should().Be(0, "a valid source zero survives temporary coverage masking");
        reader.GetDouble(8).Should().Be(200);
        reader.GetDouble(9).Should().Be(declaredNoData ? 255 : 0);
        reader.GetDouble(10).Should().Be(0);
        reader.IsDBNull(11).Should().BeTrue();
        double.IsNaN(reader.GetDouble(12)).Should().BeTrue();
        reader.GetDouble(13).Should().Be(0);
    }

    [IntegrationTest]
    public async Task TrySample_MosaicCoverage_DoesNotOccludeOtherSourcesWithUndeclaredZeroPadding()
    {
        await using var connection = await fixture.GetConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            WITH sources(id, rast) AS MATERIALIZED (
                VALUES (1, ST_AddBand(ST_MakeEmptyRaster(2, 1, 0, 1, 1, -1, 0, 0, 4326), '8BUI'::text, 7, NULL)),
                       (2, ST_AddBand(ST_MakeEmptyRaster(1, 1, 1, 1, 1, -1, 0, 0, 4326), '8BUI'::text, 0, NULL))
            ), grid AS (
                SELECT ST_MakeEmptyRaster(4, 1, -1, 1, 1, -1, 0, 0, 4326) AS rast
            ), inputs AS MATERIALIZED (
                SELECT s.id, {RasterSeparableNearestSql.TrySample("s.rast", "g.rast", preserveCoverageMask: true)} AS rast
                FROM sources s, grid g
            ), mosaic AS (
                SELECT ST_Union(rast, 'LAST' ORDER BY id) AS rast FROM inputs
            )
            SELECT ST_Value(rast, 1, 2, 1), ST_Value(rast, 1, 3, 1),
                   ST_Value(rast, 1, 1, 1), ST_Value(rast, 1, 4, 1)
            FROM mosaic;
            """;
        await using var reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue();
        reader.GetDouble(0).Should().Be(7, "newer source padding cannot hide the older raster");
        reader.GetDouble(1).Should().Be(0, "a real newer zero must replace the older value");
        reader.IsDBNull(2).Should().BeTrue();
        reader.IsDBNull(3).Should().BeTrue();
    }

    [IntegrationTheory]
    [InlineData(0.25, 4326, 4326, 0.0, 1.0, 1.0)]
    [InlineData(0.0, 32610, 3857, 0.0, 1.0, 1.0)]
    [InlineData(0.0, 4326, 3857, 0.0, 21000000.0, 1.0)]
    [InlineData(0.0, 4326, 4326, 0.0, 1.0, 1e-10)]
    [InlineData(0.0, 4326, 4326, 0.0, 1.0, 1e-200)]
    public async Task TrySample_UnsupportedDomainOrAllocation_ReturnsNoCorrectedRaster(
        double skew, int sourceSrid, int outputSrid, double x, double y, double scale)
    {
        await using var connection = await fixture.GetConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            WITH source AS (
                SELECT ST_AddBand(ST_MakeEmptyRaster(1, 1, 0, 1, @scale, -@scale, @skew, 0, @source),
                    '8BUI'::text, 7, 255) AS rast
            ), grid AS (
                SELECT ST_MakeEmptyRaster(256, 256, @x, @y, 1, -1, 0, 0, @output) AS rast
            )
            SELECT {RasterSeparableNearestSql.TrySample("s.rast", "g.rast")} IS NULL FROM source s, grid g;
            """;
        command.Parameters.AddWithValue("skew", skew);
        command.Parameters.AddWithValue("source", sourceSrid);
        command.Parameters.AddWithValue("output", outputSrid);
        command.Parameters.AddWithValue("x", x);
        command.Parameters.AddWithValue("y", y);
        command.Parameters.AddWithValue("scale", scale);
        (await command.ExecuteScalarAsync()).Should().Be(true);
    }

    [IntegrationTheory]
    [InlineData(0.5, 7.0)]
    [InlineData(0.999999, 7.0)]
    [InlineData(1.0, 11.0)]
    [InlineData(1.000001, 11.0)]
    [InlineData(1.5, 11.0)]
    [InlineData(2.0, 255.0)]
    public async Task TrySample_TargetCenters_UseCanonicalPixelEdgeConvention(double center, double expected)
    {
        await using var connection = await fixture.GetConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            WITH source AS (
                SELECT ST_SetValue(ST_AddBand(ST_MakeEmptyRaster(2, 1, 0, 1, 1, -1, 0, 0, 4326),
                    '8BUI'::text, 7, 255), 1, 2, 1, 11) AS rast
            ), grid AS (
                SELECT ST_MakeEmptyRaster(1, 1, @center - 0.05, 1, 0.1, -1, 0, 0, 4326) AS rast
            ), sampled AS MATERIALIZED (
                SELECT {RasterCorrectedNearestSql.Sample("s.rast", "g.rast")} AS rast FROM source s, grid g
            )
            SELECT ST_Value(rast, 1, 1, 1, FALSE), ST_Width(rast), ST_Height(rast),
                   ST_BandPixelType(rast, 1), ST_BandNoDataValue(rast, 1) FROM sampled;
            """;
        command.Parameters.AddWithValue("center", center);
        await using var reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue();
        reader.GetDouble(0).Should().Be(expected);
        reader.GetInt32(1).Should().Be(1);
        reader.GetInt32(2).Should().Be(1);
        reader.GetString(3).Should().Be("8BUI");
        reader.GetDouble(4).Should().Be(255);
    }

    [IntegrationTheory]
    [InlineData(81, 195)]
    [InlineData(80, 196)]
    public async Task TrySample_CompleteTargetPage_MatchesIndependentLiteralSourceAtEveryCenter(int col, int row)
    {
        await using var connection = await fixture.GetConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandTimeout = 20;
        command.CommandText = $"""
            SET LOCAL statement_timeout = '15s';
            WITH source AS MATERIALIZED (
                SELECT ST_SetValues(ST_SetValues(ST_AddBand(ST_AddBand(
                    ST_MakeEmptyRaster(4, 3, -124, 40, 0.25, -0.5, 0, 0, 4326),
                    '32BF'::text, -9999, -9999), '32BF'::text, -9999, -9999),
                    1, 1, 1, ARRAY[[-17.25, 0, 33.5, 8], [101, 2, -9999, -9], [10, 11, 12, 13]]::double precision[][]),
                    2, 1, 1, ARRAY[[200, 210, 220, -9999], [-4.5, 500, 600, 700], [800, 900, 1000, 1100]]::double precision[][]) AS rast
            ), bounds AS (
                SELECT ST_TileEnvelope(9, @col, @row) AS geom
            ), grid AS MATERIALIZED (
                SELECT ST_MakeEmptyRaster(256, 256, ST_XMin(geom), ST_YMax(geom),
                    (ST_XMax(geom)-ST_XMin(geom))/256.0,
                    -(ST_YMax(geom)-ST_YMin(geom))/256.0, 0, 0, 3857) AS rast FROM bounds
            ), sampled AS MATERIALIZED (
                SELECT {RasterCorrectedNearestSql.Sample("s.rast", "g.rast")} AS rast FROM source s, grid g
            )
            SELECT ST_Width(rast), ST_Height(rast), ST_SRID(rast), ST_NumBands(rast),
                   ST_DumpValues(rast, 1, FALSE), ST_DumpValues(rast, 2, FALSE) FROM sampled;
            """;
        command.Parameters.AddWithValue("col", col);
        command.Parameters.AddWithValue("row", row);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        await using var reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue();
        reader.GetInt32(0).Should().Be(256);
        reader.GetInt32(1).Should().Be(256);
        reader.GetInt32(2).Should().Be(3857);
        reader.GetInt32(3).Should().Be(2);
        double[][,] literalBands =
        [
            new double[,] { { -17.25, 0, 33.5, 8 }, { 101, 2, -9999, -9 }, { 10, 11, 12, 13 } },
            new double[,] { { 200, 210, 220, -9999 }, { -4.5, 500, 600, 700 }, { 800, 900, 1000, 1100 } }
        ];
        const double earthRadius = 6378137.0;
        const double halfWorld = 20037508.342789244;
        var tileSpan = 2 * halfWorld / 512;
        var left = -halfWorld + col * tileSpan;
        var top = halfWorld - row * tileSpan;
        for (var band = 0; band < 2; band++)
        {
            var actual = reader.GetFieldValue<double[,]>(4 + band);
            actual.GetLength(0).Should().Be(256);
            actual.GetLength(1).Should().Be(256);
            for (var y = 0; y < 256; y++)
            {
                var mercatorY = top - (y + 0.5) * tileSpan / 256;
                var latitude = (2 * Math.Atan(Math.Exp(mercatorY / earthRadius)) - Math.PI / 2) * 180 / Math.PI;
                var sourceY = (int)Math.Floor((40 - latitude) / 0.5);
                for (var x = 0; x < 256; x++)
                {
                    var mercatorX = left + (x + 0.5) * tileSpan / 256;
                    var longitude = mercatorX / earthRadius * 180 / Math.PI;
                    var sourceX = (int)Math.Floor((longitude + 124) / 0.25);
                    var expected = sourceX is >= 0 and < 4 && sourceY is >= 0 and < 3
                        ? literalBands[band][sourceY, sourceX] : -9999;
                    actual[y, x].Should().Be(expected, $"literal band {band + 1}, target ({x},{y})");
                }
            }
        }
        watch.Stop();
        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(15),
            "a bounded page must complete within its SQL deadline; this is not a practical latency claim");
    }

    [IntegrationTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Sample_UnsupportedFastPaths_RetainsGeneralLiteralValues(bool skewedIdentity)
    {
        await using var connection = await fixture.GetConnectionAsync();
        await using var command = connection.CreateCommand();
        var sourceGrid = skewedIdentity
            ? "ST_MakeEmptyRaster(2, 2, 0, 2, 1, -1, 0.25, 0.125, 4326)"
            : "ST_MakeEmptyRaster(2, 2, 499000, 1000, 1000, -1000, 0, 0, 32610)";
        // UTM zone 10's central meridian is longitude -123: at the equator its
        // easting/northing is (500000,0). Four target centers lie away from both
        // axes and therefore have these independent west/east, north/south values.
        var outputGrid = skewedIdentity ? sourceGrid
            : "ST_MakeEmptyRaster(2, 2, -13692397.36757265, 100, 100, -100, 0, 0, 3857)";
        command.CommandText = $"""
            WITH source AS MATERIALIZED (
                SELECT ST_SetValues(ST_AddBand({sourceGrid}, '8BUI'::text, 7, 255),
                    1, 1, 1, ARRAY[[7,11],[13,17]]::double precision[][]) AS rast
            ), grid AS MATERIALIZED (SELECT {outputGrid} AS rast),
            result AS MATERIALIZED (
                SELECT {RasterCorrectedNearestSql.Sample("s.rast", "g.rast")} AS rast,
                       {RasterNativeWindowSql.TryWarp("s.rast", "g.rast")} IS NULL AS window_rejected,
                       {RasterSeparableNearestSql.TrySample("s.rast", "g.rast")} IS NULL AS axes_rejected,
                       g.rast AS grid FROM source s, grid g
            )
            SELECT window_rejected, axes_rejected, ST_DumpValues(rast, 1, FALSE),
                   ST_BandPixelType(rast,1), ST_BandNoDataValue(rast,1),
                   ST_SameAlignment(rast,grid), ST_Equals(ST_ConvexHull(rast),ST_ConvexHull(grid)) FROM result;
            """;
        await using var reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue();
        reader.GetBoolean(0).Should().BeTrue();
        reader.GetBoolean(1).Should().BeTrue();
        var values = reader.GetFieldValue<double[,]>(2);
        values[0, 0].Should().Be(7);
        values[0, 1].Should().Be(11);
        values[1, 0].Should().Be(13);
        values[1, 1].Should().Be(17);
        reader.GetString(3).Should().Be("8BUI");
        reader.GetDouble(4).Should().Be(255);
        reader.GetBoolean(5).Should().BeTrue();
        reader.GetBoolean(6).Should().BeTrue();
    }

    [IntegrationTest]
    [Trait("Category", "Performance")]
    public async Task Sample_HighResolutionCappedWindow_MatchesEveryLiteralPixelWithinDeadline()
    {
        await using var connection = await fixture.GetConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        // Fixture construction is not a production tile read. Retain the full 2048-square,
        // two-band source, and report its setup time separately from the bounded Sample call.
        command.CommandTimeout = 120;
        command.CommandText = """
            SET LOCAL statement_timeout = '120s';
            CREATE TEMP TABLE corrected_highres_source (rast raster) ON COMMIT DROP;
            INSERT INTO corrected_highres_source
            SELECT ST_AddBand(
                    ST_MapAlgebra(ST_AddBand(ST_MakeEmptyRaster(2048, 2048, 0, 2048, 1, -1, 0, 0, 3857),
                        '16BUI'::text, 0, 65535), 1, '16BUI', '([rast.x] - 1)::integer % 251', 65535),
                    ST_MapAlgebra(ST_AddBand(ST_MakeEmptyRaster(2048, 2048, 0, 2048, 1, -1, 0, 0, 3857),
                        '16BUI'::text, 0, 65535), 1, '16BUI', '([rast.y] - 1)::integer % 239', 65535));
            """;
        var setupWatch = System.Diagnostics.Stopwatch.StartNew();
        await command.ExecuteNonQueryAsync();
        setupWatch.Stop();
        output.WriteLine($"HighResolution fixture setup: {setupWatch.Elapsed.TotalMilliseconds:F3} ms");
        command.CommandTimeout = 20;
        command.CommandText = $"""
            SET LOCAL statement_timeout = '15s';
            WITH source AS MATERIALIZED (SELECT rast FROM corrected_highres_source),
            grid AS MATERIALIZED (
                SELECT ST_MakeEmptyRaster(256, 256, 0, 2048, 8, -8, 0, 0, 3857) AS rast
            ), result AS MATERIALIZED (
                SELECT {RasterCorrectedNearestSql.Sample("s.rast", "g.rast")} AS rast,
                       {RasterNativeWindowSql.TryWarp("s.rast", "g.rast")} IS NULL AS capped,
                       g.rast AS grid, ST_Width(s.rast) AS source_width,
                       ST_Height(s.rast) AS source_height, ST_NumBands(s.rast) AS source_bands
                FROM source s, grid g
            )
            SELECT capped, ST_DumpValues(rast, 1, FALSE), ST_DumpValues(rast, 2, FALSE),
                   ST_BandPixelType(rast, 1), ST_BandPixelType(rast, 2),
                   ST_BandNoDataValue(rast, 1), ST_BandNoDataValue(rast, 2),
                   ST_SameAlignment(rast, grid), ST_Equals(ST_ConvexHull(rast), ST_ConvexHull(grid)),
                   source_width, source_height, source_bands
            FROM result;
            """;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        await using var reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue();
        reader.GetBoolean(0).Should().BeTrue("the full native window exceeds its band-weighted cap");
        var first = reader.GetFieldValue<double[,]>(1);
        var second = reader.GetFieldValue<double[,]>(2);
        first.GetLength(0).Should().Be(256);
        first.GetLength(1).Should().Be(256);
        second.GetLength(0).Should().Be(256);
        second.GetLength(1).Should().Be(256);
        for (var y = 0; y < 256; y++)
        {
            for (var x = 0; x < 256; x++)
            {
                first[y, x].Should().Be((x * 8 + 4) % 251);
                second[y, x].Should().Be((y * 8 + 4) % 239);
            }
        }

        reader.GetString(3).Should().Be("16BUI");
        reader.GetString(4).Should().Be("16BUI");
        reader.GetDouble(5).Should().Be(65535);
        reader.GetDouble(6).Should().Be(65535);
        reader.GetBoolean(7).Should().BeTrue();
        reader.GetBoolean(8).Should().BeTrue();
        reader.GetInt32(9).Should().Be(2048);
        reader.GetInt32(10).Should().Be(2048);
        reader.GetInt32(11).Should().Be(2);
        watch.Stop();
        output.WriteLine($"HighResolution production Sample plus full array assertions: {watch.Elapsed.TotalMilliseconds:F3} ms");
        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(15),
            "the larger-source fallback must meet the same bounded query deadline");
    }

    [IntegrationTest]
    public async Task Sample_CappedWindow_RetainsSeparableLiteralPixelAndDeclaredOutsideMask()
    {
        await using var connection = await fixture.GetConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            WITH source AS MATERIALIZED (
                SELECT ST_AddBand(ST_MakeEmptyRaster(1,1,0,1,0.01,-0.01,0,0,4326),
                    '8BUI'::text,7,255) AS rast
            ), grid AS MATERIALIZED (
                SELECT ST_MakeEmptyRaster(2,2,-4.995,5.995,10,-10,0,0,4326) AS rast
            ), result AS MATERIALIZED (
                SELECT {RasterCorrectedNearestSql.Sample("s.rast", "g.rast")} AS rast,
                       {RasterNativeWindowSql.TryWarp("s.rast", "g.rast")} IS NULL AS window_rejected,
                       {RasterSeparableNearestSql.TrySample("s.rast", "g.rast")} IS NOT NULL AS axes_admitted,
                       g.rast AS grid FROM source s,grid g
            )
            SELECT window_rejected,axes_admitted,ST_DumpValues(rast,1,FALSE),
                   ST_BandPixelType(rast,1),ST_BandNoDataValue(rast,1),
                   ST_SameAlignment(rast,grid),ST_Equals(ST_ConvexHull(rast),ST_ConvexHull(grid)) FROM result;
            """;
        await using var reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue();
        reader.GetBoolean(0).Should().BeTrue("the native window exceeds its allocation cap");
        reader.GetBoolean(1).Should().BeTrue();
        var values = reader.GetFieldValue<double[,]>(2);
        values[0, 0].Should().Be(7, "the first literal center (0.005,0.995) lies inside the only source cell");
        values[0, 1].Should().Be(255);
        values[1, 0].Should().Be(255);
        values[1, 1].Should().Be(255);
        reader.GetString(3).Should().Be("8BUI");
        reader.GetDouble(4).Should().Be(255);
        reader.GetBoolean(5).Should().BeTrue();
        reader.GetBoolean(6).Should().BeTrue();
    }

    [IntegrationTest]
    public async Task Sample_ValidLowestFiniteDouble_RoutesAroundTemporaryMaskWithoutLosingValue()
    {
        await using var connection = await fixture.GetConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            WITH source AS MATERIALIZED (
                SELECT ST_AddBand(ST_MakeEmptyRaster(1, 1, 0, 1, 1, -1, 0, 0, 4326),
                    '64BF'::text, -1.7976931348623157e308, NULL) AS rast
            ), grid AS MATERIALIZED (
                SELECT ST_MakeEmptyRaster(1, 1, 0, 1, 1, -1, 0, 0, 4326) AS rast
            ), result AS MATERIALIZED (
                SELECT {RasterCorrectedNearestSql.Sample("s.rast", "g.rast")} AS rast,
                    {RasterNativeWindowSql.TryWarp("s.rast", "g.rast")} IS NULL AS native_rejected
                FROM source s, grid g
            )
            SELECT native_rejected, ST_BandPixelType(rast, 1), ST_BandNoDataValue(rast, 1),
                   ST_Value(rast, 1, 1, 1, FALSE) FROM result;
            """;
        await using var reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue();
        reader.GetBoolean(0).Should().BeTrue();
        reader.GetString(1).Should().Be("64BF");
        reader.IsDBNull(2).Should().BeTrue();
        reader.GetDouble(3).Should().Be(double.MinValue);
    }

    [IntegrationTest]
    public async Task Sample_MosaicReservedSentinelCollision_RetainsExplicitDiagnostic()
    {
        await using var connection = await fixture.GetConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            WITH source AS MATERIALIZED (
                SELECT ST_AddBand(ST_MakeEmptyRaster(1, 1, 0, 1, 1, -1, 0, 0, 4326),
                    '64BF'::text, -1.7976931348623157e308, NULL) AS rast
            ), grid AS MATERIALIZED (
                SELECT ST_MakeEmptyRaster(2, 1, 0, 1, 1, -1, 0, 0, 4326) AS rast
            )
            SELECT ST_Value({RasterCorrectedNearestSql.Sample("s.rast", "g.rast", preserveCoverageMask: true)},
                            1, 1, 1, FALSE) FROM source s, grid g;
            """;
        var action = async () => await command.ExecuteScalarAsync();
        var exception = await action.Should().ThrowAsync<Npgsql.PostgresException>();
        exception.Which.SqlState.Should().Be("22P02");
        exception.Which.MessageText.Should().Contain("raster_mosaic_64bf_nodata_collision");
    }

    [IntegrationTheory]
    [InlineData(RasterMergeStrategy.Newest)]
    [InlineData(RasterMergeStrategy.Average)]
    public async Task Sample_MosaicBeforeEncoding_PreservesDistinctBandNoDataAndCanonicalValues(
        RasterMergeStrategy strategy)
    {
        await using var connection = await fixture.GetConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            WITH original AS MATERIALIZED (
                SELECT 1 AS id, '2024-01-01'::timestamptz AS created_at,
                    '2024-01-01'::timestamptz AS effective_acquisition,
                    ST_AddBand(ST_AddBand(ST_MakeEmptyRaster(1, 1, 0, 1, 1, -1, 0, 0, 4326),
                        '32BF'::text, 2.75, -9999), '32BF'::text, 6.5, -9998) AS rast
                UNION ALL
                SELECT 2, '2024-01-02'::timestamptz, '2024-01-02'::timestamptz,
                    ST_AddBand(ST_AddBand(ST_MakeEmptyRaster(1, 1, 0, 1, 1, -1, 0, 0, 4326),
                        '8BUI'::text, 20, 255), '16BUI'::text, 40, 65535)
            ), grid AS MATERIALIZED (
                SELECT ST_MakeEmptyRaster(1, 1, 0, 1, 1, -1, 0, 0, 4326) AS rast
            ), source AS MATERIALIZED (
                SELECT {RasterCorrectedNearestSql.Sample("o.rast", "g.rast", preserveCoverageMask: true)} AS rast,
                    o.rast AS original, id, created_at, effective_acquisition FROM original o, grid g
            ), metadata_source AS (
                SELECT {RasterCorrectedNearestSql.MetadataHeader("original", "(SELECT rast FROM grid)")} AS rast,
                    id, created_at, effective_acquisition FROM source
            ), metadata_raw AS (
                SELECT {RasterMosaicSql.CreateMosaicAggregateExpression(strategy)} AS rast FROM metadata_source
            ), metadata AS (
                SELECT {RasterCorrectedNearestSql.RestoreUndeclaredNoData("rast", "source")} AS rast FROM metadata_raw
            ), merged AS (
                SELECT {RasterMosaicSql.CreateMosaicAggregateExpression(strategy)} AS rast FROM source
            ), actual AS (
                SELECT {RasterCorrectedNearestSql.RestoreAfterMosaic("rast", "(SELECT rast FROM metadata)")} AS rast FROM merged
            ), canonical AS (
                SELECT {RasterMosaicSql.CreateMosaicAggregateExpression(strategy)} AS rast FROM original
            )
            SELECT ST_BandPixelType(a.rast, n), ST_BandPixelType(c.rast, n),
                ST_BandNoDataValue(a.rast, n), ST_BandNoDataValue(c.rast, n),
                ST_Value(a.rast, n, 1, 1), ST_Value(c.rast, n, 1, 1)
            FROM actual a, canonical c, generate_series(1, 2) n ORDER BY n;
            """;
        await using var reader = await command.ExecuteReaderAsync();
        for (var band = 0; band < 2; band++)
        {
            (await reader.ReadAsync()).Should().BeTrue();
            reader.GetString(0).Should().Be(reader.GetString(1));
            reader.GetDouble(2).Should().Be(reader.GetDouble(3));
            reader.GetDouble(2).Should().Be(band == 0 ? -9999 : -9998,
                "raw raster metadata must retain each marker before codec-specific normalization");
            reader.GetDouble(4).Should().Be(reader.GetDouble(5));
            if (strategy == RasterMergeStrategy.Average)
            {
                reader.GetDouble(4).Should().Be(band == 0 ? 11.375 : 23.25);
            }
        }

        (await reader.ReadAsync()).Should().BeFalse();
    }
}
