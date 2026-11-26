// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Data.Common;
using System.Net;
using FluentAssertions;
using Honua.Protocols.GeoServices.ImageServer;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.ImageServer;

// This fixture mutates fixed honua raster rows and holds the seed lock through real HTTP renders.
// Do not run parallel fixture initialization while those multi-request assertions own that lock.
[Collection("Database")]
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
                            dimensions.CommandText = format == "png"
                                ? "SELECT ST_Width(r), ST_Height(r), ST_NumBands(r), ST_BandNoDataValue(r, 1), ST_BandNoDataValue(r, 2) FROM (SELECT ST_FromGDALRaster(@data) r) decoded;"
                                : "SELECT ST_Width(r), ST_Height(r), ST_NumBands(r), ST_BandNoDataValue(r, 1) FROM (SELECT ST_FromGDALRaster(@data) r) decoded;";
                            dimensions.Parameters.AddWithValue("data", data);
                            await using var reader = await dimensions.ExecuteReaderAsync();
                            (await reader.ReadAsync()).Should().BeTrue();
                            reader.GetInt32(0).Should().Be(256);
                            reader.GetInt32(1).Should().Be(256);
                            if (format == "png")
                            {
                                reader.GetInt32(2).Should().Be(2, "numeric PNG carries grayscale and independent validity alpha");
                                reader.IsDBNull(3).Should().BeTrue("the grayscale display band has no NoData sentinel");
                                reader.IsDBNull(4).Should().BeTrue("alpha, not a NoData sentinel, encodes transparency");
                            }
                            else
                            {
                                reader.GetInt32(2).Should().Be(1);
                                reader.GetDouble(3).Should().Be(255);
                            }
                        }

                        var outsidePixel = row == rows[0] ? 1 : 256;
                        var samples = new List<PixelSample>();
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
                                samples.Add(new PixelSample(pixelCol, pixelRow, sourceCol, sourceRow));
                            }
                        }

                        await using var pixels = connection.CreateCommand();
                        pixels.CommandText = PixelQuery(format);
                        pixels.Parameters.AddWithValue("data", data);
                        pixels.Parameters.AddWithValue("columns", samples.Select(sample => sample.Column).Append(outsidePixel).ToArray());
                        pixels.Parameters.AddWithValue("rows", samples.Select(sample => sample.Row).Append(outsidePixel).ToArray());
                        await using var pixelReader = await pixels.ExecuteReaderAsync();
                        foreach (var sample in samples)
                        {
                            (await pixelReader.ReadAsync()).Should().BeTrue();
                            AssertSourcePixel(pixelReader, sample, format);
                            checkedCells++;
                        }

                        // The final sample is in the tile padding, outside the fixture footprint.
                        (await pixelReader.ReadAsync()).Should().BeTrue();
                        AssertPaddingPixel(pixelReader, format);
                        (await pixelReader.ReadAsync()).Should().BeFalse();
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

    private readonly record struct PixelSample(int Column, int Row, int SourceColumn, int SourceRow);

    private static string PixelQuery(string format)
    {
        var bands = format == "png"
            ? "ST_Value(r, 1, pixel_col, pixel_row), ST_Value(r, 2, pixel_col, pixel_row)"
            : "ST_Value(r, 1, pixel_col, pixel_row)";
        return $"""
            WITH decoded AS MATERIALIZED (SELECT ST_FromGDALRaster(@data) r)
            SELECT {bands}
            FROM decoded
            CROSS JOIN unnest(@columns, @rows) WITH ORDINALITY AS samples(pixel_col, pixel_row, sample_index)
            ORDER BY sample_index;
            """;
    }

    private static void AssertSourcePixel(DbDataReader reader, PixelSample sample, string format)
    {
        var expected = SourceValues[sample.SourceRow, sample.SourceColumn];
        var cell = $"source cell r{sample.SourceRow}c{sample.SourceColumn}";
        if (format == "png")
        {
            reader.GetDouble(0).Should().Be(expected ?? 0, $"{cell} must retain its grayscale value, including valid zero");
            reader.GetDouble(1).Should().Be(expected.HasValue ? 255 : 0, $"{cell} must retain its independent validity alpha");
        }
        else if (expected is { } value)
        {
            reader.GetDouble(0).Should().Be(value, $"{cell} must survive reprojection, including valid zero");
        }
        else
        {
            reader.IsDBNull(0).Should().BeTrue("the source NoData cell must remain NoData");
        }
    }

    private static void AssertPaddingPixel(DbDataReader reader, string format)
    {
        if (format == "png")
        {
            reader.GetDouble(0).Should().Be(0, "uncovered grayscale padding has no source data");
            reader.GetDouble(1).Should().Be(0, "uncovered padding must be transparent");
            return;
        }

        reader.IsDBNull(0).Should().BeTrue();
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
