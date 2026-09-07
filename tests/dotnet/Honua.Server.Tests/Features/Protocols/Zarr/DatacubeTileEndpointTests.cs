// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
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

/// <summary>
/// Endpoint-level coverage for the datacube (Zarr coverage) slice -> tile render route (#1835),
/// exercising the public GET so it is backed by a real HTTP request (EndpointRegistryDriftTests).
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.Admin)]
[Operation(Operations.Tile)]
public sealed class DatacubeTileEndpointTests : IAsyncLifetime
{
    /// <summary>Edge length of the registered cube, in cells.</summary>
    /// <remarks>
    /// Odd on purpose. The zoom-1 tile edge falls at the world centre, which on a nine-cell axis
    /// spanning the whole world is cell index 4.5 — strictly inside a cell. An even grid would put
    /// that edge exactly on a cell boundary, where a sub-ulp difference in the tile-bounds
    /// arithmetic decides whether the boundary cell is included, and the expected window would
    /// depend on floating-point luck rather than on the geometry.
    /// </remarks>
    private const int Grid = 9;

    /// <summary>Rendered tile edge length (<c>ZarrTileRenderer.DefaultTileSize</c>).</summary>
    private const int TileSize = 256;

    /// <summary>Half-extent of the WebMercatorQuad world, and of the registered cube.</summary>
    private const double WorldExtent = SpatialConstants.WebMercatorExtent;

    private const string CubeRoot = "cubes/datacube-tile";

    private readonly WebAppFixture _fixture = new();
    private readonly WebAppFixture _anonymousFixture = new WebAppFixture()
        .UseSeed("tests/seed/server.yaml")
        .ReplaceService<IMetadataV2GraphProvider>(BuildProtectedLayerGraphProvider())
        .ConfigureWebHost(builder =>
        {
            builder.UseEnvironment("Test");
            builder.UseSetting("HONUA_DEV_AUTH", "false");
        });

    /// <summary>
    /// A host whose only storage reader serves the in-memory cube below, so the tile route runs
    /// its production path — publication resolution, authorization, registration lookup, slice
    /// planning, chunk decode and render — against a cube with known cell values.
    /// </summary>
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

    private HttpClient _client = null!;
    private HttpClient _anonymousClient = null!;

    /// <summary>The cube's cell value at storage row <paramref name="row"/>, column <paramref name="col"/>.</summary>
    /// <remarks>
    /// Asymmetric (<c>Sample(r, c) != Sample(c, r)</c> off the diagonal) so a render that swapped
    /// the X and Y strides is visibly different, and strictly increasing along both axes so the
    /// grey ramp's endpoints are the window's north-west and south-east cells.
    /// </remarks>
    private static float Sample(int row, int col) => (row * 10f) + col;

    private static TestMetadataV2GraphProvider BuildProtectedLayerGraphProvider()
        => new TestMetadataV2GraphBuilder()
            // An unrelated public resource has a storage id matching the protected
            // publication index. Its policy must never authorize the tile request.
            .AddResource(
                "res-public-collision",
                "Unrelated Public Raster",
                MetadataV2ResourceType.RasterDataset,
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
                MetadataV2ResourceType.RasterDataset)
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
                publicationType: MetadataV2PublicationType.OgcCollection)
            .BuildProvider();

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        _client = _fixture.Client;
        await _anonymousFixture.InitializeAsync();
        _anonymousClient = _anonymousFixture.Client;
        await _cubeFixture.InitializeAsync();
        await RegisterAndScanCubeAsync();
    }

    public async Task DisposeAsync()
    {
        await _fixture.DisposeAsync();
        await _anonymousFixture.DisposeAsync();
        await _cubeFixture.DisposeAsync();
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/datacubes/{layerId}/tiles/{tileMatrixSetId}/{z}/{x}/{y}")]
    public async Task DatacubeTile_LayerWithoutServableCoverage_Returns404()
    {
        // The default test layer has no registered Zarr coverage, so the datacube tile
        // handler resolves no servable coverage and returns 404.
        var response = await _client.GetAsync(
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
        var response = await _anonymousClient.GetAsync(
            $"/api/v1/datacubes/{WebAppFixture.TestLayerId}/tiles/WebMercatorQuad/0/0/0");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    /// <summary>
    /// The first positive test of the datacube tile route (honua-server#4395).
    /// </summary>
    /// <remarks>
    /// Zarr serving is GA in 2026.1, and this route's only coverage was a 404 and a 401 — nothing
    /// asserted that it ever returns a correct tile. The cube is registered and scanned through the
    /// production admin endpoints, then a zoom-0 WebMercatorQuad tile is requested over HTTP and
    /// every pixel is decoded and compared against the value its own source cell carries.
    /// </remarks>
    [IntegrationTest]
    [Endpoint("GET /api/v1/datacubes/{layerId}/tiles/{tileMatrixSetId}/{z}/{x}/{y}")]
    public async Task DatacubeTile_RegisteredCoverage_RendersEveryCubeCellAtZoomZero()
    {
        var response = await _cubeFixture.Client.GetAsync(
            $"/api/v1/datacubes/{WebAppFixture.TestLayerId}/tiles/WebMercatorQuad/0/0/0");

        var png = await ReadTilePngAsync(response);

        // Zoom 0 is the whole WebMercatorQuad world, which is exactly the cube's declared extent,
        // so the selected window is the entire grid.
        AssertTilePixels(png, windowRow: 0, windowCol: 0, windowRows: Grid, windowCols: Grid);
    }

    /// <summary>
    /// The tile index must select the matching window of the cube, not the whole cube (#4395).
    /// </summary>
    /// <remarks>
    /// A renderer that ignored z/x/y and drew the full cube into every tile satisfied every
    /// assertion this route previously carried. The zoom-1 north-east tile spans x in [0, E] and
    /// y in [0, E] of a cube covering [-E, E] on both axes, so the half-open index window is
    /// columns [4, 9) and rows [0, 5).
    /// </remarks>
    [IntegrationTest]
    [Endpoint("GET /api/v1/datacubes/{layerId}/tiles/{tileMatrixSetId}/{z}/{x}/{y}")]
    public async Task DatacubeTile_ZoomOneNorthEastTile_RendersOnlyThatWindowOfTheCube()
    {
        var response = await _cubeFixture.Client.GetAsync(
            $"/api/v1/datacubes/{WebAppFixture.TestLayerId}/tiles/WebMercatorQuad/1/1/0");

        var png = await ReadTilePngAsync(response);

        var image = AssertTilePixels(png, windowRow: 0, windowCol: 4, windowRows: 5, windowCols: 5);

        // Corner anchors stated in cube terms rather than pixel terms: the tile's north-west pixel
        // is the window's north-west cell (row 0, col 4) — the ramp minimum, so pure black — and its
        // south-east pixel is (row 4, col 8), the ramp maximum, so pure white. A transposed, flipped
        // or whole-cube render moves at least one of them.
        image.Pixel(0, 0).Should().Be(((byte)0, (byte)0, (byte)0, (byte)255));
        image.Pixel(TileSize - 1, TileSize - 1).Should().Be(((byte)255, (byte)255, (byte)255, (byte)255));
    }

    /// <summary>
    /// Registers the in-memory cube through the production admin endpoints and scans its metadata,
    /// so the served tiles depend on the same registration/scan path an operator drives.
    /// </summary>
    private async Task RegisterAndScanCubeAsync()
    {
        var register = await _cubeFixture.Client.PostAsJsonAsync(
            "/api/v1/admin/zarr-stores",
            new
            {
                layerId = WebAppFixture.TestLayerId,
                name = "datacube-tile-cube",
                provider = "AwsS3",
                bucket = "bucket",
                rootPath = CubeRoot,
            });
        register.StatusCode.Should().Be(HttpStatusCode.Created, await register.Content.ReadAsStringAsync());

        using var created = JsonDocument.Parse(await register.Content.ReadAsStringAsync());
        var id = created.RootElement.GetProperty("id").GetInt64();

        var refresh = await _cubeFixture.Client.PostAsync($"/api/v1/admin/zarr-stores/{id}/refresh", null);
        refresh.StatusCode.Should().Be(HttpStatusCode.OK, await refresh.Content.ReadAsStringAsync());

        using var scanned = JsonDocument.Parse(await refresh.Content.ReadAsStringAsync());
        // The scan must have read the store rather than recorded an empty registration: these are
        // the cube's own declarations, and the tile route refuses to serve without them.
        scanned.RootElement.GetProperty("srid").GetInt32().Should().Be(3857);
        scanned.RootElement.GetProperty("primaryVariable").GetString().Should().Be("temperature");
        scanned.RootElement.GetProperty("variableCount").GetInt32().Should().Be(1);
    }

    private static async Task<byte[]> ReadTilePngAsync(HttpResponseMessage response)
    {
        var bytes = await response.Content.ReadAsByteArrayAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, System.Text.Encoding.UTF8.GetString(bytes));
        response.Content.Headers.ContentType?.MediaType.Should().Be("image/png");
        return bytes;
    }

    /// <summary>
    /// Decodes the served tile and asserts every pixel against the cube cell it renders.
    /// </summary>
    /// <remarks>
    /// The tile covers the window <c>[windowRow, windowRow + windowRows) x [windowCol, windowCol +
    /// windowCols)</c> of the cube, so output pixel (px, py) samples window cell
    /// <c>(py * windowRows / TileSize, px * windowCols / TileSize)</c> — the nearest-neighbour block
    /// map for a north-up tile over a north-to-south stored grid. With no colormap the display ramp
    /// spans the window's own finite range, and <see cref="Sample"/> increases along both axes, so
    /// that range runs from the window's north-west cell to its south-east cell. Comparing by
    /// coordinate rather than as a multiset is what pins the spatial mapping: a multiset comparison
    /// passes for a transposed or vertically flipped render.
    /// </remarks>
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
