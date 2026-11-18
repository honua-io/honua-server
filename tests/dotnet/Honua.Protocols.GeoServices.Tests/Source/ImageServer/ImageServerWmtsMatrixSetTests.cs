// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.Domain;
using Honua.Protocols.GeoServices.ImageServer;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Extensions;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.ImageServer;

[Collection("Database.GeoServicesRaster")]
[Protocol(TestProtocols.ImageServer)]
public sealed class ImageServerWmtsMatrixSetTests : IClassFixture<ImageServerWmtsMatrixSetTests.MatrixSetHosts>
{
    private const int TestLayerId = 0;
    private const string WorldCrs84Quad = "WorldCRS84Quad";
    private readonly MatrixSetHosts _hosts;

    public ImageServerWmtsMatrixSetTests(MatrixSetHosts hosts) => _hosts = hosts;

    // One host per matrix-set configuration; each case borrows one with its own raster store.
    // Database isolation does not isolate the file-backed tile cache, so each host owns a storage
    // directory, and every borrow deletes the stored files: an earlier case's tile never
    // satisfies a later case's first rendering request.
    public sealed class MatrixSetHosts : IAsyncLifetime
    {
        private readonly DirectoryInfo _storageRoot = Directory.CreateTempSubdirectory("honua-wmts-matrix-");

        public MatrixSetHosts()
        {
            var defaultStorage = _storageRoot.CreateSubdirectory("default");
            var worldCrs84QuadStorage = _storageRoot.CreateSubdirectory("world-crs84-quad");
            Default = new SwitchedServiceHost<IRasterStore>(
                CreateRasterStoreSubstitute(),
                services => UseStorage(services, defaultStorage));
            WorldCrs84QuadEnabled = new SwitchedServiceHost<IRasterStore>(
                CreateRasterStoreSubstitute(),
                services =>
                {
                    UseStorage(services, worldCrs84QuadStorage);
                    services.Configure<ImageServerTileMatrixSetOptions>(options => options.Enabled.Add(WorldCrs84Quad));
                });
        }

        public SwitchedServiceHost<IRasterStore> Default { get; }

        public SwitchedServiceHost<IRasterStore> WorldCrs84QuadEnabled { get; }

        public async Task InitializeAsync()
        {
            await Default.InitializeAsync();
            await WorldCrs84QuadEnabled.InitializeAsync();
        }

        public async Task DisposeAsync()
        {
            await Default.DisposeAsync();
            await WorldCrs84QuadEnabled.DisposeAsync();
            _storageRoot.Delete(recursive: true);
        }

        private static void UseStorage(IServiceCollection services, DirectoryInfo storage)
        {
            services.PostConfigure<LocalStorageOptions>(options => options.BasePath = storage.FullName);
            services.PostConfigure<CloudStorageOptions>(options =>
            {
                options.LocalStorage ??= new LocalStorageOptions();
                options.LocalStorage.BasePath = storage.FullName;
            });
        }
    }

    private static IRasterStore CreateRasterStoreSubstitute()
    {
        var store = Substitute.For<IRasterStore>();
        var rasterInfo = new RasterInfo
        {
            Id = 100,
            LayerId = TestLayerId,
            Name = "test-raster",
            Width = 256,
            Height = 256,
            BandCount = 1,
            PixelType = "8BUI",
            Srid = 4326,
            GeoTransform = [-180, 1.40625, 0, 90, 0, -0.703125],
            Extent = new RasterExtent { XMin = -180, YMin = -90, XMax = 180, YMax = 90, Srid = 4326 },
            CreatedAt = DateTimeOffset.UtcNow,
        };

        store.QueryRastersAsync(default, default, default).ReturnsForAnyArgs([rasterInfo]);
        store.GetRasterInfoAsync(Arg.Any<int>(), Arg.Any<long>(), Arg.Any<CancellationToken>()).Returns(rasterInfo);
        store.ListRastersAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns([rasterInfo]);
        store.GetStatisticsAsync(Arg.Any<int>(), Arg.Any<long>(), Arg.Any<int[]?>(), Arg.Any<RasterIdentifyRendering?>(), Arg.Any<CancellationToken>())
            .Returns(new[]
            {
                new RasterStatistics
                {
                    Band = 1,
                    MinValue = 0,
                    MaxValue = 255,
                    MeanValue = 128,
                    StandardDeviation = 45,
                    ValidPixelCount = 65536,
                    NoDataPixelCount = 0,
                },
            });
        return store;
    }

    private async Task<SwitchedServiceLease> CreateFixtureAsync(IRasterStore rasterStore, bool enableWorldCrs84Quad)
    {
        var host = enableWorldCrs84Quad ? _hosts.WorldCrs84QuadEnabled : _hosts.Default;
        var fixture = await host.BorrowAsync(rasterStore);

        // Delete through the storage service: it indexes stored files in memory, so removing
        // files from disk alone would leave index entries that suppress the next tile write.
        var storage = fixture.GetService<ICloudFileStorage>();
        foreach (var file in await storage.ListFilesAsync(maxResults: int.MaxValue))
        {
            await storage.DeleteAsync(file.FileId);
        }

        return fixture;
    }

    [IntegrationTest]
    [Endpoint("GET /rest/services/{id}/ImageServer/WMTS")]
    [Operation(Operations.Metadata)]
    public async Task Wmts_GetCapabilities_DefaultConfig_AdvertisesOnlyWebMercatorQuad()
    {
        var fixture = await CreateFixtureAsync(CreateRasterStoreSubstitute(), enableWorldCrs84Quad: false);
        try
        {
            var response = await fixture.Client.GetAsync(
                $"/rest/services/{TestLayerId}/ImageServer/WMTS?SERVICE=WMTS&REQUEST=GetCapabilities&VERSION=1.0.0");

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var content = await response.Content.ReadAsStringAsync();
            content.Should().Contain("<ows:Identifier>WebMercatorQuad</ows:Identifier>");
            content.Should().NotContain(WorldCrs84Quad);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("GET /rest/services/{id}/ImageServer/WMTS")]
    [Operation(Operations.Metadata)]
    public async Task Wmts_GetCapabilities_WithWorldCrs84QuadEnabled_AdvertisesMatrixSet()
    {
        var fixture = await CreateFixtureAsync(CreateRasterStoreSubstitute(), enableWorldCrs84Quad: true);
        try
        {
            var response = await fixture.Client.GetAsync(
                $"/rest/services/{TestLayerId}/ImageServer/WMTS?SERVICE=WMTS&REQUEST=GetCapabilities&VERSION=1.0.0");

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var content = await response.Content.ReadAsStringAsync();
            // Still advertises WebMercatorQuad (baseline preserved) plus the configured gridset.
            content.Should().Contain("<TileMatrixSet>WebMercatorQuad</TileMatrixSet>");
            content.Should().Contain($"<TileMatrixSet>{WorldCrs84Quad}</TileMatrixSet>");
            content.Should().Contain($"<ows:Identifier>{WorldCrs84Quad}</ows:Identifier>");
            content.Should().Contain("<ows:SupportedCRS>http://www.opengis.net/def/crs/OGC/1.3/CRS84</ows:SupportedCRS>");
            content.Should().Contain("<TopLeftCorner>-180 90</TopLeftCorner>");
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("GET /rest/services/{id}/ImageServer/WMTS")]
    [Operation(Operations.GetTile)]
    public async Task Wmts_GetTile_WorldCrs84QuadEnabled_RendersThroughSharedGridsetPipeline()
    {
        var store = CreateRasterStoreSubstitute();
        // Use ReturnsForAnyArgs (rather than per-argument matchers) so the by-value RasterTileWindow
        // struct parameter is matched cleanly; the actual window is inspected via ReceivedCalls.
        store.GetImageTileAsync(default, default, default, default, default)
            .ReturnsForAnyArgs(new RasterResult
            {
                Data = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A],
                ContentType = "image/png",
                Width = 256,
                Height = 256,
                Srid = 4326,
            });

        var fixture = await CreateFixtureAsync(store, enableWorldCrs84Quad: true);
        try
        {
            // WorldCRS84Quad level 0: 2 columns x 1 row. Tile (col 0, row 0) covers the western
            // hemisphere: lon [-180, 0], lat [-90, 90] in CRS84.
            var tileUrl = $"/rest/services/{TestLayerId}/ImageServer/WMTS?SERVICE=WMTS&REQUEST=GetTile&VERSION=1.0.0&LAYER={TestLayerId}&STYLE=default&FORMAT=image/png&TILEMATRIXSET={WorldCrs84Quad}&TILEMATRIX=0&TILEROW=0&TILECOL=0";
            using var response = await fixture.Client.GetAsync(tileUrl);

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            response.Content.Headers.ContentType?.MediaType.Should().Be("image/png");

            var windowCall = store.ReceivedCalls()
                .Single(c => string.Equals(c.GetMethodInfo().Name, "GetImageTileAsync", StringComparison.Ordinal)
                    && c.GetArguments().Length == 5);
            var window = (RasterTileWindow)windowCall.GetArguments()[2]!;
            window.Srid.Should().Be(4326);
            window.MinX.Should().BeApproximately(-180, 1e-9);
            window.MaxX.Should().BeApproximately(0, 1e-9);
            window.MinY.Should().BeApproximately(-90, 1e-9);
            window.MaxY.Should().BeApproximately(90, 1e-9);

            // Caching remains enabled: the second request must serve this fixture's bytes
            // without rendering again. The cold request above must still prove its window.
            var coldBytes = await response.Content.ReadAsByteArrayAsync();
            coldBytes.Should().Equal([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
            using var cachedResponse = await fixture.Client.GetAsync(tileUrl);
            cachedResponse.StatusCode.Should().Be(HttpStatusCode.OK);
            cachedResponse.Content.Headers.ContentType?.MediaType.Should().Be("image/png");
            (await cachedResponse.Content.ReadAsByteArrayAsync()).Should().Equal(coldBytes);
            store.ReceivedCalls().Count(c => string.Equals(c.GetMethodInfo().Name, "GetImageTileAsync", StringComparison.Ordinal)
                && c.GetArguments().Length == 5).Should().Be(1);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("GET /rest/services/{id}/ImageServer/WMTS")]
    [Operation(Operations.Identify)]
    public async Task Wmts_GetFeatureInfo_WorldCrs84QuadEnabled_ReturnsPixelValueInCrs84()
    {
        var store = CreateRasterStoreSubstitute();
        store.IdentifyAsync(
                Arg.Any<int>(), Arg.Any<long>(), Arg.Any<double>(), Arg.Any<double>(),
                Arg.Any<int?>(), Arg.Any<RasterIdentifyRendering?>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => new PixelValueResult
            {
                X = callInfo.ArgAt<double>(2),
                Y = callInfo.ArgAt<double>(3),
                Srid = callInfo.ArgAt<int?>(4) ?? 4326,
                BandValues = new Dictionary<int, object?> { [1] = 42 },
                HasData = true,
            });

        var fixture = await CreateFixtureAsync(store, enableWorldCrs84Quad: true);
        try
        {
            var response = await fixture.Client.GetAsync(
                $"/rest/services/{TestLayerId}/ImageServer/WMTS?SERVICE=WMTS&REQUEST=GetFeatureInfo&VERSION=1.0.0&LAYER={TestLayerId}&STYLE=default&FORMAT=image/png&TILEMATRIXSET={WorldCrs84Quad}&TILEMATRIX=0&TILEROW=0&TILECOL=0&I=128&J=128&INFOFORMAT=application/json");

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var root = json.RootElement;
            root.GetProperty("hasData").GetBoolean().Should().BeTrue();
            // Location reported in the gridset CRS (CRS84 / EPSG:4326), not Web Mercator.
            root.GetProperty("location").GetProperty("spatialReference").GetProperty("wkid").GetInt32().Should().Be(4326);
            var x = root.GetProperty("location").GetProperty("x").GetDouble();
            var y = root.GetProperty("location").GetProperty("y").GetDouble();
            x.Should().BeInRange(-180, 0);
            y.Should().BeInRange(-90, 90);
            root.GetProperty("bands")[0].GetProperty("value").GetInt32().Should().Be(42);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("GET /rest/services/{id}/ImageServer/WMTS")]
    [Operation(Operations.GetTile)]
    public async Task Wmts_GetTile_WorldCrs84QuadNotConfigured_ReturnsInvalidParameterValue()
    {
        var fixture = await CreateFixtureAsync(CreateRasterStoreSubstitute(), enableWorldCrs84Quad: false);
        try
        {
            var response = await fixture.Client.GetAsync(
                $"/rest/services/{TestLayerId}/ImageServer/WMTS?SERVICE=WMTS&REQUEST=GetTile&VERSION=1.0.0&LAYER={TestLayerId}&STYLE=default&FORMAT=image/png&TILEMATRIXSET={WorldCrs84Quad}&TILEMATRIX=0&TILEROW=0&TILECOL=0");

            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            var content = await response.Content.ReadAsStringAsync();
            content.Should().Contain("InvalidParameterValue");
            content.Should().Contain("Only TILEMATRIXSET=WebMercatorQuad is supported.");
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("GET /rest/services/{id}/ImageServer/WMTS")]
    [Operation(Operations.GetTile)]
    public async Task Wmts_GetTile_UnknownMatrixSet_ReturnsInvalidParameterValue()
    {
        var fixture = await CreateFixtureAsync(CreateRasterStoreSubstitute(), enableWorldCrs84Quad: true);
        try
        {
            var response = await fixture.Client.GetAsync(
                $"/rest/services/{TestLayerId}/ImageServer/WMTS?SERVICE=WMTS&REQUEST=GetTile&VERSION=1.0.0&LAYER={TestLayerId}&STYLE=default&FORMAT=image/png&TILEMATRIXSET=NotARealGrid&TILEMATRIX=0&TILEROW=0&TILECOL=0");

            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            var content = await response.Content.ReadAsStringAsync();
            content.Should().Contain("InvalidParameterValue");
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("GET /rest/services/{id}/ImageServer/WMTS")]
    [Operation(Operations.GetTile)]
    public async Task Wmts_GetTile_WorldCrs84QuadColumnOutOfBounds_ReturnsInvalidParameterValue()
    {
        var fixture = await CreateFixtureAsync(CreateRasterStoreSubstitute(), enableWorldCrs84Quad: true);
        try
        {
            // At level 0 WorldCRS84Quad has 2 columns (0..1); column 2 is out of bounds.
            var response = await fixture.Client.GetAsync(
                $"/rest/services/{TestLayerId}/ImageServer/WMTS?SERVICE=WMTS&REQUEST=GetTile&VERSION=1.0.0&LAYER={TestLayerId}&STYLE=default&FORMAT=image/png&TILEMATRIXSET={WorldCrs84Quad}&TILEMATRIX=0&TILEROW=0&TILECOL=2");

            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            var content = await response.Content.ReadAsStringAsync();
            content.Should().Contain("InvalidParameterValue");
            content.Should().Contain("TILECOL");
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("GET /rest/services/{id}/ImageServer/WMTS")]
    [Operation(Operations.GetTile)]
    public async Task Wmts_GetTile_WorldCrs84QuadRowOutOfBounds_ReturnsInvalidParameterValue()
    {
        var fixture = await CreateFixtureAsync(CreateRasterStoreSubstitute(), enableWorldCrs84Quad: true);
        try
        {
            // At level 0 WorldCRS84Quad has 1 row (0 only); row 1 is out of bounds.
            var response = await fixture.Client.GetAsync(
                $"/rest/services/{TestLayerId}/ImageServer/WMTS?SERVICE=WMTS&REQUEST=GetTile&VERSION=1.0.0&LAYER={TestLayerId}&STYLE=default&FORMAT=image/png&TILEMATRIXSET={WorldCrs84Quad}&TILEMATRIX=0&TILEROW=1&TILECOL=0");

            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            var content = await response.Content.ReadAsStringAsync();
            content.Should().Contain("InvalidParameterValue");
            content.Should().Contain("TILEROW");
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }
}
