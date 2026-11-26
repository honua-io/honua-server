// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Raster.Domain;
using Honua.Scene.Terrain;
using Honua.TestKit.Infrastructure;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using SkiaSharp;

namespace Honua.Server.Tests.Features.Protocols.Terrain;

[Collection("Database")]
[Protocol(TestProtocols.Terrain)]
public sealed class TerrainEndpointTests : IAsyncLifetime
{
    private const double WebMercatorExtent = TerrainTestRasters.WebMercatorExtent;

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
        await using var heldRasters = await HoldFullWorldRasterAsync(123.4);

        var response = await _fixture.Client.GetAsync("/terrain/0/tile.json");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");
        // The development-bypass identity is authenticated and must not enter a cache.
        response.Headers.CacheControl?.Private.Should().BeTrue();
        response.Headers.CacheControl?.Public.Should().BeFalse();
        response.Headers.Vary.Should().Contain("X-API-Key");
        response.Headers.CacheControl?.NoStore.Should().BeTrue();
        response.Headers.CacheControl?.MaxAge.Should().BeNull();
        response.Headers.CacheControl?.SharedMaxAge.Should().BeNull();

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

        var coverage = root.GetProperty("coverage");
        coverage.GetProperty("hasCoverage").GetBoolean().Should().BeTrue();
        coverage.GetProperty("rasterCount").GetInt32().Should().Be(1);

        var capabilities = root.GetProperty("capabilities");
        capabilities.GetProperty("terrainRgb").GetProperty("available").GetBoolean().Should().BeTrue();
        capabilities.GetProperty("terrainRgb").GetProperty("status").GetString().Should().Be("available");
        capabilities.GetProperty("quantizedMesh").GetProperty("available").GetBoolean().Should().BeTrue();
        capabilities.GetProperty("quantizedMesh").GetProperty("status").GetString().Should().Be("available");
        capabilities.GetProperty("quantizedMesh").GetProperty("href").GetString().Should().Contain("/terrain/0/layer.json");

        var links = root.GetProperty("links").EnumerateArray().ToArray();
        links.Should().Contain(link =>
            link.GetProperty("rel").GetString() == "self"
            && link.GetProperty("format").GetString() == "terrain-rgb"
            && link.GetProperty("href").GetString()!.Contains("/terrain/0/tile.json", StringComparison.Ordinal));
        links.Should().Contain(link =>
            link.GetProperty("rel").GetString() == "alternate"
            && link.GetProperty("format").GetString() == "quantized-mesh-1.0"
            && link.GetProperty("href").GetString()!.Contains("/terrain/0/layer.json", StringComparison.Ordinal));
    }

    [IntegrationTest]
    [Operation(Operations.GetTileMetadata)]
    [Endpoint("GET /terrain/{datasetId}/tile.json")]
    public async Task GetTerrainMetadata_WithCollectionName_ResolvesDataset()
    {
        await using var heldRasters = await HoldFullWorldRasterAsync(12.3);

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
        await using var heldRasters = await HoldFullWorldRasterAsync(123.4);

        var response = await _fixture.Client.GetAsync("/terrain/0/0/0/0.png");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("image/png");
        // The development-bypass identity is authenticated and must not enter a cache.
        response.Headers.CacheControl?.Private.Should().BeTrue();
        response.Headers.CacheControl?.Public.Should().BeFalse();
        response.Headers.Vary.Should().Contain("X-API-Key");
        response.Headers.CacheControl?.NoStore.Should().BeTrue();
        response.Headers.CacheControl?.MaxAge.Should().BeNull();
        response.Headers.CacheControl?.SharedMaxAge.Should().BeNull();

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
        await using var heldRasters = await TerrainTestRasters.HoldSouthEastQuadrantAsync(_fixture, 50);

        var response = await _fixture.Client.GetAsync("/terrain/0/1/0/0.png");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var bitmap = await DecodeBitmapAsync(response);
        using (bitmap)
        {
            bitmap.Width.Should().Be(TerrainRgbEncoding.TileSize);
            bitmap.Height.Should().Be(TerrainRgbEncoding.TileSize);
            AssertTransparentNoDataPixel(bitmap, 128, 128);
        }
    }

    [IntegrationTest]
    [Operation(Operations.GetTile)]
    [Endpoint("GET /terrain/{datasetId}/{z}/{x}/{y}.png")]
    public async Task GetTerrainTile_WithPartialCoverage_EncodesCoveredAndUncoveredPixels()
    {
        await using var heldRasters = await RasterIntegrationTestData.HoldLayerRastersAsync(
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
            AssertTransparentNoDataPixel(bitmap, 64, 128);
            AssertTerrainPixel(bitmap, 192, 128, 50);
        }
    }

    [IntegrationTest]
    [Operation(Operations.GetTile)]
    [Endpoint("GET /terrain/{datasetId}/{z}/{x}/{y}.png")]
    public async Task GetTerrainTile_WithSourceNoData_ReturnsTransparentPixels()
    {
        await using var heldRasters = await HoldRasterSqlAsync("""
            ST_AddBand(
                ST_MakeEmptyRaster(2, 2, @upperLeftX, @upperLeftY, @scaleX / 2, @scaleY / 2, 0, 0, 3857),
                '32BF'::text,
                -9999,
                -9999)
            """);

        var response = await _fixture.Client.GetAsync("/terrain/0/0/0/0.png");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var bitmap = await DecodeBitmapAsync(response);
        for (var row = 0; row < bitmap.Height; row++)
        {
            for (var column = 0; column < bitmap.Width; column++)
            {
                AssertTransparentNoDataPixel(bitmap, column, row);
            }
        }
    }

    [IntegrationTest]
    [Operation(Operations.GetTile)]
    [Endpoint("GET /terrain/{datasetId}/{z}/{x}/{y}.png")]
    public async Task GetTerrainTile_WithValidMinusTenThousandElevation_RemainsOpaque()
    {
        await using var heldRasters = await HoldFullWorldRasterAsync(-10000);

        var response = await _fixture.Client.GetAsync("/terrain/0/0/0/0.png");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var bitmap = await DecodeBitmapAsync(response);
        AssertTerrainPixel(bitmap, 128, 128, -10000);
    }

    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("GET /terrain/{datasetId}/tile.json")]
    public async Task GetTerrainMetadata_WithUnknownDataset_ReturnsNotFound()
    {
        await using var heldRasters = await HoldFullWorldRasterAsync(1);

        var response = await _fixture.Client.GetAsync("/terrain/999999/tile.json");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");
    }

    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("GET /terrain/{datasetId}/{z}/{x}/{y}.png")]
    public async Task GetTerrainTile_WithZoomAboveLimit_ReturnsBadRequest()
    {
        await using var heldRasters = await HoldFullWorldRasterAsync(1);

        var response = await _fixture.Client.GetAsync("/terrain/0/23/0/0.png");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");
    }

    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("GET /terrain/{datasetId}/{z}/{x}/{y}.png")]
    public async Task GetTerrainTile_WithTileCoordinateOutsideMatrix_ReturnsBadRequest()
    {
        await using var heldRasters = await HoldFullWorldRasterAsync(1);

        var response = await _fixture.Client.GetAsync("/terrain/0/1/2/0.png");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");
    }

    [IntegrationTest]
    [Operation(Operations.GetTileMetadata)]
    [Endpoint("GET /terrain/{datasetId}/tile.json")]
    public async Task GetTerrainMetadata_WhenTerrainProtocolDisabled_ReturnsNotFound()
    {
        await using var heldRasters = await HoldFullWorldRasterAsync(1);
        // Protocol gating reads MetadataV2Service.Protocols.
        var anonymous = new AccessPolicy { AllowAnonymous = true };
        _fixture.UpdateV2ServiceMetadata(
            WebAppFixture.TestServiceId,
            enabledProtocols: TerrainTestRasters.ProtocolsWithoutTerrain,
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
                enabledProtocols: TerrainTestRasters.AllProtocols,
                accessPolicy: anonymous);
        }
    }

    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("GET /terrain/{datasetId}/{z}/{x}/{y}.png")]
    public async Task GetTerrainTile_WithUnsupportedMultibandSource_ReturnsUnprocessableEntity()
    {
        await using var heldRasters = await HoldRasterSqlAsync("""
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
        await using var heldRasters = await HoldUnknownCrsRasterAsync();

        var response = await _fixture.Client.GetAsync("/terrain/0/tile.json");

        await AssertUnsupportedCrsMetadataAsync(response);
    }

    [IntegrationTest]
    [Operation(Operations.GetTileMetadata)]
    [Endpoint("GET /terrain/{datasetId}/tile.json")]
    public async Task GetTerrainMetadata_WithUnregisteredPositiveSourceCrs_DoesNotAdvertiseInvalidEpsgCode()
    {
        await using var heldRasters = await HoldUnregisteredCrsRasterAsync();

        var response = await _fixture.Client.GetAsync("/terrain/0/tile.json");

        await AssertUnsupportedCrsMetadataAsync(response);
    }

    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("GET /terrain/{datasetId}/{z}/{x}/{y}.png")]
    public async Task GetTerrainTile_WithUnknownSourceCrs_ReturnsUnprocessableEntity()
    {
        await using var heldRasters = await HoldUnknownCrsRasterAsync();

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
        await using var heldRasters = await HoldUnregisteredCrsRasterAsync();

        var response = await _fixture.Client.GetAsync("/terrain/0/0/0/0.png");

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("CRS");
    }

    [IntegrationTest]
    [Operation(Operations.GetTileMetadata)]
    [Endpoint("GET /terrain/{datasetId}/tile.json")]
    [Endpoint("GET /terrain/{datasetId}/layer.json")]
    public async Task GetTerrainMetadataDocuments_SendETag_AndAnswerMatchingIfNoneMatchWith304()
    {
        await using var heldRasters = await HoldFullWorldRasterAsync(123.4);

        foreach (var path in new[] { "/terrain/0/tile.json", "/terrain/0/layer.json" })
        {
            var response = await _fixture.Client.GetAsync(path);

            response.StatusCode.Should().Be(HttpStatusCode.OK, path);
            var etag = response.Headers.ETag;
            etag.Should().NotBeNull($"{path} is mapped with WithETag");
            using var conditional = new HttpRequestMessage(HttpMethod.Get, path);
            conditional.Headers.IfNoneMatch.Add(etag!);
            var revalidated = await _fixture.Client.SendAsync(conditional);
            revalidated.StatusCode.Should().Be(HttpStatusCode.NotModified, path);
        }
    }

    [IntegrationTest]
    [Operation(Operations.GetTileMetadata)]
    [Endpoint("GET /terrain/{datasetId}/layer.json")]
    public async Task GetTerrainLayerJson_WithRasterFixture_ReturnsQuantizedMeshMetadataAndCrossLinks()
    {
        await using var heldRasters = await HoldFullWorldRasterAsync(123.4);

        var response = await _fixture.Client.GetAsync("/terrain/0/layer.json");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");
        var content = await response.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(content);
        var root = json.RootElement;

        root.GetProperty("format").GetString().Should().Be("quantized-mesh-1.0");
        root.GetProperty("version").GetString().Should().NotBeNullOrWhiteSpace();
        root.GetProperty("projection").GetString().Should().Be("EPSG:4326");
        root.GetProperty("scheme").GetString().Should().Be("tms");
        root.GetProperty("bounds").GetArrayLength().Should().Be(4);
        root.GetProperty("tiles")[0].GetString().Should().Contain("/terrain/0/{z}/{x}/{y}.terrain");
        root.GetProperty("extensions").EnumerateArray().Select(static value => value.GetString())
            .Should().Contain("octvertexnormals");
        root.GetProperty("available").GetArrayLength().Should().BeGreaterThan(0);
        root.GetProperty("supported").GetBoolean().Should().BeTrue();
        root.GetProperty("links").EnumerateArray().Select(static link => link.GetProperty("href").GetString())
            .Should().Contain(static href => href != null && href.Contains("/terrain/0/tile.json", StringComparison.Ordinal))
            .And.Contain(static href => href != null && href.Contains("/terrain/0/layer.json", StringComparison.Ordinal));
    }

    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("GET /terrain/{datasetId}/layer.json")]
    public async Task GetTerrainLayerJson_WithUnknownDataset_ReturnsNotFound()
    {
        await using var heldRasters = await HoldFullWorldRasterAsync(1);

        var response = await _fixture.Client.GetAsync("/terrain/999999/layer.json");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");
    }

    [IntegrationTest]
    [Operation(Operations.GetTileMetadata)]
    [Endpoint("GET /terrain/{datasetId}/layer.json")]
    public async Task GetTerrainLayerJson_WithUnknownSourceCrs_ReportsUnavailableCapabilities()
    {
        await using var heldRasters = await HoldUnknownCrsRasterAsync();

        var response = await _fixture.Client.GetAsync("/terrain/0/layer.json");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(content);
        var root = json.RootElement;

        root.GetProperty("supported").GetBoolean().Should().BeFalse();
        root.GetProperty("available").GetArrayLength().Should().Be(0);
        root.GetProperty("capabilities").GetProperty("quantizedMesh").GetProperty("available").GetBoolean().Should().BeFalse();
        root.GetProperty("capabilities").GetProperty("quantizedMesh").GetProperty("status").GetString().Should().Be("unavailable");
        root.GetProperty("capabilities").GetProperty("terrainRgb").GetProperty("available").GetBoolean().Should().BeFalse();
    }

    [IntegrationTest]
    [Operation(Operations.GetTile)]
    [Endpoint("GET /terrain/{datasetId}/{z}/{x}/{y}.terrain")]
    public async Task GetTerrainQuantizedMeshTile_WithRegisteredDem_ReturnsDecodableGzippedMesh()
    {
        await using var heldRasters = await HoldFullWorldRasterAsync(123.4);

        var response = await _fixture.Client.GetAsync("/terrain/0/0/0/0.terrain");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be(QuantizedMeshTileWriter.MediaType);
        response.Content.Headers.ContentEncoding.Should().Contain("gzip");
        response.Headers.Vary.Should().Contain("Accept");

        var bytes = await response.Content.ReadAsByteArrayAsync();
        var tile = QuantizedMeshTileReader.Read(bytes);
        tile.U.Length.Should().Be(TerrainTmsGrid.DefaultGridSize * TerrainTmsGrid.DefaultGridSize);
        tile.Header.MaximumHeight.Should().BeApproximately(123.4f, 0.5f);
        tile.OctEncodedNormals.Should().BeNull();
    }

    [IntegrationTest]
    [Operation(Operations.GetTile)]
    [Endpoint("GET /terrain/{datasetId}/{z}/{x}/{y}.terrain")]
    public async Task GetTerrainQuantizedMeshTile_WithOctVertexNormalsAcceptHeader_ReturnsNormalsExtension()
    {
        await using var heldRasters = await HoldFullWorldRasterAsync(250.0);

        using var request = new HttpRequestMessage(HttpMethod.Get, "/terrain/0/1/0/1.terrain");
        request.Headers.Accept.Add(MediaTypeWithQualityHeaderValue.Parse("application/vnd.quantized-mesh;extensions=octvertexnormals"));

        var response = await _fixture.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be(QuantizedMeshTileWriter.MediaType);
        var bytes = await response.Content.ReadAsByteArrayAsync();
        var tile = QuantizedMeshTileReader.Read(bytes);
        tile.OctEncodedNormals.Should().NotBeNull();
        tile.OctEncodedNormals!.Length.Should().Be(tile.U.Length * 2);
    }

    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("GET /terrain/{datasetId}/{z}/{x}/{y}.terrain")]
    public async Task GetTerrainQuantizedMeshTile_WithInvalidTmsCoordinates_ReturnsBadRequest()
    {
        await using var heldRasters = await HoldFullWorldRasterAsync(1);

        var response = await _fixture.Client.GetAsync("/terrain/0/0/2/0.terrain");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");
    }

    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("GET /terrain/{datasetId}/{z}/{x}/{y}.terrain")]
    public async Task GetTerrainQuantizedMeshTile_WithUnsupportedMultibandSource_ReturnsUnprocessableEntity()
    {
        await using var heldRasters = await HoldRasterSqlAsync("""
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

        var response = await _fixture.Client.GetAsync("/terrain/0/0/0/0.terrain");

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("single numeric elevation band");
    }

    private Task<IAsyncDisposable> HoldFullWorldRasterAsync(double elevationMeters)
        => TerrainTestRasters.HoldFullWorldAsync(_fixture, elevationMeters);

    private Task<IAsyncDisposable> HoldUnknownCrsRasterAsync()
        => TerrainTestRasters.HoldUnknownCrsAsync(_fixture);

    private Task<IAsyncDisposable> HoldUnregisteredCrsRasterAsync()
        => TerrainTestRasters.HoldUnregisteredCrsAsync(_fixture);

    private Task<IAsyncDisposable> HoldRasterSqlAsync(string rasterSql)
        => TerrainTestRasters.HoldRasterSqlAsync(_fixture, rasterSql);

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

    private static void AssertTransparentNoDataPixel(SKBitmap bitmap, int x, int y)
    {
        var actual = bitmap.GetPixel(x, y);
        actual.Red.Should().Be(0);
        actual.Green.Should().Be(0);
        actual.Blue.Should().Be(0);
        actual.Alpha.Should().Be(0);
    }
}
