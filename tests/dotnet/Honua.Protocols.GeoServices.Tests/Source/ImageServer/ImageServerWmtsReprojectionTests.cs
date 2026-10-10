// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using FluentAssertions;
using Honua.Protocols.GeoServices.ImageServer;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.ImageServer;

/// <summary>
/// Real WMTS tiles must retain asymmetric source cells when their requested grid is finer than
/// the source. Projecting first onto an automatically sized intermediate grid loses columns.
/// </summary>
[Collection("Database.GeoServicesRaster")]
[Protocol(TestProtocols.ImageServer)]
public sealed class ImageServerWmtsReprojectionTests
{
    private const double MercatorLimit = 20037508.342789244;
    private const double TileSpan = 2 * MercatorLimit / 128;
    private static readonly int[] MercatorRows = [48, 49];
    private static readonly int[] MercatorColumns = [19, 20];
    private static readonly int[] GeographicRows = [35, 36];
    private static readonly int[] GeographicColumns = [39, 40];
    private static readonly double?[,] SourceValues =
    {
        { 17, 0, 33, 8 },
        { 101, 2, null, 9 },
        { 10, 11, 12, 13 }
    };

    [IntegrationTheory]
    [InlineData(false, "tiff", false)]
    [InlineData(true, "tiff", false)]
    [InlineData(false, "png", false)]
    [InlineData(true, "png", false)]
    [InlineData(false, "tiff", true)]
    [InlineData(true, "tiff", true)]
    [InlineData(false, "png", true)]
    [InlineData(true, "png", true)]
    [Operation(Operations.GetTile)]
    [Endpoint("GET /rest/services/{id}/ImageServer/WMTS")]
    public async Task Wmts_GetTile_ReprojectsSingleAndMosaicSourcesWithoutLosingCells(bool mosaic, string format, bool geographic)
    {
        var fixture = new WebAppFixture().ConfigureServices(services =>
            services.Configure<ImageServerTileMatrixSetOptions>(options => options.Enabled.Add("WorldCRS84Quad")));
        await fixture.InitializeAsync();
        try
        {
            await fixture.Postgres.RunUnderSchemaMutationLockAsync(async () =>
            {
                await SeedAsync(fixture, mosaic, geographic);
                var grid = SourceGrid(geographic);
                var rows = geographic ? GeographicRows : MercatorRows;
                var cols = geographic ? GeographicColumns : MercatorColumns;
                var span = geographic ? 180.0 / 128 : TileSpan;
                var originX = geographic ? -180 : -MercatorLimit;
                var originY = geographic ? 90 : MercatorLimit;
                var matrixSet = geographic ? "WorldCRS84Quad" : "WebMercatorQuad";
                var checkedCells = 0;
                foreach (var row in rows)
                {
                    foreach (var col in cols)
                    {
                        using var response = await fixture.Client.GetAsync(
                            $"/rest/services/{WebAppFixture.TestLayerId}/ImageServer/WMTS?SERVICE=WMTS&REQUEST=GetTile&VERSION=1.0.0&LAYER={WebAppFixture.TestLayerId}&STYLE=default&FORMAT=image/{format}&TILEMATRIXSET={matrixSet}&TILEMATRIX=7&TILEROW={row}&TILECOL={col}");
                        response.StatusCode.Should().Be(HttpStatusCode.OK);
                        response.Content.Headers.ContentType?.MediaType.Should().Be($"image/{format}",
                            "a HTTP 200 JSON error is not a valid tile");
                        var data = await response.Content.ReadAsByteArrayAsync();
                        await using var connection = await fixture.Postgres.GetConnectionAsync(fixture.CurrentSchema!);
                        await using (var dimensions = connection.CreateCommand())
                        {
                            dimensions.CommandText = "SELECT ST_Width(r), ST_Height(r), ST_BandNoDataValue(r, 1) FROM (SELECT ST_FromGDALRaster(@data) r) decoded;";
                            dimensions.Parameters.AddWithValue("data", data);
                            await using var reader = await dimensions.ExecuteReaderAsync();
                            (await reader.ReadAsync()).Should().BeTrue();
                            reader.GetInt32(0).Should().Be(256);
                            reader.GetInt32(1).Should().Be(256);
                            reader.GetDouble(2).Should().Be(255);
                        }

                        for (var sourceRow = 0; sourceRow < 3; sourceRow++)
                        {
                            for (var sourceCol = 0; sourceCol < 4; sourceCol++)
                            {
                                var sourceX = grid.X + (sourceCol + 0.5) * grid.ScaleX;
                                var sourceY = grid.Y + (sourceRow + 0.5) * grid.ScaleY;
                                var x = geographic ? sourceX * 180 / MercatorLimit : sourceX * MercatorLimit / 180;
                                var y = geographic ? Math.Atan(Math.Sinh(sourceY * Math.PI / MercatorLimit)) * 180 / Math.PI : ProjectLatitude(sourceY);
                                if ((int)Math.Floor((x - originX) / span) != col ||
                                    (int)Math.Floor((originY - y) / span) != row)
                                {
                                    continue;
                                }

                                var pixelCol = 1 + (int)Math.Floor((x - (originX + col * span)) / (span / 256));
                                var pixelRow = 1 + (int)Math.Floor(((originY - row * span) - y) / (span / 256));
                                await using var pixel = connection.CreateCommand();
                                pixel.CommandText = "SELECT ST_Value(ST_FromGDALRaster(@data), 1, @col, @row);";
                                pixel.Parameters.AddWithValue("data", data);
                                pixel.Parameters.AddWithValue("col", pixelCol);
                                pixel.Parameters.AddWithValue("row", pixelRow);
                                var actual = await pixel.ExecuteScalarAsync();
                                var expected = SourceValues[sourceRow, sourceCol];
                                if (expected is { } value)
                                {
                                    actual.Should().Be(value, $"source cell r{sourceRow}c{sourceCol} must survive reprojection, including valid zero");
                                }
                                else
                                {
                                    actual.Should().Be(DBNull.Value, "the source NoData cell must remain NoData");
                                }
                                checkedCells++;
                            }
                        }

                        // Northern tiles' top-left and southern tiles' bottom-right are outside
                        // the fixture footprint. Padding must not stretch a source cell over them.
                        var outsidePixel = row == rows[0] ? 1 : 256;
                        await using var outside = connection.CreateCommand();
                        outside.CommandText = "SELECT ST_Value(ST_FromGDALRaster(@data), 1, @pixel, @pixel);";
                        outside.Parameters.AddWithValue("data", data);
                        outside.Parameters.AddWithValue("pixel", outsidePixel);
                        (await outside.ExecuteScalarAsync()).Should().Be(DBNull.Value);
                    }
                }
                checkedCells.Should().Be(12, "all eleven valid cells and the NoData cell must be checked");
            });
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    private static double ProjectLatitude(double latitude)
        => Math.Log(Math.Tan(Math.PI / 4 + latitude * Math.PI / 360)) * MercatorLimit / Math.PI;

    private static (double X, double Y, double ScaleX, double ScaleY, int Srid) SourceGrid(bool geographic)
        => geographic
            ? (-124 * MercatorLimit / 180, ProjectLatitude(40), 0.25 * MercatorLimit / 180, (ProjectLatitude(38.5) - ProjectLatitude(40)) / 3, 3857)
            : (-124, 40, 0.25, -0.5, 4326);

    private static async Task SeedAsync(WebAppFixture fixture, bool mosaic, bool geographic)
    {
        await using var connection = await fixture.Postgres.GetConnectionAsync(fixture.CurrentSchema!);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM honua.raster_data WHERE layer_id = @layerId;
            INSERT INTO honua.raster_data (layer_id, name, raster, acquisition_date, created_at)
            SELECT @layerId, 'wmts-asymmetric-' || n,
                   ST_SetValues(ST_AddBand(ST_MakeEmptyRaster(4, 3, @x, @y, @scalex, @scaley, 0, 0, @srid),
                                          '8BUI'::text, 0, 255),
                                1, 1, 1, ARRAY[[17,0,33,8],[101,2,255,9],[10,11,12,13]]::double precision[][]),
                   now(), now()
            FROM generate_series(1, @count) n;
            """;
        command.Parameters.AddWithValue("layerId", WebAppFixture.TestLayerId);
        command.Parameters.AddWithValue("count", mosaic ? 2 : 1);
        var grid = SourceGrid(geographic);
        command.Parameters.AddWithValue("x", grid.X);
        command.Parameters.AddWithValue("y", grid.Y);
        command.Parameters.AddWithValue("scalex", grid.ScaleX);
        command.Parameters.AddWithValue("scaley", grid.ScaleY);
        command.Parameters.AddWithValue("srid", grid.Srid);
        await command.ExecuteNonQueryAsync();

        // Read the persisted seed independently before asserting the HTTP response.
        command.Parameters.Clear();
        command.CommandText = "SELECT count(*), bool_and(ST_DumpValues(raster, 1, false) = ARRAY[[17,0,33,8],[101,2,255,9],[10,11,12,13]]::double precision[][]) FROM honua.raster_data WHERE layer_id = @layerId;";
        command.Parameters.AddWithValue("layerId", WebAppFixture.TestLayerId);
        await using var reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue();
        reader.GetInt64(0).Should().Be(mosaic ? 2 : 1,
            "two coextensive rasters select the real mosaic path for every requested tile");
        reader.GetBoolean(1).Should().BeTrue();
    }
}
