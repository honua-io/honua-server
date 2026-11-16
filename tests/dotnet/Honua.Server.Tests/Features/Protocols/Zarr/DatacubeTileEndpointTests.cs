// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Security.Domain;
using Honua.Core.Features.Shared.Models;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Formats;
using Honua.TestKit.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.Server.Tests.Features.Protocols.Zarr;

[Collection("Database")]
[Protocol(TestProtocols.Admin)]
[Operation(Operations.Tile)]
public sealed class DatacubeTileEndpointTests : IAsyncLifetime
{
    private const int Grid = 9;

    private const int TileSize = 256;

    private const double WorldExtent = SpatialConstants.WebMercatorExtent;

    private const string CubeRoot = "cubes/datacube-tile";

    private const int CubeLayerId = 1;

    private readonly WebAppFixture _fixture = new();
    private readonly WebAppFixture _anonymousFixture = new WebAppFixture()
        .UseSeed("tests/seed/server.yaml")
        .ReplaceService<IMetadataV2GraphProvider>(BuildProtectedLayerGraphProvider())
        .ConfigureWebHost(builder =>
        {
            builder.UseEnvironment("Test");
            builder.UseSetting("HONUA_DEV_AUTH", "false");
        });

    private readonly WebAppFixture _cubeFixture = new WebAppFixture()
        .ConfigureServices(services =>
        {
            services.RemoveAll<ICloudRangeReader>();
            services.AddSingleton<ICloudRangeReader>(new InMemoryZarrRangeReader(
                ZarrFixtureBuilder.BuildGroupedZlib(
                    root: CubeRoot,
                    rows: Grid,
                    cols: Grid,
                    // Three cells per chunk on each axis, so every window asserted below spans
                    // more than one chunk and the render depends on chunk assembly, not on a
                    // single decoded block.
                    chunkRows: 3,
                    chunkCols: 3,
                    sample: Sample,
                    srid: 3857,
                    xMin: -WorldExtent,
                    yMin: -WorldExtent,
                    xMax: WorldExtent,
                    yMax: WorldExtent)));
        });

    private readonly List<WebAppFixture> _started = [];

    private static float Sample(int row, int col)
    {
        // Evaluated in float space from the start rather than multiplying as integers and casting
        // at the end, so there is no integer-overflow-into-float pattern for the analyzer (or a
        // future larger grid) to flag. Every value on this grid is exactly representable.
        float r = row;
        float c = col;
        return (r * r * 100f) + (c * c);
    }

    private static TestMetadataV2GraphProvider BuildProtectedLayerGraphProvider()
        => new TestMetadataV2GraphBuilder()
            // An unrelated public resource has a storage id matching the protected
            // publication index. Its policy must never authorize the tile request.
            .AddResource(
                "res-public-collision",
                "Unrelated Public Raster",
                MetadataV2ObjectType.RasterDataset,
                accessPolicy: new AccessPolicy { AllowAnonymous = true })
            .AddStorageBinding(
                "binding-public-collision",
                "res-public-collision",
                "public.zarr",
                storageLayerId: WebAppFixture.TestLayerId,
                storageType: MetadataV2StorageType.Zarr)
            .AddResource(
                "res-zarr-layer-0",
                "Protected Zarr Layer",
                MetadataV2ObjectType.RasterDataset)
            .AddStorageBinding(
                "binding-zarr-layer-0",
                "res-zarr-layer-0",
                "protected.zarr",
                // Deliberately differ from the service-local publication index. The
                // route must authorize the resolved publication/resource, not treat
                // its index as a storage-layer id.
                storageLayerId: WebAppFixture.TestLayerId + 1000,
                storageType: MetadataV2StorageType.Zarr)
            .AddService(
                "svc-zarr",
                "zarr",
                protocols: ["OGC-API-Coverages"],
                accessPolicy: new AccessPolicy { AllowAnonymous = false })
            .AddPublication(
                "pub-zarr-layer-0",
                "svc-zarr",
                "res-zarr-layer-0",
                layerIndex: WebAppFixture.TestLayerId,
                storageBindingId: "binding-zarr-layer-0",
                publicationType: MetadataV2LayerType.OgcCollection)
            .BuildProvider();

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var fixture in _started)
        {
            await fixture.DisposeAsync();
        }
    }

    private async Task<HttpClient> StartAsync(WebAppFixture fixture)
    {
        await fixture.InitializeAsync();
        _started.Add(fixture);
        return fixture.Client;
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/datacubes/{layerId}/tiles/{tileMatrixSetId}/{z}/{x}/{y}")]
    public async Task DatacubeTile_LayerWithoutServableCoverage_Returns404()
    {
        // The default test layer has no registered Zarr coverage, so the datacube tile
        // handler resolves no servable coverage and returns 404.
        var client = await StartAsync(_fixture);
        var response = await client.GetAsync(
            $"/api/v1/datacubes/{WebAppFixture.TestLayerId}/tiles/WebMercatorQuad/0/0/0");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/datacubes/{layerId}/tiles/{tileMatrixSetId}/{z}/{x}/{y}")]
    public async Task DatacubeTile_WithoutAuthentication_Returns401BeforeStoreLookup()
    {
        // Before the per-layer authorization guard, this request reached the Zarr
        // store and returned 404. A valid layer id must not reveal registration
        // state or pixels to an anonymous caller.
        var client = await StartAsync(_anonymousFixture);
        var response = await client.GetAsync(
            $"/api/v1/datacubes/{WebAppFixture.TestLayerId}/tiles/WebMercatorQuad/0/0/0");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/datacubes/{layerId}/tiles/{tileMatrixSetId}/{z}/{x}/{y}")]
    public async Task DatacubeTile_RegisteredCoverage_RendersEveryCubeCellAtZoomZero()
    {
        var client = await StartCubeAsync();
        var response = await client.GetAsync(
            $"/api/v1/datacubes/{CubeLayerId}/tiles/WebMercatorQuad/0/0/0");

        var png = await ReadTilePngAsync(response);

        // Zoom 0 is the whole WebMercatorQuad world, which is exactly the cube's declared extent,
        // so the selected window is the entire grid.
        AssertTilePixels(png, windowRow: 0, windowCol: 0, windowRows: Grid, windowCols: Grid);
    }

    [IntegrationTheory]
    [InlineData("<f4")]
    [InlineData("<i8")]
    [InlineData("<u8")]
    [InlineData("|b1")]
    [InlineData("<i8", true)]
    [InlineData("<u8", true)]
    [Endpoint("GET /api/v1/datacubes/{layerId}/tiles/{tileMatrixSetId}/{z}/{x}/{y}")]
    public async Task DatacubeTile_TypedFill_MasksStoredAndAbsentChunksThroughService(string dtype, bool wide = false)
    {
        var objects = ZarrFixtureBuilder.BuildGroupedZlib(CubeRoot, 2, 2, 1, 1,
            (_, _) => 0, 3857, -WorldExtent, -WorldExtent, WorldExtent, WorldExtent);
        object fill = dtype switch { "<f4" => -9999f, "<i8" => -9999L, "<u8" => 9999UL, _ => true };
        if (wide) fill = dtype == "<i8" ? (object)9007199254740993L : 9007199254740993UL;
        var arrayKey = CubeRoot + "/temperature/.zarray";
        var array = JsonNode.Parse(Encoding.UTF8.GetString(objects[arrayKey]))!.AsObject();
        array["dtype"] = dtype;
        array["compressor"] = null;
        array["fill_value"] = JsonSerializer.SerializeToNode(fill);
        objects[arrayKey] = Encoding.UTF8.GetBytes(array.ToJsonString());
        objects.Remove(CubeRoot + "/temperature/0.0"); // Must synthesize the declared typed fill.
        objects[CubeRoot + "/temperature/0.1"] = Cell(fill);
        objects[CubeRoot + "/temperature/1.0"] = Cell(dtype == "|b1" ? false : wide ? 9007199254740992L : 10L);
        objects[CubeRoot + "/temperature/1.1"] = Cell(dtype == "|b1" ? false : 30L);
        var fixture = new WebAppFixture().ConfigureServices(services =>
        {
            services.RemoveAll<ICloudRangeReader>();
            services.AddSingleton<ICloudRangeReader>(new InMemoryZarrRangeReader(objects));
        });
        var client = await StartCubeAsync(fixture);
        var image = MiniPngDecoder.Decode(await ReadTilePngAsync(await client.GetAsync(
            $"/api/v1/datacubes/{CubeLayerId}/tiles/WebMercatorQuad/0/0/0")));
        image.Pixel(0, 0).Item4.Should().Be(0, "an absent chunk must be transparent after service conversion");
        image.Pixel(TileSize - 1, 0).Item4.Should().Be(0, "a stored sentinel must be transparent too");
        var neighborGray = wide ? (byte)255 : (byte)0;
        image.Pixel(0, TileSize - 1).Should().Be((neighborGray, neighborGray, neighborGray, (byte)255));
        image.Pixel(TileSize - 1, TileSize - 1).Item4.Should().Be(255);
        if (dtype != "|b1")
            image.Pixel(TileSize - 1, TileSize - 1).Item1.Should().Be(wide ? (byte)0 : (byte)255,
                "stored/missing fill values must not enter the stretch range");

        byte[] Cell(object value)
        {
            if (dtype == "|b1") return [(bool)value ? (byte)1 : (byte)0];
            var bytes = new byte[dtype == "<f4" ? 4 : 8];
            if (dtype == "<f4") BinaryPrimitives.WriteSingleLittleEndian(bytes, Convert.ToSingle(value, CultureInfo.InvariantCulture));
            else if (dtype == "<i8") BinaryPrimitives.WriteInt64LittleEndian(bytes, Convert.ToInt64(value, CultureInfo.InvariantCulture));
            else BinaryPrimitives.WriteUInt64LittleEndian(bytes, Convert.ToUInt64(value, CultureInfo.InvariantCulture));
            return bytes;
        }
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/datacubes/{layerId}/tiles/{tileMatrixSetId}/{z}/{x}/{y}")]
    public async Task DatacubeTile_ZoomOneQuadrants_EachRenderTheirOwnWindowOfTheCube()
    {
        var client = await StartCubeAsync();

        var northEast = await ReadTilePngAsync(
            await client.GetAsync($"/api/v1/datacubes/{CubeLayerId}/tiles/WebMercatorQuad/1/1/0"));
        var northWest = await ReadTilePngAsync(
            await client.GetAsync($"/api/v1/datacubes/{CubeLayerId}/tiles/WebMercatorQuad/1/0/0"));

        var image = AssertTilePixels(northEast, windowRow: 0, windowCol: 4, windowRows: 5, windowCols: 5);
        AssertTilePixels(northWest, windowRow: 0, windowCol: 0, windowRows: 5, windowCols: 5);

        northEast.Should().NotEqual(northWest, "each tile index selects a different window of the cube");

        // Corner anchors stated in cube terms rather than pixel terms: the north-east tile's
        // north-west pixel is that window's north-west cell (row 0, col 4) — the ramp minimum, so
        // pure black — and its south-east pixel is (row 4, col 8), the ramp maximum, so pure white.
        // A transposed, flipped or whole-cube render moves at least one of them.
        image.Pixel(0, 0).Should().Be(((byte)0, (byte)0, (byte)0, (byte)255));
        image.Pixel(TileSize - 1, TileSize - 1).Should().Be(((byte)255, (byte)255, (byte)255, (byte)255));
    }

    private async Task<HttpClient> StartCubeAsync(WebAppFixture? fixture = null)
    {
        var client = await StartAsync(fixture ?? _cubeFixture);
        var register = await client.PostAsJsonAsync(
            "/api/v1/admin/zarr-stores",
            new
            {
                layerId = CubeLayerId,
                name = "datacube-tile-cube",
                provider = "AwsS3",
                bucket = "bucket",
                rootPath = CubeRoot,
            });
        register.StatusCode.Should().Be(HttpStatusCode.Created, await register.Content.ReadAsStringAsync());

        using var created = JsonDocument.Parse(await register.Content.ReadAsStringAsync());
        var id = created.RootElement.GetProperty("id").GetInt64();

        var refresh = await client.PostAsync($"/api/v1/admin/zarr-stores/{id}/refresh", null);
        refresh.StatusCode.Should().Be(HttpStatusCode.OK, await refresh.Content.ReadAsStringAsync());

        using var scanned = JsonDocument.Parse(await refresh.Content.ReadAsStringAsync());
        // The scan must have read the store rather than recorded an empty registration: these are
        // the cube's own declarations, and the tile route refuses to serve without them.
        scanned.RootElement.GetProperty("srid").GetInt32().Should().Be(3857);
        scanned.RootElement.GetProperty("primaryVariable").GetString().Should().Be("temperature");
        scanned.RootElement.GetProperty("variableCount").GetInt32().Should().Be(1);
        return client;
    }

    private static async Task<byte[]> ReadTilePngAsync(HttpResponseMessage response)
    {
        var bytes = await response.Content.ReadAsByteArrayAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, System.Text.Encoding.UTF8.GetString(bytes));
        response.Content.Headers.ContentType?.MediaType.Should().Be("image/png");
        return bytes;
    }

    private static MiniPngDecoder.DecodedImage AssertTilePixels(
        byte[] png,
        int windowRow,
        int windowCol,
        int windowRows,
        int windowCols)
    {
        var image = MiniPngDecoder.Decode(png);
        image.Width.Should().Be(TileSize);
        image.Height.Should().Be(TileSize);

        var min = Sample(windowRow, windowCol);
        var max = Sample(windowRow + windowRows - 1, windowCol + windowCols - 1);

        for (var py = 0; py < TileSize; py++)
        {
            var row = windowRow + (py * windowRows / TileSize);
            for (var px = 0; px < TileSize; px++)
            {
                var col = windowCol + (px * windowCols / TileSize);
                var value = Sample(row, col);
                var grey = (byte)Math.Clamp((int)Math.Round((value - min) / (max - min) * 255.0), 0, 255);
                var expected = (grey, grey, grey, (byte)255);
                var actual = image.Pixel(px, py);
                if (actual != expected)
                {
                    actual.Should().Be(
                        expected,
                        "pixel ({0},{1}) renders cube cell (row {2}, col {3}) whose value is {4}",
                        px,
                        py,
                        row,
                        col,
                        value);
                }
            }
        }

        return image;
    }
}
