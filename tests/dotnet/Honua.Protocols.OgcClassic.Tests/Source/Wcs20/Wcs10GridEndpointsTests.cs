// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Npgsql;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Classic.Wcs20;

/// <summary>Checks the actual WCS GeoTIFF grid and samples through the real PostGIS raster store.</summary>
[Collection("Database")]
[Protocol(TestProtocols.Wcs10)]
public sealed class Wcs10GridEndpointsTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new();

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        await using var connection = new NpgsqlConnection(_fixture.DatabaseConnectionProvider.GetConnectionString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM raster_data WHERE layer_id = 0;
            INSERT INTO raster_data (layer_id, name, raster, acquisition_date, created_at)
            SELECT 0, 'wcs-grid-regression',
                ST_SetValues(ST_SetValues(
                    ST_AddBand(ST_AddBand(ST_MakeEmptyRaster(4, 3, -124, 40, 0.25, -0.5, 0, 0, 4326),
                        '32BF'::text, -9999, -9999), '32BF'::text, -9999, -9999),
                    1, 1, 1, ARRAY[[1,2,3,4],[5,6,7,8],[9,10,11,12]]::double precision[][]),
                    2, 1, 1, ARRAY[[101,102,103,104],[105,106,107,108],[109,110,111,112]]::double precision[][]),
                NOW(), NOW();
            """;
        await command.ExecuteNonQueryAsync();
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTheory]
    [InlineData("-124,38.5,-123,40", 4, 3, -124, 40, 0.25, -0.5, 4, 1)]
    [InlineData("-125,37.5,-122,41", 12, 7, -125, 41, 0.25, -0.5, 8, 3)]
    [InlineData("-125,37.5,-122,41", 1000, 999, -125, 41, 0.003, -3.5 / 999, 626, 357)]
    [InlineData("-123.126,39.749,-123.124,39.751", 1, 1, -123.126, 39.751, 0.002, -0.002, 1, 1)]
    [Operation(Operations.Render)]
    [InterfaceOperation(TestProtocols.Wcs10, "GetCoverage")]
    [Endpoint("GET /ogc/services/{serviceId}/wcs")]
    public async Task GetCoverage_PreservesRequestedGridAndCellValues(
        string bbox, int width, int height, double x, double y, double scaleX, double scaleY,
        int sampleColumn, int sampleRow)
    {
        using var response = await _fixture.Client.GetAsync(
            $"/ogc/services/{WebAppFixture.TestServiceId}/wcs?SERVICE=WCS&VERSION=1.0.0&REQUEST=GetCoverage" +
            $"&COVERAGE=coverage_0&FORMAT=GeoTIFF&BBOX={bbox}&CRS=EPSG:4326&WIDTH={width}&HEIGHT={height}");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("image/tiff");
        var bytes = await response.Content.ReadAsByteArrayAsync();

        // Decode the returned file independently of RasterResult's metadata.
        await using var connection = new NpgsqlConnection(_fixture.DatabaseConnectionProvider.GetConnectionString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            WITH decoded AS (SELECT ST_FromGDALRaster(@bytes) AS rast)
            SELECT ST_Width(rast), ST_Height(rast), ST_UpperLeftX(rast), ST_UpperLeftY(rast),
                ST_ScaleX(rast), ST_ScaleY(rast), ST_SRID(rast), ST_NumBands(rast),
                ST_Value(rast, 1, @col, @row, FALSE), ST_Value(rast, 2, @col, @row, FALSE),
                ST_BandNoDataValue(rast, 1), ST_BandNoDataValue(rast, 2),
                ST_Value(rast, 1, 1, 1, FALSE), ST_Value(rast, 2, 1, 1, FALSE)
            FROM decoded;
            """;
        command.Parameters.AddWithValue("bytes", bytes);
        command.Parameters.AddWithValue("col", sampleColumn);
        command.Parameters.AddWithValue("row", sampleRow);
        await using var reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue();
        reader.GetInt32(0).Should().Be(width);
        reader.GetInt32(1).Should().Be(height);
        reader.GetDouble(2).Should().BeApproximately(x, 1e-9);
        reader.GetDouble(3).Should().BeApproximately(y, 1e-9);
        reader.GetDouble(4).Should().BeApproximately(scaleX, 1e-9);
        reader.GetDouble(5).Should().BeApproximately(scaleY, 1e-9);
        reader.GetInt32(6).Should().Be(4326);
        reader.GetInt32(7).Should().Be(2);
        reader.GetDouble(8).Should().Be(4);
        reader.GetDouble(9).Should().Be(104);
        reader.GetDouble(10).Should().Be(-9999);
        reader.GetDouble(11).Should().Be(-9999);
        if (width > 4)
        {
            reader.GetDouble(12).Should().Be(-9999);
            reader.GetDouble(13).Should().Be(-9999);
        }
    }
}
