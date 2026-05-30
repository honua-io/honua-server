// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Raster.Domain;
using Honua.TestKit.Infrastructure;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using SkiaSharp;

namespace Honua.Server.Tests.Features.Protocols.Terrain;

[Collection("Database")]
[Protocol(TestProtocols.Terrain)]
public sealed class TerrainEndpointTests : IAsyncLifetime
{
    private const double WebMercatorExtent = 20037508.342789244;
    private const int UnregisteredSrid = 999999;
    private const string TerrainProtocolName = "Terrain";

    private static readonly string[] AllProtocols =
    [
        "FeatureServer",
        "MapServer",
        "ImageServer",
        "GPServer",
        "OgcFeatures",
        "OGC-API-Maps",
        "OGC-API-Coverages",
        "OGC-API-Tiles",
        "Wfs20",
        "Wms",
        "Wmts",
        "Wcs",
        "OData",
        "Grpc",
        "Stac",
        TerrainProtocolName,
        "Elevation",
    ];

    private readonly WebAppFixture _fixture = new();

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.GetTileMetadata)]
    [Endpoint("GET /terrain/{datasetId}/tile.json")]
    public async Task GetTerrainMetadata_WithRasterFixture_ReturnsTileJsonExtensionsAndCacheHeaders()
    {
        await SeedFullWorldRasterAsync(123.4);

        var response = await _fixture.Client.GetAsync("/terrain/0/tile.json");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");
        response.Headers.CacheControl?.Public.Should().BeTrue();
        response.Headers.CacheControl?.MaxAge.Should().BeGreaterThan(TimeSpan.Zero);

        var content = await response.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(content);
        var root = json.RootElement;

        root.GetProperty("tilejson").GetString().Should().Be("3.0.0");
        root.GetProperty("scheme").GetString().Should().Be("xyz");
        root.GetProperty("format").GetString().Should().Be("terrain-rgb");
        root.GetProperty("tiles")[0].GetString().Should().Contain("/terrain/0/{z}/{x}/{y}.png");
        root.GetProperty("bounds").GetArrayLength().Should().Be(4);
        root.GetProperty("supported").GetBoolean().Should().BeTrue();
        root.GetProperty("unsupportedReasons").GetArrayLength().Should().Be(0);

        var encoding = root.GetProperty("encoding");
        encoding.GetProperty("type").GetString().Should().Be("mapbox-terrain-rgb");
        encoding.GetProperty("tileSize").GetInt32().Should().Be(TerrainRgbEncoding.TileSize);
        encoding.GetProperty("formula").GetString().Should().Contain("R * 256 * 256");

        var source = root.GetProperty("source");
        source.GetProperty("datasetId").GetString().Should().Be("0");
        source.GetProperty("layerId").GetInt32().Should().Be(0);
        source.GetProperty("rasterCount").GetInt32().Should().Be(1);
        source.GetProperty("sourceSrid").GetInt32().Should().Be(3857);
        source.GetProperty("sourceCrs").GetString().Should().Be("EPSG:3857");
        source.GetProperty("bandCount").GetInt32().Should().Be(1);
        source.GetProperty("pixelType").GetString().Should().Be("32BF");
        source.GetProperty("verticalUnit").ValueKind.Should().Be(JsonValueKind.Null);
        source.GetProperty("verticalDatum").ValueKind.Should().Be(JsonValueKind.Null);

        var noData = root.GetProperty("noData");
        noData.GetProperty("terrainRgbSentinelMeters").GetDouble().Should().Be(TerrainRgbEncoding.NoDataSentinelMeters);
        noData.GetProperty("terrainRgbSentinel").EnumerateArray().Select(static value => value.GetInt32())
            .Should().Equal(0, 0, 0);
    }

    [IntegrationTest]
    [Operation(Operations.GetTileMetadata)]
    [Endpoint("GET /terrain/{datasetId}/tile.json")]
    public async Task GetTerrainMetadata_WithCollectionName_ResolvesDataset()
    {
        await SeedFullWorldRasterAsync(12.3);

        var response = await _fixture.Client.GetAsync("/terrain/Test%20Layer/tile.json");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(content);

        var source = json.RootElement.GetProperty("source");
        source.GetProperty("datasetId").GetString().Should().Be("Test Layer");
        source.GetProperty("layerId").GetInt32().Should().Be(WebAppFixture.TestLayerId);
        json.RootElement.GetProperty("tiles")[0].GetString().Should().Contain("/terrain/Test%20Layer/{z}/{x}/{y}.png");
    }

    [IntegrationTest]
    [Operation(Operations.GetTile)]
    [Endpoint("GET /terrain/{datasetId}/{z}/{x}/{y}.png")]
    public async Task GetTerrainTile_WithRegisteredDem_ReturnsTerrainRgbPng()
    {
        await SeedFullWorldRasterAsync(123.4);

        var response = await _fixture.Client.GetAsync("/terrain/0/0/0/0.png");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("image/png");
        response.Headers.CacheControl?.Public.Should().BeTrue();
        response.Headers.CacheControl?.MaxAge.Should().BeGreaterThan(TimeSpan.Zero);

        var bitmap = await DecodeBitmapAsync(response);
        using (bitmap)
        {
            bitmap.Width.Should().Be(TerrainRgbEncoding.TileSize);
            bitmap.Height.Should().Be(TerrainRgbEncoding.TileSize);
            AssertTerrainPixel(bitmap, 128, 128, 123.4);
        }
    }

    [IntegrationTest]
    [Operation(Operations.GetTile)]
    [Endpoint("GET /terrain/{datasetId}/{z}/{x}/{y}.png")]
    public async Task GetTerrainTile_OutsideCoverage_ReturnsAllNoDataPng()
    {
        await RasterIntegrationTestData.ReplaceLayerRastersAsync(
            _fixture,
            WebAppFixture.TestLayerId,
            new RasterSeed(
                Name: "se-quadrant-dem",
                Width: 1,
                Height: 1,
                UpperLeftX: 0,
                UpperLeftY: 0,
                ScaleX: WebMercatorExtent,
                ScaleY: -WebMercatorExtent,
                Value: 50,
                AcquisitionDate: RasterIntegrationTestData.WestAcquisition,
                CreatedAt: RasterIntegrationTestData.WestAcquisition,
                Srid: 3857));

        var response = await _fixture.Client.GetAsync("/terrain/0/1/0/0.png");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var bitmap = await DecodeBitmapAsync(response);
        using (bitmap)
        {
            bitmap.Width.Should().Be(TerrainRgbEncoding.TileSize);
            bitmap.Height.Should().Be(TerrainRgbEncoding.TileSize);
            AssertTerrainPixel(bitmap, 128, 128, TerrainRgbEncoding.NoDataSentinelMeters);
        }
    }

    [IntegrationTest]
    [Operation(Operations.GetTile)]
    [Endpoint("GET /terrain/{datasetId}/{z}/{x}/{y}.png")]
    public async Task GetTerrainTile_WithPartialCoverage_EncodesCoveredAndUncoveredPixels()
    {
        await RasterIntegrationTestData.ReplaceLayerRastersAsync(
            _fixture,
            WebAppFixture.TestLayerId,
            new RasterSeed(
                Name: "east-half-dem",
                Width: 1,
                Height: 1,
                UpperLeftX: 0,
                UpperLeftY: WebMercatorExtent,
                ScaleX: WebMercatorExtent,
                ScaleY: -2 * WebMercatorExtent,
                Value: 50,
                AcquisitionDate: RasterIntegrationTestData.WestAcquisition,
                CreatedAt: RasterIntegrationTestData.WestAcquisition,
                Srid: 3857));

        var response = await _fixture.Client.GetAsync("/terrain/0/0/0/0.png");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var bitmap = await DecodeBitmapAsync(response);
        using (bitmap)
        {
            AssertTerrainPixel(bitmap, 64, 128, TerrainRgbEncoding.NoDataSentinelMeters);
            AssertTerrainPixel(bitmap, 192, 128, 50);
        }
    }

    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("GET /terrain/{datasetId}/tile.json")]
    public async Task GetTerrainMetadata_WithUnknownDataset_ReturnsNotFound()
    {
        await SeedFullWorldRasterAsync(1);

        var response = await _fixture.Client.GetAsync("/terrain/999999/tile.json");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");
    }

    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("GET /terrain/{datasetId}/{z}/{x}/{y}.png")]
    public async Task GetTerrainTile_WithZoomAboveLimit_ReturnsBadRequest()
    {
        await SeedFullWorldRasterAsync(1);

        var response = await _fixture.Client.GetAsync("/terrain/0/23/0/0.png");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");
    }

    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("GET /terrain/{datasetId}/{z}/{x}/{y}.png")]
    public async Task GetTerrainTile_WithTileCoordinateOutsideMatrix_ReturnsBadRequest()
    {
        await SeedFullWorldRasterAsync(1);

        var response = await _fixture.Client.GetAsync("/terrain/0/1/2/0.png");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");
    }

    [IntegrationTest]
    [Operation(Operations.GetTileMetadata)]
    [Endpoint("GET /terrain/{datasetId}/tile.json")]
    public async Task GetTerrainMetadata_WhenTerrainProtocolDisabled_ReturnsNotFound()
    {
        await SeedFullWorldRasterAsync(1);
        // V2 cutover (#1035 72/N): protocol gating reads MetadataV2Service.Protocols.
        var anonymous = new AccessPolicy { AllowAnonymous = true };
        _fixture.UpdateV2ServiceMetadata(
            WebAppFixture.TestServiceId,
            enabledProtocols: AllProtocols
                .Where(static protocol => !string.Equals(protocol, TerrainProtocolName, StringComparison.Ordinal))
                .ToArray(),
            accessPolicy: anonymous);

        try
        {
            var response = await _fixture.Client.GetAsync("/terrain/0/tile.json");

            response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        }
        finally
        {
            _fixture.UpdateV2ServiceMetadata(
                WebAppFixture.TestServiceId,
                enabledProtocols: AllProtocols,
                accessPolicy: anonymous);
        }
    }

    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("GET /terrain/{datasetId}/{z}/{x}/{y}.png")]
    public async Task GetTerrainTile_WithUnsupportedMultibandSource_ReturnsUnprocessableEntity()
    {
        await ReplaceLayerRasterSqlAsync("""
            ST_AddBand(
                ST_AddBand(
                    ST_MakeEmptyRaster(1, 1, @upperLeftX, @upperLeftY, @scaleX, @scaleY, 0, 0, 3857),
                    '32BF'::text,
                    10,
                    NULL),
                '32BF'::text,
                20,
                NULL)
            """);

        var response = await _fixture.Client.GetAsync("/terrain/0/0/0/0.png");

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("single numeric elevation band");
    }

    [IntegrationTest]
    [Operation(Operations.GetTileMetadata)]
    [Endpoint("GET /terrain/{datasetId}/tile.json")]
    public async Task GetTerrainMetadata_WithUnknownSourceCrs_DoesNotAdvertiseInvalidEpsgCode()
    {
        await ReplaceLayerWithUnknownCrsRasterAsync();

        var response = await _fixture.Client.GetAsync("/terrain/0/tile.json");

        await AssertUnsupportedCrsMetadataAsync(response);
    }

    [IntegrationTest]
    [Operation(Operations.GetTileMetadata)]
    [Endpoint("GET /terrain/{datasetId}/tile.json")]
    public async Task GetTerrainMetadata_WithUnregisteredPositiveSourceCrs_DoesNotAdvertiseInvalidEpsgCode()
    {
        await ReplaceLayerWithUnregisteredCrsRasterAsync();

        var response = await _fixture.Client.GetAsync("/terrain/0/tile.json");

        await AssertUnsupportedCrsMetadataAsync(response);
    }

    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("GET /terrain/{datasetId}/{z}/{x}/{y}.png")]
    public async Task GetTerrainTile_WithUnknownSourceCrs_ReturnsUnprocessableEntity()
    {
        await ReplaceLayerWithUnknownCrsRasterAsync();

        var response = await _fixture.Client.GetAsync("/terrain/0/0/0/0.png");

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("CRS");
    }

    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("GET /terrain/{datasetId}/{z}/{x}/{y}.png")]
    public async Task GetTerrainTile_WithUnregisteredPositiveSourceCrs_ReturnsUnprocessableEntity()
    {
        await ReplaceLayerWithUnregisteredCrsRasterAsync();

        var response = await _fixture.Client.GetAsync("/terrain/0/0/0/0.png");

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("CRS");
    }

    private Task SeedFullWorldRasterAsync(double elevationMeters)
        => RasterIntegrationTestData.ReplaceLayerRastersAsync(
            _fixture,
            WebAppFixture.TestLayerId,
            new RasterSeed(
                Name: "world-dem",
                Width: 2,
                Height: 2,
                UpperLeftX: -WebMercatorExtent,
                UpperLeftY: WebMercatorExtent,
                ScaleX: WebMercatorExtent,
                ScaleY: -WebMercatorExtent,
                Value: elevationMeters,
                AcquisitionDate: RasterIntegrationTestData.WestAcquisition,
                CreatedAt: RasterIntegrationTestData.WestAcquisition,
                Srid: 3857));

    private Task ReplaceLayerWithUnknownCrsRasterAsync()
        => ReplaceLayerRasterSqlAsync("""
            ST_AddBand(
                ST_MakeEmptyRaster(1, 1, @upperLeftX, @upperLeftY, @scaleX, @scaleY, 0, 0, 0),
                '32BF'::text,
                10,
                NULL)
            """);

    private Task ReplaceLayerWithUnregisteredCrsRasterAsync()
        => ReplaceLayerRasterSqlAsync($$"""
            ST_AddBand(
                ST_MakeEmptyRaster(1, 1, @upperLeftX, @upperLeftY, @scaleX, @scaleY, 0, 0, {{UnregisteredSrid}}),
                '32BF'::text,
                10,
                NULL)
            """);

    private async Task ReplaceLayerRasterSqlAsync(string rasterSql)
    {
        var schemaName = _fixture.CurrentSchema
            ?? throw new InvalidOperationException("Fixture schema is not initialized.");

        await using var connection = await _fixture.Postgres.GetConnectionAsync(schemaName);

        await using (var delete = connection.CreateCommand())
        {
            delete.CommandText = "DELETE FROM honua.raster_data WHERE layer_id = @layerId;";
            delete.Parameters.AddWithValue("layerId", WebAppFixture.TestLayerId);
            await delete.ExecuteNonQueryAsync();
        }

        await using var insert = connection.CreateCommand();
        insert.CommandText = $"""
            INSERT INTO honua.raster_data (layer_id, name, description, raster, acquisition_date, created_at)
            SELECT @layerId,
                   'unsupported-dem',
                   'Unsupported raster for terrain validation tests',
                   {rasterSql},
                   @acquisitionDate,
                   @createdAt;
            """;
        insert.Parameters.AddWithValue("layerId", WebAppFixture.TestLayerId);
        insert.Parameters.AddWithValue("upperLeftX", -WebMercatorExtent);
        insert.Parameters.AddWithValue("upperLeftY", WebMercatorExtent);
        insert.Parameters.AddWithValue("scaleX", 2 * WebMercatorExtent);
        insert.Parameters.AddWithValue("scaleY", -2 * WebMercatorExtent);
        insert.Parameters.AddWithValue("acquisitionDate", RasterIntegrationTestData.WestAcquisition.UtcDateTime);
        insert.Parameters.AddWithValue("createdAt", RasterIntegrationTestData.WestAcquisition.UtcDateTime);
        await insert.ExecuteNonQueryAsync();
    }

    private static async Task<SKBitmap> DecodeBitmapAsync(HttpResponseMessage response)
    {
        var data = await response.Content.ReadAsByteArrayAsync();
        data.Length.Should().BeGreaterThan(0);
        return SKBitmap.Decode(data) ?? throw new InvalidOperationException("PNG response could not be decoded.");
    }

    private static async Task AssertUnsupportedCrsMetadataAsync(HttpResponseMessage response)
    {
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(content);
        var root = json.RootElement;

        root.GetProperty("supported").GetBoolean().Should().BeFalse();
        root.GetProperty("unsupportedReasons").EnumerateArray()
            .Select(static reason => reason.GetString())
            .Should().Contain(static reason => reason != null && reason.Contains("CRS", StringComparison.OrdinalIgnoreCase));
        root.GetProperty("bounds").ValueKind.Should().Be(JsonValueKind.Null);

        var source = root.GetProperty("source");
        source.GetProperty("sourceSrid").ValueKind.Should().Be(JsonValueKind.Null);
        source.GetProperty("sourceCrs").ValueKind.Should().Be(JsonValueKind.Null);
        source.GetProperty("sourceExtent").ValueKind.Should().Be(JsonValueKind.Null);
    }

    private static void AssertTerrainPixel(SKBitmap bitmap, int x, int y, double elevationMeters)
    {
        var expected = TerrainRgbEncoding.Encode(elevationMeters);
        var actual = bitmap.GetPixel(x, y);

        actual.Red.Should().Be(expected.R);
        actual.Green.Should().Be(expected.G);
        actual.Blue.Should().Be(expected.B);
        actual.Alpha.Should().Be(byte.MaxValue);
    }
}
