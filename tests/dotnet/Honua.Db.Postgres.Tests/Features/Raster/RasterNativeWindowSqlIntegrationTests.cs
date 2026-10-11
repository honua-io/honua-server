// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Db.Postgres.Features.Raster;
using Honua.TestKit;
using Honua.TestKit.Attributes;

namespace Honua.Db.Postgres.Tests.Features.Raster;

/// <summary>
/// Verifies guarded native-window warps, source coverage and original typed masks.
/// </summary>
[Collection("Database")]
public sealed class RasterNativeWindowSqlIntegrationTests(PostgresFixture fixture)
{
    [IntegrationTheory]
    [InlineData(81, 195, -123.02, 39.20, -9.0, 700.0)]
    [InlineData(80, 196, -123.5, 38.52, 12.0, 1000.0)]
    public async Task TryWarp_AnisotropicSource_PreservesOriginalEdgeValues(
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
                SELECT {RasterNativeWindowSql.TryWarp("s.rast", "g.rast")} AS rast,
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
    public async Task TryWarp_Identity_PreservesTypesZeroNaNAndDeclaredMasks(bool declaredNoData)
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
                SELECT {RasterNativeWindowSql.TryWarp("s.rast", "g.rast")} AS rast FROM source s, grid g
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
    public async Task TryWarp_MosaicCoverage_DoesNotOccludeOtherSourcesWithUndeclaredZeroPadding()
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
                SELECT s.id, {RasterNativeWindowSql.TryWarp("s.rast", "g.rast", preserveCoverageMask: true)} AS rast
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
    public async Task TryWarp_UnsupportedDomainOrAllocation_ReturnsNoCorrectedRaster(
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
            SELECT {RasterNativeWindowSql.TryWarp("s.rast", "g.rast")} IS NULL FROM source s, grid g;
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
    [InlineData(0.00001, -1.0)]
    [InlineData(1.0, -0.00001)]
    public async Task TryWarp_ProjectedAxisExceedsRasterLimit_ReturnsNullBeforeWarp(double scaleX, double scaleY)
    {
        await using var connection = await fixture.GetConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            WITH source AS (
                SELECT ST_AddBand(ST_MakeEmptyRaster(2, 1, 0, 1, 1, -1, 0, 0, 4326),
                    '8BUI'::text, 7, 255) AS rast
            ), grid AS (
                SELECT ST_MakeEmptyRaster(1, 1, 0, 1, @sx, @sy, 0, 0, 4326) AS rast
            )
            SELECT {RasterNativeWindowSql.TryWarp("s.rast", "g.rast")} IS NULL FROM source s, grid g;
            """;
        command.Parameters.AddWithValue("sx", scaleX);
        command.Parameters.AddWithValue("sy", scaleY);
        (await command.ExecuteScalarAsync()).Should().Be(true,
            "an intermediate with a legal area but over-wide axis must be rejected before ST_Transform");
    }
}
