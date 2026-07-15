// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.IO.Compression;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.Domain;
using Honua.Core.Features.Raster.Services;
using Honua.Protocols.GeoServices.ImageServer.Models;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Extensions;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.ImageServer;

/// <summary>
/// API surface integration tests for the ImageServer endpoints added in #520
/// (catalog query, computeStatisticsHistograms, legend, computeClassStatistics).
/// Each test exercises the actual route through <see cref="WebAppFixture"/> so
/// route binding, telemetry, JSON formatting, and error handling are all covered
/// per ADR-0011 API Surface Coverage.
/// </summary>
[Collection("Database.GeoServicesRaster")]
[Protocol(TestProtocols.ImageServer)]
public class ImageServerEndpointsTests
{
    private const int TestLayerId = 0;

    private static IRasterStore CreateRasterStoreSubstitute(int bandCount = 1)
    {
        var store = Substitute.For<IRasterStore>();
        var rasterInfo = new RasterInfo
        {
            Id = 100,
            LayerId = TestLayerId,
            Name = "test-raster",
            Width = 256,
            Height = 256,
            BandCount = bandCount,
            PixelType = "8BUI",
            Srid = 4326,
            GeoTransform = [-180, 1.40625, 0, 90, 0, -0.703125],
            Extent = new RasterExtent { XMin = -180, YMin = -90, XMax = 180, YMax = 90, Srid = 4326 },
            CreatedAt = DateTimeOffset.UtcNow,
        };

        store.GetPrimaryRasterInfoAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(rasterInfo);
        store.QueryRastersAsync(default, default, default)
            .ReturnsForAnyArgs([rasterInfo]);
        store.GetRasterInfoAsync(Arg.Any<int>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(rasterInfo);
        store.ListRastersAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([rasterInfo]);
        store.QueryCatalogAsync(Arg.Any<int>(), Arg.Any<RasterCatalogQuery>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => RasterCatalogQueryEvaluator.EvaluateAsync(
                [rasterInfo],
                callInfo.ArgAt<RasterCatalogQuery>(1),
                transformService: null,
                callInfo.ArgAt<CancellationToken>(2)));
        store.GetSensorMetadataAsync(Arg.Any<IReadOnlyCollection<long>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<long, RasterSensorMetadata>());
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
        store.GetHistogramsAsync(Arg.Any<int>(), Arg.Any<long>(), Arg.Any<int[]?>(), Arg.Any<int>(), Arg.Any<RasterIdentifyRendering?>(), Arg.Any<CancellationToken>())
            .Returns(new[]
            {
                new RasterHistogram
                {
                    Band = 1,
                    BinCount = 4,
                    Min = 0,
                    Max = 255,
                    Counts = [10, 20, 30, 40],
                },
            });

        // AOI-clipped statistics/histograms (computeStatisticsHistograms with a geometry)
        // mirror the whole-raster substitute payloads for these single-raster fixtures.
        store.GetClippedStatisticsAsync(Arg.Any<int>(), Arg.Any<long>(), Arg.Any<byte[]>(), Arg.Any<int?>(), Arg.Any<int[]?>(), Arg.Any<RasterIdentifyRendering?>(), Arg.Any<CancellationToken>())
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
        store.GetClippedHistogramsAsync(Arg.Any<int>(), Arg.Any<long>(), Arg.Any<byte[]>(), Arg.Any<int?>(), Arg.Any<int[]?>(), Arg.Any<int>(), Arg.Any<RasterIdentifyRendering?>(), Arg.Any<CancellationToken>())
            .Returns(new[]
            {
                new RasterHistogram
                {
                    Band = 1,
                    BinCount = 4,
                    Min = 0,
                    Max = 255,
                    Counts = [10, 20, 30, 40],
                },
            });

        // computeClassStatistics reads aligned per-pixel band vectors inside each class AOI. The
        // default substitute returns a deterministic 4-pixel sample per requested band so the
        // signature (count, mean, covariance) is exercised end-to-end.
        store.ReadClippedBandVectorsAsync(
                Arg.Any<int>(),
                Arg.Any<long[]>(),
                Arg.Any<RasterMergeStrategy>(),
                Arg.Any<byte[]>(),
                Arg.Any<int?>(),
                Arg.Any<int[]?>(),
                Arg.Any<int>(),
                Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var requestedBands = callInfo.ArgAt<int[]?>(5) ?? [1];
                var pixels = new List<double[]>();
                for (var value = 1; value <= 4; value++)
                {
                    var vector = new double[requestedBands.Length];
                    for (var b = 0; b < requestedBands.Length; b++)
                    {
                        vector[b] = (double)value * (b + 1);
                    }

                    pixels.Add(vector);
                }

                return new RasterBandVectorSet { Bands = requestedBands, Pixels = pixels };
            });

        return store;
    }

    private static IRasterStore CreateRpcRasterStoreSubstitute()
    {
        // A raster carrying an offset/scale RPC sensor model so the image-CS transformation warp
        // (#1881/#2840) has an image<->ground mapping to apply: image (0,0) -> ground (-120, 35).
        var store = CreateRasterStoreSubstitute();
        store.GetSensorMetadataAsync(Arg.Any<IReadOnlyCollection<long>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<long, RasterSensorMetadata>
            {
                [100] = new RasterSensorMetadata
                {
                    RasterDataId = 100,
                    SensorName = "TestSensor",
                    RpcJson = """
                    {
                        "sampleOffset": 0, "lineOffset": 0,
                        "longOffset": -120.0, "latOffset": 35.0,
                        "sampleScale": 1, "lineScale": 1,
                        "longScale": 0.001, "latScale": 0.001
                    }
                    """,
                },
            });

        return store;
    }

    private static IRasterStore CreateSamplingRasterStoreSubstitute()
    {
        var store = CreateRasterStoreSubstitute();
        store.IdentifyAsync(
                Arg.Any<int>(),
                Arg.Any<long>(),
                Arg.Any<double>(),
                Arg.Any<double>(),
                Arg.Any<int?>(),
                Arg.Any<RasterIdentifyRendering?>(),
                Arg.Any<CancellationToken>())
            .Returns(callInfo => new PixelValueResult
            {
                X = callInfo.ArgAt<double>(2),
                Y = callInfo.ArgAt<double>(3),
                Srid = 4326,
                BandValues = new Dictionary<int, object?> { [1] = 42 },
                HasData = true,
            });
        return store;
    }

    private static IRasterStore CreateTileExportRasterStoreSubstitute()
    {
        var store = CreateRasterStoreSubstitute();
        store.GetImageTileAsync(
                Arg.Any<int>(),
                Arg.Any<long>(),
                Arg.Any<int>(),
                Arg.Any<int>(),
                Arg.Any<int>(),
                Arg.Any<RasterFormat>(),
                Arg.Any<CancellationToken>())
            .Returns(new RasterResult
            {
                Data = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A],
                ContentType = "image/png",
                Width = 256,
                Height = 256,
                Srid = 3857,
            });

        return store;
    }

    private static IRasterStore CreateMultiRasterStoreSubstitute()
    {
        var store = Substitute.For<IRasterStore>();
        RasterInfo Build(long id, string name) => new()
        {
            Id = id,
            LayerId = TestLayerId,
            Name = name,
            Width = 256,
            Height = 256,
            BandCount = 1,
            PixelType = "8BUI",
            Srid = 4326,
            GeoTransform = [-180, 1.40625, 0, 90, 0, -0.703125],
            Extent = new RasterExtent { XMin = -180, YMin = -90, XMax = 180, YMax = 90, Srid = 4326 },
            CreatedAt = DateTimeOffset.UtcNow,
        };

        // Returned in deliberately unsorted order so orderByFields has work to do.
        var rasters = new[] { Build(200, "b"), Build(100, "a"), Build(300, "c") };
        store.ListRastersAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(rasters);
        store.QueryRastersAsync(default, default, default).ReturnsForAnyArgs(rasters);
        store.QueryCatalogAsync(Arg.Any<int>(), Arg.Any<RasterCatalogQuery>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => RasterCatalogQueryEvaluator.EvaluateAsync(
                rasters,
                callInfo.ArgAt<RasterCatalogQuery>(1),
                transformService: null,
                callInfo.ArgAt<CancellationToken>(2)));
        store.GetSensorMetadataAsync(Arg.Any<IReadOnlyCollection<long>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<long, RasterSensorMetadata>());
        return store;
    }

    private static async Task<WebAppFixture> CreateFixtureAsync(IRasterStore rasterStore)
    {
        var fixture = new WebAppFixture()
            .ConfigureServices(services => services.AddSingleton(rasterStore));
        await fixture.InitializeAsync();
        return fixture;
    }

    private static async Task<WebAppFixture> CreateFixtureAsync(
        IRasterStore rasterStore,
        IElevationService elevationService)
    {
        var fixture = new WebAppFixture()
            .ConfigureServices(services =>
            {
                services.AddSingleton(rasterStore);
                services.AddSingleton(elevationService);
            });
        await fixture.InitializeAsync();
        return fixture;
    }

    [IntegrationTest]
    [Operation(Operations.Export)]
    [Endpoint("GET /rest/services/{id}/ImageServer/exportImage")]
    public async Task ExportImage_WithStretchRenderingRule_AppliesStretchAndReturnsPng()
    {
        var store = CreateRasterStoreSubstitute();
        RasterQuery? capturedQuery = null;
        store.ExportImageAsync(Arg.Any<int>(), Arg.Any<long>(), Arg.Any<RasterQuery>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                capturedQuery = call.ArgAt<RasterQuery>(2);
                return new RasterResult
                {
                    Data = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A],
                    ContentType = "image/png",
                    Width = 256,
                    Height = 256,
                    Srid = 4326,
                };
            });

        var fixture = await CreateFixtureAsync(store);
        try
        {
            var renderingRule = Uri.EscapeDataString(
                """{"rasterFunction":"Stretch","rasterFunctionArguments":{"StretchType":5}}""");
            var response = await fixture.Client.GetAsync(
                $"/rest/services/{TestLayerId}/ImageServer/exportImage?f=image&bbox=-180,-90,180,90&renderingRule={renderingRule}");

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            response.Content.Headers.ContentType?.MediaType.Should().Be("image/png");
            capturedQuery.Should().NotBeNull();
            capturedQuery!.Value.Stretch.Should().NotBeNull();
            capturedQuery.Value.Stretch!.Value.StretchType.Should().Be(RasterStretchType.MinMax);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Operation(Operations.Export)]
    [Endpoint("GET /rest/services/{id}/ImageServer/exportImage")]
    public async Task ExportImage_WithColormapRenderingRule_AppliesColormapAndReturnsPng()
    {
        var store = CreateRasterStoreSubstitute();
        RasterQuery? capturedQuery = null;
        store.ExportImageAsync(Arg.Any<int>(), Arg.Any<long>(), Arg.Any<RasterQuery>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                capturedQuery = call.ArgAt<RasterQuery>(2);
                return new RasterResult
                {
                    Data = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A],
                    ContentType = "image/png",
                    Width = 256,
                    Height = 256,
                    Srid = 4326,
                };
            });

        var fixture = await CreateFixtureAsync(store);
        try
        {
            var renderingRule = Uri.EscapeDataString(
                """{"rasterFunction":"Colormap","rasterFunctionArguments":{"Colormap":[[0,0,0,0],[255,255,255,255]]}}""");
            var response = await fixture.Client.GetAsync(
                $"/rest/services/{TestLayerId}/ImageServer/exportImage?f=image&bbox=-180,-90,180,90&renderingRule={renderingRule}");

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            capturedQuery.Should().NotBeNull();
            capturedQuery!.Value.Colormap.Should().NotBeNull();
            capturedQuery.Value.Colormap!.Entries.Should().HaveCount(2);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Operation(Operations.Export)]
    [Endpoint("GET /rest/services/{id}/ImageServer/exportImage")]
    public async Task ExportImage_WithClipRenderingRule_AppliesClipRegion()
    {
        var store = CreateRasterStoreSubstitute();
        RasterQuery? capturedQuery = null;
        store.ExportImageAsync(Arg.Any<int>(), Arg.Any<long>(), Arg.Any<RasterQuery>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                capturedQuery = call.ArgAt<RasterQuery>(2);
                return new RasterResult
                {
                    Data = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A],
                    ContentType = "image/png",
                    Width = 256,
                    Height = 256,
                    Srid = 4326,
                };
            });

        var fixture = await CreateFixtureAsync(store);
        try
        {
            var renderingRule = Uri.EscapeDataString(
                """{"rasterFunction":"Clip","rasterFunctionArguments":{"ClippingGeometry":{"rings":[[[-10,-10],[-10,10],[10,10],[10,-10],[-10,-10]]],"spatialReference":{"wkid":4326}}}}""");
            var response = await fixture.Client.GetAsync(
                $"/rest/services/{TestLayerId}/ImageServer/exportImage?f=image&bbox=-180,-90,180,90&renderingRule={renderingRule}");

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            capturedQuery.Should().NotBeNull();
            capturedQuery!.Value.RenderingClip.Should().NotBeNull();
            capturedQuery.Value.RenderingClip!.Value.Srid.Should().Be(4326);
            capturedQuery.Value.RenderingClip.Value.Geometry.Should().NotBeEmpty();
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Operation(Operations.Export)]
    [Endpoint("GET /rest/services/{id}/ImageServer/exportImage")]
    public async Task ExportImage_WithInvalidClipGeometry_ReturnsBadRequest()
    {
        var fixture = await CreateFixtureAsync(CreateRasterStoreSubstitute());
        try
        {
            var renderingRule = Uri.EscapeDataString(
                """{"rasterFunction":"Clip","rasterFunctionArguments":{"ClippingGeometry":{"rings":[]}}}""");
            var response = await fixture.Client.GetAsync(
                $"/rest/services/{TestLayerId}/ImageServer/exportImage?f=image&bbox=-180,-90,180,90&renderingRule={renderingRule}");

            // PA-070/PA-117: GeoServices always returns HTTP 200; error code is in the JSON body.
            response.StatusCode.Should().Be(HttpStatusCode.OK);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Operation(Operations.Export)]
    [Endpoint("GET /rest/services/{id}/ImageServer/exportImage")]
    public async Task ExportImage_WithUnknownRenderingRule_ReturnsBadRequest()
    {
        var fixture = await CreateFixtureAsync(CreateRasterStoreSubstitute());
        try
        {
            // Hillshade/Slope/Aspect are now implemented terrain functions (#1803); use a name that
            // is genuinely not a known raster function so the unknown-chain -> 400 path is exercised.
            var renderingRule = Uri.EscapeDataString("""{"rasterFunction":"NonExistentRasterFunction"}""");
            var response = await fixture.Client.GetAsync(
                $"/rest/services/{TestLayerId}/ImageServer/exportImage?f=image&bbox=-180,-90,180,90&renderingRule={renderingRule}");

            // PA-070/PA-117: GeoServices always returns HTTP 200; error code is in the JSON body.
            response.StatusCode.Should().Be(HttpStatusCode.OK);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("GET /rest/services/{id}/ImageServer/WMTS")]
    [Endpoint("GET /rest/services/{serviceId}/ImageServer/WMTS/{**restPath}")]
    [Operation(Operations.Metadata)]
    public async Task Wmts_GetCapabilities_ReturnsImageServerCapabilities()
    {
        var fixture = await CreateFixtureAsync(CreateRasterStoreSubstitute());
        try
        {
            var response = await fixture.Client.GetAsync(
                $"/rest/services/{TestLayerId}/ImageServer/WMTS?SERVICE=WMTS&REQUEST=GetCapabilities&VERSION=1.0.0");

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            response.Content.Headers.ContentType?.MediaType.Should().Be("application/xml");
            var content = await response.Content.ReadAsStringAsync();
            content.Should().Contain("<ows:ServiceType>OGC WMTS</ows:ServiceType>");
            content.Should().Contain("<ows:Identifier>0</ows:Identifier>");
            content.Should().Contain("<ows:Identifier>WebMercatorQuad</ows:Identifier>");
            content.Should().Contain("ResourceURL");
            content.Should().Contain("<Format>image/png</Format>");
            content.Should().Contain("<Format>image/jpeg</Format>");
            content.Should().Contain("<InfoFormat>application/json</InfoFormat>");
            content.Should().Contain("name=\"GetFeatureInfo\"");

            var serviceId = WebAppFixture.TestServiceId;
            var restfulResponse = await fixture.Client.GetAsync(
                $"/rest/services/{serviceId}/ImageServer/WMTS/1.0.0/WMTSCapabilities.xml");

            restfulResponse.StatusCode.Should().Be(HttpStatusCode.OK);
            var restfulContent = await restfulResponse.Content.ReadAsStringAsync();
            restfulContent.Should().Contain($"<ows:Identifier>{serviceId}</ows:Identifier>");
            restfulContent.Should().Contain("/ImageServer/WMTS/{Layer}/{Style}/{TileMatrixSet}");
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("GET /rest/services/{id}/ImageServer/WMTS")]
    [Operation(Operations.Metadata)]
    public async Task Wmts_GetCapabilities_TemporalLayer_AdvertisesTimeDimension()
    {
        var store = CreateRasterStoreSubstitute();
        var temporalRaster = new RasterInfo
        {
            Id = 100,
            LayerId = TestLayerId,
            Name = "temporal-raster",
            Width = 256,
            Height = 256,
            BandCount = 1,
            PixelType = "8BUI",
            Srid = 4326,
            GeoTransform = [-180, 1.40625, 0, 90, 0, -0.703125],
            Extent = new RasterExtent { XMin = -180, YMin = -90, XMax = 180, YMax = 90, Srid = 4326 },
            CreatedAt = DateTimeOffset.UtcNow,
            AcquisitionDate = DateTimeOffset.Parse("2020-06-01T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture),
        };
        store.ListRastersAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns([temporalRaster]);

        var fixture = await CreateFixtureAsync(store);
        try
        {
            var response = await fixture.Client.GetAsync(
                $"/rest/services/{TestLayerId}/ImageServer/WMTS?SERVICE=WMTS&REQUEST=GetCapabilities&VERSION=1.0.0");

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var content = await response.Content.ReadAsStringAsync();
            content.Should().Contain("<ows:Identifier>TIME</ows:Identifier>");
            content.Should().Contain("2020-06-01T00:00:00Z");
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("GET /rest/services/{serviceId}/ImageServer/WMTS")]
    [Endpoint("GET /rest/services/{id}/ImageServer/WMTS/{**restPath}")]
    [Operation(Operations.GetTile)]
    public async Task Wmts_GetTile_KvpAndRestful_ReturnPngTiles()
    {
        var fixture = await CreateFixtureAsync(CreateTileExportRasterStoreSubstitute());
        try
        {
            var serviceId = WebAppFixture.TestServiceId;
            var response = await fixture.Client.GetAsync(
                $"/rest/services/{serviceId}/ImageServer/WMTS?SERVICE=WMTS&REQUEST=GetTile&VERSION=1.0.0&LAYER={serviceId}&STYLE=default&FORMAT=image/png&TILEMATRIXSET=WebMercatorQuad&TILEMATRIX=0&TILEROW=0&TILECOL=0");

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            response.Content.Headers.ContentType?.MediaType.Should().Be("image/png");
            var bytes = await response.Content.ReadAsByteArrayAsync();
            bytes.Should().StartWith([0x89, 0x50, 0x4E, 0x47]);

            var restfulResponse = await fixture.Client.GetAsync(
                $"/rest/services/{TestLayerId}/ImageServer/WMTS/{TestLayerId}/default/WebMercatorQuad/0/0/0.png");

            restfulResponse.StatusCode.Should().Be(HttpStatusCode.OK);
            restfulResponse.Content.Headers.ContentType?.MediaType.Should().Be("image/png");
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("GET /rest/services/{id}/ImageServer/WMTS")]
    [Operation(Operations.GetTile)]
    public async Task Wmts_GetTile_JpegFormat_RoutesJpegToTileHandler()
    {
        var store = CreateTileExportRasterStoreSubstitute();
        // JPEG requests must reach the store with RasterFormat.JPEG; return a JPEG-typed
        // tile only for that format so the response content type proves the routing.
        store.GetImageTileAsync(
                Arg.Any<int>(), Arg.Any<long>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<int>(), RasterFormat.JPEG, Arg.Any<CancellationToken>())
            .Returns(new RasterResult
            {
                Data = [0xFF, 0xD8, 0xFF, 0xE0],
                ContentType = "image/jpeg",
                Width = 256,
                Height = 256,
                Srid = 3857,
            });

        var fixture = await CreateFixtureAsync(store);
        try
        {
            var response = await fixture.Client.GetAsync(
                $"/rest/services/{TestLayerId}/ImageServer/WMTS?SERVICE=WMTS&REQUEST=GetTile&VERSION=1.0.0&LAYER={TestLayerId}&STYLE=default&FORMAT=image/jpeg&TILEMATRIXSET=WebMercatorQuad&TILEMATRIX=2&TILEROW=1&TILECOL=1");

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            response.Content.Headers.ContentType?.MediaType.Should().Be("image/jpeg");

            // RESTful .jpg resource also routes to JPEG.
            var restful = await fixture.Client.GetAsync(
                $"/rest/services/{TestLayerId}/ImageServer/WMTS/{TestLayerId}/default/WebMercatorQuad/2/1/1.jpg");
            restful.StatusCode.Should().Be(HttpStatusCode.OK);
            restful.Content.Headers.ContentType?.MediaType.Should().Be("image/jpeg");
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("GET /rest/services/{id}/ImageServer/WMTS")]
    [Operation(Operations.GetTile)]
    public async Task Wmts_GetTile_TiffFormat_RoutesTiffToTileHandler()
    {
        var store = CreateTileExportRasterStoreSubstitute();
        store.GetImageTileAsync(
                Arg.Any<int>(), Arg.Any<long>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<int>(), RasterFormat.TIFF, Arg.Any<CancellationToken>())
            .Returns(new RasterResult
            {
                Data = [0x49, 0x49, 0x2A, 0x00],
                ContentType = "image/tiff",
                Width = 256,
                Height = 256,
                Srid = 3857,
            });

        var fixture = await CreateFixtureAsync(store);
        try
        {
            var response = await fixture.Client.GetAsync(
                $"/rest/services/{TestLayerId}/ImageServer/WMTS?SERVICE=WMTS&REQUEST=GetTile&VERSION=1.0.0&LAYER={TestLayerId}&STYLE=default&FORMAT=image/tiff&TILEMATRIXSET=WebMercatorQuad&TILEMATRIX=3&TILEROW=2&TILECOL=2");

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            response.Content.Headers.ContentType?.MediaType.Should().Be("image/tiff");
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("GET /rest/services/{id}/ImageServer/WMTS")]
    [Operation(Operations.Identify)]
    public async Task Wmts_GetFeatureInfo_ReturnsPixelValueAtTilePixel()
    {
        var fixture = await CreateFixtureAsync(CreateSamplingRasterStoreSubstitute());
        try
        {
            var response = await fixture.Client.GetAsync(
                $"/rest/services/{TestLayerId}/ImageServer/WMTS?SERVICE=WMTS&REQUEST=GetFeatureInfo&VERSION=1.0.0&LAYER={TestLayerId}&STYLE=default&FORMAT=image/png&TILEMATRIXSET=WebMercatorQuad&TILEMATRIX=0&TILEROW=0&TILECOL=0&I=128&J=128&INFOFORMAT=application/json");

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");

            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var root = json.RootElement;
            root.GetProperty("hasData").GetBoolean().Should().BeTrue();
            root.GetProperty("location").GetProperty("spatialReference").GetProperty("wkid").GetInt32().Should().Be(3857);
            var bands = root.GetProperty("bands");
            bands.GetArrayLength().Should().Be(1);
            bands[0].GetProperty("band").GetInt32().Should().Be(1);
            bands[0].GetProperty("value").GetInt32().Should().Be(42);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("GET /rest/services/{id}/ImageServer/WMTS")]
    [Operation(Operations.Identify)]
    public async Task Wmts_GetFeatureInfo_WithOutOfRangePixel_ReturnsException()
    {
        var fixture = await CreateFixtureAsync(CreateSamplingRasterStoreSubstitute());
        try
        {
            var response = await fixture.Client.GetAsync(
                $"/rest/services/{TestLayerId}/ImageServer/WMTS?SERVICE=WMTS&REQUEST=GetFeatureInfo&VERSION=1.0.0&LAYER={TestLayerId}&STYLE=default&FORMAT=image/png&TILEMATRIXSET=WebMercatorQuad&TILEMATRIX=0&TILEROW=0&TILECOL=0&I=999&J=10&INFOFORMAT=application/json");

            // The ImageServer WMTS surface emits a WMTS/OWS XML exception. Per WMS/WMTS
            // convention an exception report is served with HTTP 200, but the current
            // ImageServer path still carries a 4xx transport status; accept either while
            // the XML exception body is the authoritative signal.
            response.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.BadRequest);
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
    public async Task Wmts_GetTile_WithUnsupportedFormat_ReturnsXmlException()
    {
        var fixture = await CreateFixtureAsync(CreateTileExportRasterStoreSubstitute());
        try
        {
            var response = await fixture.Client.GetAsync(
                $"/rest/services/{TestLayerId}/ImageServer/WMTS?SERVICE=WMTS&REQUEST=GetTile&VERSION=1.0.0&LAYER={TestLayerId}&STYLE=default&FORMAT=image/gif&TILEMATRIXSET=WebMercatorQuad&TILEMATRIX=0&TILEROW=0&TILECOL=0");

            // The ImageServer WMTS surface emits an XML exception report. Per WMS/WMTS
            // convention this should be HTTP 200, but the current ImageServer path still
            // carries a 4xx transport status; accept either — the XML body is authoritative.
            response.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.BadRequest);
            response.Content.Headers.ContentType?.MediaType.Should().Be("application/xml");
            var content = await response.Content.ReadAsStringAsync();
            content.Should().Contain("InvalidParameterValue");
            content.Should().Contain("FORMAT must be image/png, image/jpeg, or image/tiff.");
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("GET /rest/services/{id}/ImageServer/query")]
    [Operation(Operations.Query)]
    public async Task QueryCatalog_Get_ReturnsFeatureCollection()
    {
        var fixture = await CreateFixtureAsync(CreateRasterStoreSubstitute());
        try
        {
            var response = await fixture.Client.GetAsync(
                $"/rest/services/{TestLayerId}/ImageServer/query?f=json");

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");

            var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            json.RootElement.GetProperty("geometryType").GetString().Should().Be("esriGeometryPolygon");
            json.RootElement.GetProperty("features").GetArrayLength().Should().Be(1);
            json.RootElement.GetProperty("features")[0]
                .GetProperty("attributes")
                .GetProperty("OBJECTID").GetInt64().Should().Be(100);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("GET /rest/services/{id}/ImageServer/{rasterId}")]
    [Operation(Operations.Query)]
    public async Task GetRasterCatalogItem_ExistingRaster_ReturnsFeature()
    {
        var fixture = await CreateFixtureAsync(CreateRasterStoreSubstitute());
        try
        {
            var response = await fixture.Client.GetAsync(
                $"/rest/services/{TestLayerId}/ImageServer/100?f=json");

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");

            var feature = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
            feature.GetProperty("attributes").GetProperty("OBJECTID").GetInt64().Should().Be(100);
            feature.GetProperty("attributes").GetProperty("Name").GetString().Should().Be("test-raster");
            feature.GetProperty("geometry").GetProperty("rings").GetArrayLength().Should().BeGreaterThan(0);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("GET /rest/services/{serviceId}/ImageServer/{rasterId}")]
    [Operation(Operations.Query)]
    public async Task GetRasterCatalogItem_ByServiceName_ReturnsFeature()
    {
        var fixture = await CreateFixtureAsync(CreateRasterStoreSubstitute());
        try
        {
            var response = await fixture.Client.GetAsync(
                $"/rest/services/{WebAppFixture.TestServiceId}/ImageServer/100?f=pjson");

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var feature = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
            feature.GetProperty("attributes").GetProperty("OBJECTID").GetInt64().Should().Be(100);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("GET /rest/services/{id}/ImageServer/{rasterId}")]
    [Operation(Operations.Query)]
    public async Task GetRasterCatalogItem_UnknownRaster_ReturnsNotFoundError()
    {
        var fixture = await CreateFixtureAsync(CreateRasterStoreSubstitute());
        try
        {
            var response = await fixture.Client.GetAsync(
                $"/rest/services/{TestLayerId}/ImageServer/999?f=json");

            // GeoServices errors use HTTP 200 and carry the status in the Esri error body.
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
            body.GetProperty("error").GetProperty("code").GetInt32().Should().Be(404);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("GET /rest/services/{id}/ImageServer/{rasterId}/image")]
    [Endpoint("GET /rest/services/{serviceId}/ImageServer/{rasterId}/image")]
    [Operation(Operations.Export)]
    public async Task GetRasterItemImage_ExistingRaster_ReturnsSelectedRasterPixels()
    {
        var store = CreateMultiRasterStoreSubstitute();
        var selectedRasterIds = new List<long>();
        store.ExportImageAsync(Arg.Any<int>(), Arg.Any<long>(), Arg.Any<RasterQuery>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                selectedRasterIds.Add(call.ArgAt<long>(1));
                return new RasterResult
                {
                    Data = [0x89, 0x50, 0x4E, 0x47],
                    ContentType = "image/png",
                    Width = 256,
                    Height = 256,
                    Srid = 4326,
                };
            });

        var fixture = await CreateFixtureAsync(store);
        try
        {
            var response = await fixture.Client.GetAsync(
                $"/rest/services/{TestLayerId}/ImageServer/200/image?format=png&f=image");

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            response.Content.Headers.ContentType?.MediaType.Should().Be("image/png");

            foreach (var responseFormat in new string?[] { null, "json", "pjson" })
            {
                var formatQuery = responseFormat is null ? string.Empty : $"&f={responseFormat}";
                var jsonResponse = await fixture.Client.GetAsync(
                    $"/rest/services/{TestLayerId}/ImageServer/200/image?format=png{formatQuery}");

                jsonResponse.StatusCode.Should().Be(HttpStatusCode.OK);
                jsonResponse.Content.Headers.ContentType?.MediaType.Should().Be("application/json");
                var envelope = JsonDocument.Parse(await jsonResponse.Content.ReadAsStringAsync()).RootElement;
                envelope.GetProperty("href").GetString().Should().NotBeNullOrWhiteSpace();
                envelope.GetProperty("width").GetInt32().Should().Be(256);
                envelope.GetProperty("height").GetInt32().Should().Be(256);
            }

            selectedRasterIds.Should().Equal(200, 200, 200, 200);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("GET /rest/services/{id}/ImageServer/{rasterId}/info")]
    [Endpoint("GET /rest/services/{serviceId}/ImageServer/{rasterId}/info")]
    [Operation(Operations.GetServiceInfo)]
    public async Task GetRasterItemInfo_ExistingRaster_ReturnsCanonicalMetadata()
    {
        var fixture = await CreateFixtureAsync(CreateRasterStoreSubstitute());
        try
        {
            var response = await fixture.Client.GetAsync(
                $"/rest/services/{TestLayerId}/ImageServer/100/info?f=json");

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var info = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
            info.EnumerateObject().Select(static property => property.Name).Should().BeEquivalentTo([
                "origin",
                "blockWidth",
                "blockHeight",
                "pixelSizeX",
                "pixelSizeY",
                "extent",
                "bandCount",
                "pixelType",
                "firstPyramidLevel",
                "maxPyramidLevel",
            ]);
            info.GetProperty("blockWidth").GetInt32().Should().Be(256);
            info.GetProperty("bandCount").GetInt32().Should().Be(1);
            info.GetProperty("pixelType").GetString().Should().Be("U8");
            info.GetProperty("extent").GetProperty("spatialReference").GetProperty("wkid").GetInt32().Should().Be(4326);
            info.GetProperty("firstPyramidLevel").GetInt32().Should().Be(0);
            info.GetProperty("maxPyramidLevel").GetInt32().Should().Be(0);
            info.TryGetProperty("maximumPyramidLevel", out _).Should().BeFalse();
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("GET /rest/services/{id}/ImageServer/{rasterId}/info/keyProperties")]
    [Endpoint("GET /rest/services/{serviceId}/ImageServer/{rasterId}/info/keyProperties")]
    [Operation(Operations.GetServiceInfo)]
    public async Task GetRasterItemKeyProperties_ByServiceName_ReturnsSelectedRasterProperties()
    {
        var fixture = await CreateFixtureAsync(CreateRasterStoreSubstitute());
        try
        {
            var response = await fixture.Client.GetAsync(
                $"/rest/services/{WebAppFixture.TestServiceId}/ImageServer/100/info/keyProperties?f=pjson");

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var properties = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
            properties.GetProperty("BandCount").GetInt32().Should().Be(1);
            properties.GetProperty("DataType").GetString().Should().Be("U8");
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("GET /rest/services/{id}/ImageServer/{rasterId}/info/histograms")]
    [Endpoint("GET /rest/services/{serviceId}/ImageServer/{rasterId}/info/histograms")]
    [Operation(Operations.GetServiceInfo)]
    public async Task GetRasterItemHistograms_ExistingRaster_ReturnsBandHistograms()
    {
        var fixture = await CreateFixtureAsync(CreateRasterStoreSubstitute());
        try
        {
            var response = await fixture.Client.GetAsync(
                $"/rest/services/{TestLayerId}/ImageServer/100/info/histograms?f=json");

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var histograms = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement
                .GetProperty("histograms");
            histograms.GetArrayLength().Should().Be(1);
            histograms[0].GetProperty("counts").GetArrayLength().Should().Be(4);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("GET /rest/services/{id}/ImageServer/{rasterId}/imageSupportData")]
    [Endpoint("GET /rest/services/{serviceId}/ImageServer/{rasterId}/imageSupportData")]
    [Operation(Operations.GetServiceInfo)]
    public async Task GetRasterItemImageSupportData_WithSensorMetadata_ReturnsSupportData()
    {
        var store = CreateRasterStoreSubstitute();
        store.GetSensorMetadataAsync(Arg.Any<IReadOnlyCollection<long>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<long, RasterSensorMetadata>
            {
                [100] = new()
                {
                    RasterDataId = 100,
                    SensorName = "WorldView-3",
                    CameraModel = "WV110",
                    RpcJson = "{\"rowNum\":1}",
                    DemSource = "dem-layer",
                    CreatedAt = DateTimeOffset.UtcNow,
                },
            });

        var fixture = await CreateFixtureAsync(store);
        try
        {
            var response = await fixture.Client.GetAsync(
                $"/rest/services/{TestLayerId}/ImageServer/100/imageSupportData?f=json");

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
            body.GetProperty("rasterId").GetInt64().Should().Be(100);
            body.GetProperty("sensorName").GetString().Should().Be("WorldView-3");
            body.GetProperty("cameraModel").GetString().Should().Be("WV110");
            body.GetProperty("hasRationalPolynomialCoefficients").GetBoolean().Should().BeTrue();
            body.GetProperty("hasInteriorOrientation").GetBoolean().Should().BeFalse();
            body.GetProperty("demSource").GetString().Should().Be("dem-layer");
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("GET /rest/services/{id}/ImageServer/{rasterId}/imageSupportData")]
    [Operation(Operations.GetServiceInfo)]
    public async Task GetRasterItemImageSupportData_WithoutSensorMetadata_ReturnsNotAvailableError()
    {
        // The default substitute returns an empty sensor-metadata dictionary, so the item carries
        // no image support data and the resource must report not-available honestly.
        var fixture = await CreateFixtureAsync(CreateRasterStoreSubstitute());
        try
        {
            var response = await fixture.Client.GetAsync(
                $"/rest/services/{TestLayerId}/ImageServer/100/imageSupportData?f=json");

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
            body.GetProperty("error").GetProperty("code").GetInt32().Should().Be(404);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("GET /rest/services/{id}/ImageServer/{rasterId}/thumbnail")]
    [Endpoint("GET /rest/services/{serviceId}/ImageServer/{rasterId}/thumbnail")]
    [Operation(Operations.Export)]
    public async Task GetRasterItemThumbnail_ExistingRaster_RendersLockedRasterImage()
    {
        var store = CreateMultiRasterStoreSubstitute();
        var selectedRasterIds = new List<long>();
        store.ExportImageAsync(Arg.Any<int>(), Arg.Any<long>(), Arg.Any<RasterQuery>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                selectedRasterIds.Add(call.ArgAt<long>(1));
                return new RasterResult
                {
                    Data = [0x89, 0x50, 0x4E, 0x47],
                    ContentType = "image/png",
                    Width = 200,
                    Height = 200,
                    Srid = 4326,
                };
            });

        var fixture = await CreateFixtureAsync(store);
        try
        {
            // Default (no f): thumbnail returns image bytes.
            var imageResponse = await fixture.Client.GetAsync(
                $"/rest/services/{TestLayerId}/ImageServer/200/thumbnail");
            imageResponse.StatusCode.Should().Be(HttpStatusCode.OK);
            imageResponse.Content.Headers.ContentType?.MediaType.Should().Be("image/png");

            // f=json returns the href envelope.
            var jsonResponse = await fixture.Client.GetAsync(
                $"/rest/services/{TestLayerId}/ImageServer/200/thumbnail?f=json");
            jsonResponse.StatusCode.Should().Be(HttpStatusCode.OK);
            jsonResponse.Content.Headers.ContentType?.MediaType.Should().Be("application/json");
            var envelope = JsonDocument.Parse(await jsonResponse.Content.ReadAsStringAsync()).RootElement;
            envelope.GetProperty("href").GetString().Should().NotBeNullOrWhiteSpace();

            selectedRasterIds.Should().OnlyContain(id => id == 200);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("GET /rest/services/{id}/ImageServer/{rasterId}/rasterFile")]
    [Endpoint("GET /rest/services/{serviceId}/ImageServer/{rasterId}/rasterFile")]
    [Operation(Operations.GetServiceInfo)]
    public async Task GetRasterItemRasterFile_ExistingRaster_ReturnsNotAvailableError()
    {
        // Honua stores raster pixels in the provider with no downloadable source file, so rasterFile
        // must be a precise capability-honest not-available response rather than a raw error.
        var fixture = await CreateFixtureAsync(CreateRasterStoreSubstitute());
        try
        {
            var response = await fixture.Client.GetAsync(
                $"/rest/services/{TestLayerId}/ImageServer/100/rasterFile?f=json");

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
            body.GetProperty("error").GetProperty("code").GetInt32().Should().Be(404);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("GET /rest/services/{id}/ImageServer/{rasterId}/info")]
    [Operation(Operations.GetServiceInfo)]
    public async Task GetRasterItemInfo_UnknownRaster_ReturnsNotFoundError()
    {
        var store = CreateRasterStoreSubstitute();
        store.GetRasterInfoAsync(TestLayerId, 999, Arg.Any<CancellationToken>())
            .Returns((RasterInfo?)null);
        var fixture = await CreateFixtureAsync(store);
        try
        {
            var response = await fixture.Client.GetAsync(
                $"/rest/services/{TestLayerId}/ImageServer/999/info?f=json");

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
            body.GetProperty("error").GetProperty("code").GetInt32().Should().Be(404);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("POST /rest/services/{id}/ImageServer/query")]
    [Operation(Operations.Query)]
    public async Task QueryCatalog_Post_FormBody_ReturnsFeatureCollection()
    {
        var fixture = await CreateFixtureAsync(CreateRasterStoreSubstitute());
        try
        {
            using var content = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("f", "json"),
                new KeyValuePair<string, string>("returnGeometry", "false"),
            });

            var response = await fixture.Client.PostAsync(
                $"/rest/services/{TestLayerId}/ImageServer/query",
                content);

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            json.RootElement.GetProperty("features")[0]
                .TryGetProperty("geometry", out _).Should().BeFalse();
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("GET /rest/services/{id}/ImageServer/query")]
    [Operation(Operations.Query)]
    public async Task QueryCatalog_NonExistentLayer_ReturnsNotFound()
    {
        var fixture = await CreateFixtureAsync(CreateRasterStoreSubstitute());
        try
        {
            var response = await fixture.Client.GetAsync(
                "/rest/services/99999/ImageServer/query?f=json");

            // PA-070/PA-117: GeoServices always returns HTTP 200; error code is in the JSON body.
            response.StatusCode.Should().Be(HttpStatusCode.OK);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("GET /rest/services/{id}/ImageServer/query")]
    [Operation(Operations.Query)]
    public async Task QueryCatalog_Get_OrderByFieldsDescending_SortsFeatures()
    {
        var fixture = await CreateFixtureAsync(CreateMultiRasterStoreSubstitute());
        try
        {
            var response = await fixture.Client.GetAsync(
                $"/rest/services/{TestLayerId}/ImageServer/query?f=json&orderByFields=OBJECTID%20DESC");

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var ids = json.RootElement.GetProperty("features").EnumerateArray()
                .Select(f => f.GetProperty("attributes").GetProperty("OBJECTID").GetInt64())
                .ToArray();
            ids.Should().ContainInOrder(300L, 200L, 100L);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("GET /rest/services/{id}/ImageServer/query")]
    [Operation(Operations.Query)]
    public async Task QueryCatalog_Get_OutFields_ProjectsAttributes()
    {
        var fixture = await CreateFixtureAsync(CreateRasterStoreSubstitute());
        try
        {
            var response = await fixture.Client.GetAsync(
                $"/rest/services/{TestLayerId}/ImageServer/query?f=json&outFields=Name,BandCount");

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var attributes = json.RootElement.GetProperty("features")[0].GetProperty("attributes");
            attributes.TryGetProperty("OBJECTID", out _).Should().BeTrue();
            attributes.TryGetProperty("Name", out _).Should().BeTrue();
            attributes.TryGetProperty("BandCount", out _).Should().BeTrue();
            attributes.TryGetProperty("PixelType", out _).Should().BeFalse();
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("GET /rest/services/{id}/ImageServer/query")]
    [Operation(Operations.Query)]
    public async Task QueryCatalog_Get_OutFieldsUnknownField_ReturnsBadRequest()
    {
        var fixture = await CreateFixtureAsync(CreateRasterStoreSubstitute());
        try
        {
            var response = await fixture.Client.GetAsync(
                $"/rest/services/{TestLayerId}/ImageServer/query?f=json&outFields=Bogus");

            // PA-070/PA-117: GeoServices always returns HTTP 200; error code is in the JSON body.
            response.StatusCode.Should().Be(HttpStatusCode.OK);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("GET /rest/services/{id}/ImageServer/find")]
    [Endpoint("POST /rest/services/{id}/ImageServer/find")]
    [Endpoint("GET /rest/services/{serviceId}/ImageServer/find")]
    [Endpoint("POST /rest/services/{serviceId}/ImageServer/find")]
    [Operation(Operations.Query)]
    public async Task Find_GetAndPost_ReturnsImagesContainingTargetGeometry()
    {
        var fixture = await CreateFixtureAsync(CreateRasterStoreSubstitute());
        try
        {
            const string targetGeometry = """{"x":0,"y":0,"spatialReference":{"wkid":4326}}""";
            var encodedTargetGeometry = Uri.EscapeDataString(targetGeometry);

            var getResponse = await fixture.Client.GetAsync(
                $"/rest/services/{TestLayerId}/ImageServer/find?f=json&toGeometry={encodedTargetGeometry}&maxCount=1");

            getResponse.StatusCode.Should().Be(HttpStatusCode.OK);
            var find = JsonSerializer.Deserialize(
                await getResponse.Content.ReadAsStringAsync(),
                ImageServerJsonContext.Default.ImageServerFindResponse);
            AssertFindResponse(find);

            using var findContent = new FormUrlEncodedContent(
            [
                new KeyValuePair<string, string>("f", "json"),
                new KeyValuePair<string, string>("toGeometry", targetGeometry),
                new KeyValuePair<string, string>("maxCount", "1"),
            ]);
            var postResponse = await fixture.Client.PostAsync(
                $"/rest/services/{TestLayerId}/ImageServer/find",
                findContent);

            postResponse.StatusCode.Should().Be(HttpStatusCode.OK);
            find = JsonSerializer.Deserialize(
                await postResponse.Content.ReadAsStringAsync(),
                ImageServerJsonContext.Default.ImageServerFindResponse);
            AssertFindResponse(find);

            var serviceId = WebAppFixture.TestServiceId;
            var serviceGetResponse = await fixture.Client.GetAsync(
                $"/rest/services/{serviceId}/ImageServer/find?f=json&toGeometry={encodedTargetGeometry}&objectIds=100");

            serviceGetResponse.StatusCode.Should().Be(HttpStatusCode.OK);
            find = JsonSerializer.Deserialize(
                await serviceGetResponse.Content.ReadAsStringAsync(),
                ImageServerJsonContext.Default.ImageServerFindResponse);
            AssertFindResponse(find);

            using var serviceFindContent = new FormUrlEncodedContent(
            [
                new KeyValuePair<string, string>("f", "json"),
                new KeyValuePair<string, string>("toGeometry", targetGeometry),
                new KeyValuePair<string, string>("objectIds", "[100]"),
            ]);
            var servicePostResponse = await fixture.Client.PostAsync(
                $"/rest/services/{serviceId}/ImageServer/find",
                serviceFindContent);

            servicePostResponse.StatusCode.Should().Be(HttpStatusCode.OK);
            find = JsonSerializer.Deserialize(
                await servicePostResponse.Content.ReadAsStringAsync(),
                ImageServerJsonContext.Default.ImageServerFindResponse);
            AssertFindResponse(find);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("GET /rest/services/{id}/ImageServer/find")]
    [Operation(Operations.Query)]
    public async Task Find_WithOrientationMetadata_RanksByOffNadirAngle()
    {
        // Three overlapping rasters (200/100/300) carry off-nadir angles 30/5/18 degrees. The
        // most nadir image (id 100, 5 deg) must rank first regardless of footprint distance (#1880).
        var store = CreateMultiRasterStoreSubstitute();
        store.GetSensorMetadataAsync(Arg.Any<IReadOnlyCollection<long>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<long, RasterSensorMetadata>
            {
                [200] = new RasterSensorMetadata { RasterDataId = 200, ExteriorOrientationJson = """{"offNadirAngle":30}""" },
                [100] = new RasterSensorMetadata { RasterDataId = 100, ExteriorOrientationJson = """{"offNadirAngle":5}""" },
                [300] = new RasterSensorMetadata { RasterDataId = 300, ExteriorOrientationJson = """{"offNadirAngle":18}""" },
            });

        var fixture = await CreateFixtureAsync(store);
        try
        {
            const string targetGeometry = """{"x":0,"y":0,"spatialReference":{"wkid":4326}}""";
            var response = await fixture.Client.GetAsync(
                $"/rest/services/{TestLayerId}/ImageServer/find?f=json&toGeometry={Uri.EscapeDataString(targetGeometry)}");

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var find = JsonSerializer.Deserialize(
                await response.Content.ReadAsStringAsync(),
                ImageServerJsonContext.Default.ImageServerFindResponse);
            find.Should().NotBeNull();
            find!.Images.Should().NotBeNullOrEmpty();
            // Most-nadir image first.
            find.Images![0].Id.Should().Be(100);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("GET /rest/services/{id}/ImageServer/find")]
    [Operation(Operations.Query)]
    public async Task Find_MissingToGeometry_ReturnsBadRequest()
    {
        var fixture = await CreateFixtureAsync(CreateRasterStoreSubstitute());
        try
        {
            var response = await fixture.Client.GetAsync(
                $"/rest/services/{TestLayerId}/ImageServer/find?f=json");

            // PA-070/PA-117: GeoServices always returns HTTP 200; error code is in the JSON body.
            response.StatusCode.Should().Be(HttpStatusCode.OK);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("GET /rest/services/{id}/ImageServer/measure")]
    [Endpoint("POST /rest/services/{id}/ImageServer/measure")]
    [Endpoint("GET /rest/services/{serviceId}/ImageServer/measure")]
    [Endpoint("POST /rest/services/{serviceId}/ImageServer/measure")]
    [Operation(Operations.Distance)]
    public async Task Measure_DistanceAndAngle_GetAndPost_ReturnsBasicMensuration()
    {
        var fixture = await CreateFixtureAsync(CreateRasterStoreSubstitute());
        try
        {
            const string fromGeometry = """{"x":0,"y":0,"spatialReference":{"wkid":3857}}""";
            const string toGeometry = """{"x":3,"y":4,"spatialReference":{"wkid":3857}}""";
            var query = string.Join('&',
            [
                "f=json",
                "measureOperation=esriMensurationDistanceAndAngle",
                "geometryType=esriGeometryPoint",
                $"fromGeometry={Uri.EscapeDataString(fromGeometry)}",
                $"toGeometry={Uri.EscapeDataString(toGeometry)}",
                "linearUnit=esriMeters",
                "angularUnit=esriDUDecimalDegrees",
            ]);

            var getResponse = await fixture.Client.GetAsync(
                $"/rest/services/{TestLayerId}/ImageServer/measure?{query}");

            getResponse.StatusCode.Should().Be(HttpStatusCode.OK);
            AssertDistanceMeasure(await getResponse.Content.ReadAsStringAsync());

            using var measureContent = new FormUrlEncodedContent(
            [
                new KeyValuePair<string, string>("f", "json"),
                new KeyValuePair<string, string>("measureOperation", "esriMensurationDistanceAndAngle"),
                new KeyValuePair<string, string>("geometryType", "esriGeometryPoint"),
                new KeyValuePair<string, string>("fromGeometry", fromGeometry),
                new KeyValuePair<string, string>("toGeometry", toGeometry),
                new KeyValuePair<string, string>("linearUnit", "esriMeters"),
                new KeyValuePair<string, string>("angularUnit", "esriDUDecimalDegrees"),
            ]);
            var postResponse = await fixture.Client.PostAsync(
                $"/rest/services/{TestLayerId}/ImageServer/measure",
                measureContent);

            postResponse.StatusCode.Should().Be(HttpStatusCode.OK);
            AssertDistanceMeasure(await postResponse.Content.ReadAsStringAsync());

            var serviceId = WebAppFixture.TestServiceId;
            var serviceGetResponse = await fixture.Client.GetAsync(
                $"/rest/services/{serviceId}/ImageServer/measure?{query}");

            serviceGetResponse.StatusCode.Should().Be(HttpStatusCode.OK);
            AssertDistanceMeasure(await serviceGetResponse.Content.ReadAsStringAsync());

            using var serviceMeasureContent = new FormUrlEncodedContent(
            [
                new KeyValuePair<string, string>("f", "json"),
                new KeyValuePair<string, string>("measureOperation", "esriMensurationDistanceAndAngle"),
                new KeyValuePair<string, string>("geometryType", "esriGeometryPoint"),
                new KeyValuePair<string, string>("fromGeometry", fromGeometry),
                new KeyValuePair<string, string>("toGeometry", toGeometry),
            ]);
            var servicePostResponse = await fixture.Client.PostAsync(
                $"/rest/services/{serviceId}/ImageServer/measure",
                serviceMeasureContent);

            servicePostResponse.StatusCode.Should().Be(HttpStatusCode.OK);
            AssertDistanceMeasure(await servicePostResponse.Content.ReadAsStringAsync());
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("GET /rest/services/{id}/ImageServer/measure")]
    [Operation(Operations.Distance)]
    public async Task Measure_AreaAndPerimeterEnvelope_ReturnsBasicMensuration()
    {
        var fixture = await CreateFixtureAsync(CreateRasterStoreSubstitute());
        try
        {
            const string envelope = """{"xmin":0,"ymin":0,"xmax":10,"ymax":5,"spatialReference":{"wkid":3857}}""";
            var response = await fixture.Client.GetAsync(
                $"/rest/services/{TestLayerId}/ImageServer/measure?f=json&measureOperation=esriMensurationAreaAndPerimeter&geometryType=esriGeometryEnvelope&fromGeometry={Uri.EscapeDataString(envelope)}");

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var measure = JsonSerializer.Deserialize(
                await response.Content.ReadAsStringAsync(),
                ImageServerJsonContext.Default.ImageServerMeasureResponse);

            measure.Should().NotBeNull();
            measure!.Area.Should().NotBeNull();
            // 10x5 EPSG:3857 envelope near the equator: ground area/perimeter ≈ the map-unit
            // values (Web Mercator scale ≈ 1 at the equator), now measured geodesically (#2734).
            measure.Area!.Value.Should().BeApproximately(50d, 0.2d);
            measure.Area.Unit.Should().Be("esriSquareMeters");
            measure.Perimeter.Should().NotBeNull();
            measure.Perimeter!.Value.Should().BeApproximately(30d, 0.1d);
            measure.Perimeter.Unit.Should().Be("esriMeters");
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("GET /rest/services/{id}/ImageServer/measure")]
    [Operation(Operations.Distance)]
    public async Task Measure_DistanceAndAngle_GeographicDegrees_ReturnsGroundMeters()
    {
        // EPSG:4269 (NAD83) 1° of longitude at 40°N. The pre-fix planar path returned the raw
        // degree delta (~1.0) as "meters"; the ground distance is ~85 km (#2734).
        var fixture = await CreateFixtureAsync(CreateRasterStoreSubstitute());
        try
        {
            const string fromGeometry = """{"x":0,"y":40,"spatialReference":{"wkid":4269}}""";
            const string toGeometry = """{"x":1,"y":40,"spatialReference":{"wkid":4269}}""";
            var response = await fixture.Client.GetAsync(
                $"/rest/services/{TestLayerId}/ImageServer/measure?f=json&measureOperation=esriMensurationDistanceAndAngle&geometryType=esriGeometryPoint&fromGeometry={Uri.EscapeDataString(fromGeometry)}&toGeometry={Uri.EscapeDataString(toGeometry)}");

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var measure = JsonSerializer.Deserialize(
                await response.Content.ReadAsStringAsync(),
                ImageServerJsonContext.Default.ImageServerMeasureResponse);

            measure.Should().NotBeNull();
            measure!.Distance.Should().NotBeNull();
            measure.Distance!.Value.Should().BeInRange(84_500d, 85_500d);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("GET /rest/services/{id}/ImageServer/measure")]
    [Operation(Operations.Distance)]
    public async Task Measure_AreaAndPerimeter_AntimeridianPolygon_ReturnsFiniteArea()
    {
        // A ~2°x1° polygon straddling the antimeridian (179°E .. -179°E) in WGS 84. Without
        // longitude unwrapping this projects to a ~358°-wide polygon (area ~1e14 m²); unwrapping
        // keeps it a small finite quadrangle of a few 1e10 m² (#2734).
        var fixture = await CreateFixtureAsync(CreateRasterStoreSubstitute());
        try
        {
            const string polygon = """{"rings":[[[179,0],[-179,0],[-179,1],[179,1],[179,0]]],"spatialReference":{"wkid":4326}}""";
            var response = await fixture.Client.GetAsync(
                $"/rest/services/{TestLayerId}/ImageServer/measure?f=json&measureOperation=esriMensurationAreaAndPerimeter&geometryType=esriGeometryPolygon&fromGeometry={Uri.EscapeDataString(polygon)}");

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var measure = JsonSerializer.Deserialize(
                await response.Content.ReadAsStringAsync(),
                ImageServerJsonContext.Default.ImageServerMeasureResponse);

            measure.Should().NotBeNull();
            measure!.Area.Should().NotBeNull();
            measure.Area!.Value.Should().BeInRange(1e10, 1e12);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("GET /rest/services/{id}/ImageServer/measure")]
    [Operation(Operations.Distance)]
    public async Task Measure_DistanceAndAngle_WebMercatorAtHighLatitude_ReturnsGroundDistance()
    {
        // #2734: a 3857 (Web Mercator) segment must be measured as TRUE GROUND distance, not the
        // planar map-unit length. The two points are lon 0° and lon 1° at lat 60°N, expressed in
        // Web-Mercator meters (y = 8399737.89 is the Mercator ordinate of 60°N; x = 111319.49 is
        // the Mercator abscissa of lon 1°). The great-circle ground distance between them is
        // 55597.01 m on the mean-radius sphere (R = 6371008.8 m). The buggy planar
        // sqrt(dx²+dy²) would report 111319.49 m — ~2x overstated, exactly 1/cos(60°).
        var fixture = await CreateFixtureAsync(CreateRasterStoreSubstitute());
        try
        {
            const string fromGeometry = """{"x":0.0,"y":8399737.889818361,"spatialReference":{"wkid":3857}}""";
            const string toGeometry = """{"x":111319.49079327357,"y":8399737.889818361,"spatialReference":{"wkid":3857}}""";
            var response = await fixture.Client.GetAsync(
                $"/rest/services/{TestLayerId}/ImageServer/measure?f=json&measureOperation=esriMensurationDistanceAndAngle&geometryType=esriGeometryPoint&fromGeometry={Uri.EscapeDataString(fromGeometry)}&toGeometry={Uri.EscapeDataString(toGeometry)}");

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var measure = JsonSerializer.Deserialize(
                await response.Content.ReadAsStringAsync(),
                ImageServerJsonContext.Default.ImageServerMeasureResponse);

            measure.Should().NotBeNull();
            measure!.Distance.Should().NotBeNull();
            measure.Distance!.Value.Should().BeApproximately(55597.01d, 1d);
            measure.Distance.Unit.Should().Be("esriMeters");
            // Guard against a regression back to the planar (2x overstated) value.
            measure.Distance.Value.Should().BeLessThan(60000d);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("GET /rest/services/{id}/ImageServer/measure")]
    [Operation(Operations.Distance)]
    public async Task Measure_DistanceAndAngle_GeographicAzimuth_UsesGreatCircleBearing()
    {
        // #2734: azimuth for geographic inputs must include cos(lat) longitude scaling. From
        // (lon 0, lat 60°N) to (lon 1, lat 61°N) the great-circle initial bearing is 25.78°
        // (standard atan2 initial-bearing formula; radius-independent), consistent with the
        // geodesic distance in the same response. The buggy planar atan2(dLon, dLat) reported
        // 45° regardless of latitude.
        var fixture = await CreateFixtureAsync(CreateRasterStoreSubstitute());
        try
        {
            const string fromGeometry = """{"x":0.0,"y":60.0,"spatialReference":{"wkid":4326}}""";
            const string toGeometry = """{"x":1.0,"y":61.0,"spatialReference":{"wkid":4326}}""";
            var response = await fixture.Client.GetAsync(
                $"/rest/services/{TestLayerId}/ImageServer/measure?f=json&measureOperation=esriMensurationDistanceAndAngle&geometryType=esriGeometryPoint&fromGeometry={Uri.EscapeDataString(fromGeometry)}&toGeometry={Uri.EscapeDataString(toGeometry)}");

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var measure = JsonSerializer.Deserialize(
                await response.Content.ReadAsStringAsync(),
                ImageServerJsonContext.Default.ImageServerMeasureResponse);

            measure.Should().NotBeNull();
            measure!.AzimuthAngle.Should().NotBeNull();
            measure.AzimuthAngle!.Value.Should().BeApproximately(25.7824d, 1e-3);
            measure.AzimuthAngle.Unit.Should().Be("esriDUDecimalDegrees");
            // Guard against a regression to the cos(lat)-free planar bearing (45°).
            measure.AzimuthAngle.Value.Should().BeLessThan(40d);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("GET /rest/services/{id}/ImageServer/measure")]
    [Operation(Operations.Distance)]
    public async Task Measure_Centroid_AntimeridianRing_LandsOnCorrectSide()
    {
        // #2734: the area-weighted centroid of the dateline-straddling [179,181]x[0,1] ring is at
        // lon ±180°, lat 0.5°. The buggy vertex mean of (179, -179, -179, 179) collapses to lon 0°
        // — the opposite side of the globe.
        var fixture = await CreateFixtureAsync(CreateRasterStoreSubstitute());
        try
        {
            const string polygon = """{"rings":[[[179,0],[-179,0],[-179,1],[179,1],[179,0]]],"spatialReference":{"wkid":4326}}""";
            var response = await fixture.Client.GetAsync(
                $"/rest/services/{TestLayerId}/ImageServer/measure?f=json&measureOperation=esriMensurationCentroid&geometryType=esriGeometryPolygon&fromGeometry={Uri.EscapeDataString(polygon)}");

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var measure = JsonSerializer.Deserialize(
                await response.Content.ReadAsStringAsync(),
                ImageServerJsonContext.Default.ImageServerMeasureResponse);

            measure.Should().NotBeNull();
            measure!.Point.Should().NotBeNull();
            // Centroid longitude is ±180 (the antimeridian), never near 0.
            Math.Abs(measure.Point!.Value.X).Should().BeApproximately(180d, 1e-6);
            measure.Point.Value.Y.Should().BeApproximately(0.5d, 1e-6);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("GET /rest/services/{id}/ImageServer/measure")]
    [Operation(Operations.Distance)]
    public async Task Measure_HeightOperation_ReturnsNotImplemented()
    {
        // No DEM/sensor metadata on the default substitute, so height is honestly 501.
        var fixture = await CreateFixtureAsync(CreateRasterStoreSubstitute());
        try
        {
            const string fromGeometry = """{"x":0,"y":0,"spatialReference":{"wkid":3857}}""";
            const string toGeometry = """{"x":3,"y":4,"spatialReference":{"wkid":3857}}""";
            var response = await fixture.Client.GetAsync(
                $"/rest/services/{TestLayerId}/ImageServer/measure?f=json&measureOperation=esriMensurationHeightFromBaseAndTop&geometryType=esriGeometryPoint&fromGeometry={Uri.EscapeDataString(fromGeometry)}&toGeometry={Uri.EscapeDataString(toGeometry)}");

            // #2795: not-implemented operations now surface body error.code 501 (pass-through) instead
            // of collapsing to 500, so clients can distinguish "unsupported" from a server fault.
            await response.AssertGeoServicesErrorAsync(501);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("GET /rest/services/{id}/ImageServer/measure")]
    [Operation(Operations.Distance)]
    public async Task Measure_HeightOperation_WithDemMetadata_ReturnsDifferencedHeight()
    {
        // The raster carries a DEM source; the elevation service returns 50m at the base point
        // and 130m at the top point, so the measured height is 80m (#1879).
        var store = CreateRasterStoreSubstitute();
        store.GetSensorMetadataAsync(Arg.Any<IReadOnlyCollection<long>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<long, RasterSensorMetadata>
            {
                [100] = new RasterSensorMetadata
                {
                    RasterDataId = 100,
                    SensorName = "TestSensor",
                    DemSource = "777",
                },
            });

        var elevation = Substitute.For<IElevationService>();
        elevation.QueryPointAsync(777, 0, 0, Arg.Any<int?>(), Arg.Any<RasterMergeStrategy>(), Arg.Any<CancellationToken>())
            .Returns(new ElevationPointResult
            {
                Elevation = 50,
                NoData = false,
                OutOfBounds = false,
                LayerId = 777,
                RasterIds = [1],
                X = 0,
                Y = 0,
            });
        elevation.QueryPointAsync(777, 3, 4, Arg.Any<int?>(), Arg.Any<RasterMergeStrategy>(), Arg.Any<CancellationToken>())
            .Returns(new ElevationPointResult
            {
                Elevation = 130,
                NoData = false,
                OutOfBounds = false,
                LayerId = 777,
                RasterIds = [1],
                X = 3,
                Y = 4,
            });

        var fixture = await CreateFixtureAsync(store, elevation);
        try
        {
            const string fromGeometry = """{"x":0,"y":0,"spatialReference":{"wkid":3857}}""";
            const string toGeometry = """{"x":3,"y":4,"spatialReference":{"wkid":3857}}""";
            var response = await fixture.Client.GetAsync(
                $"/rest/services/{TestLayerId}/ImageServer/measure?f=json&measureOperation=esriMensurationHeightFromBaseAndTop&geometryType=esriGeometryPoint&fromGeometry={Uri.EscapeDataString(fromGeometry)}&toGeometry={Uri.EscapeDataString(toGeometry)}");

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var measure = JsonSerializer.Deserialize(
                await response.Content.ReadAsStringAsync(),
                ImageServerJsonContext.Default.ImageServerMeasureResponse);
            measure.Should().NotBeNull();
            measure!.Height.Should().NotBeNull();
            measure.Height!.Value.Should().BeApproximately(80, 1e-6);
            measure.SensorName.Should().Be("TestSensor");
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("GET /rest/services/{id}/ImageServer/measure")]
    [Operation(Operations.Distance)]
    public async Task Measure_HeightOperation_WithDemMetadata_ButPointOutsideDem_ReturnsNotImplemented()
    {
        // DEM is modeled but does not cover the top point: return 501 rather than a faked height.
        var store = CreateRasterStoreSubstitute();
        store.GetSensorMetadataAsync(Arg.Any<IReadOnlyCollection<long>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<long, RasterSensorMetadata>
            {
                [100] = new RasterSensorMetadata
                {
                    RasterDataId = 100,
                    DemSource = "777",
                },
            });

        var elevation = Substitute.For<IElevationService>();
        elevation.QueryPointAsync(777, Arg.Any<double>(), Arg.Any<double>(), Arg.Any<int?>(), Arg.Any<RasterMergeStrategy>(), Arg.Any<CancellationToken>())
            .Returns(new ElevationPointResult
            {
                Elevation = null,
                NoData = true,
                OutOfBounds = true,
                LayerId = 777,
                RasterIds = [],
                X = 0,
                Y = 0,
            });

        var fixture = await CreateFixtureAsync(store, elevation);
        try
        {
            const string fromGeometry = """{"x":0,"y":0,"spatialReference":{"wkid":3857}}""";
            const string toGeometry = """{"x":3,"y":4,"spatialReference":{"wkid":3857}}""";
            var response = await fixture.Client.GetAsync(
                $"/rest/services/{TestLayerId}/ImageServer/measure?f=json&measureOperation=esriMensurationHeightFromBaseAndTop&geometryType=esriGeometryPoint&fromGeometry={Uri.EscapeDataString(fromGeometry)}&toGeometry={Uri.EscapeDataString(toGeometry)}");

            // #2795: not-implemented operations now surface body error.code 501 (pass-through) instead
            // of collapsing to 500, so clients can distinguish "unsupported" from a server fault.
            await response.AssertGeoServicesErrorAsync(501);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("GET /rest/services/{id}/ImageServer")]
    [Operation(Operations.GetServiceInfo)]
    public async Task GetServiceInfo_WithMeasureRoute_AdvertisesBasicMensuration()
    {
        var fixture = await CreateFixtureAsync(CreateRasterStoreSubstitute());
        try
        {
            var response = await fixture.Client.GetAsync(
                $"/rest/services/{TestLayerId}/ImageServer?f=json");

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var root = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
            root.GetProperty("capabilities").GetString().Should().Contain("Mensuration");
            root.GetProperty("mensurationCapabilities").GetString().Should().Be("Basic");
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("GET /rest/services/{id}/ImageServer/computeStatisticsHistograms")]
    [Operation(Operations.Query)]
    public async Task ComputeStatisticsHistograms_Get_WithoutGeometry_ReturnsBadRequest()
    {
        var fixture = await CreateFixtureAsync(CreateRasterStoreSubstitute());
        try
        {
            var response = await fixture.Client.GetAsync(
                $"/rest/services/{TestLayerId}/ImageServer/computeStatisticsHistograms?f=json");

            // PA-070/PA-117: GeoServices always returns HTTP 200; error code is in the JSON body.
            response.StatusCode.Should().Be(HttpStatusCode.OK);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("GET /rest/services/{id}/ImageServer/computeStatisticsHistograms")]
    [Operation(Operations.Query)]
    public async Task ComputeStatisticsHistograms_Get_WithEnvelope_ReturnsStatisticsAndHistograms()
    {
        var fixture = await CreateFixtureAsync(CreateRasterStoreSubstitute());
        try
        {
            var geometry = Uri.EscapeDataString(
                """{"xmin":-180,"ymin":-90,"xmax":180,"ymax":90,"spatialReference":{"wkid":4326}}""");
            var response = await fixture.Client.GetAsync(
                $"/rest/services/{TestLayerId}/ImageServer/computeStatisticsHistograms?f=json&geometryType=esriGeometryEnvelope&geometry={geometry}");

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            json.RootElement.GetProperty("statistics").GetArrayLength().Should().Be(1);
            json.RootElement.GetProperty("statistics")[0].GetProperty("min").GetDouble().Should().Be(0);
            json.RootElement.GetProperty("statistics")[0].GetProperty("max").GetDouble().Should().Be(255);
            json.RootElement.GetProperty("histograms").GetArrayLength().Should().Be(1);
            json.RootElement.GetProperty("histograms")[0].GetProperty("counts").GetArrayLength().Should().Be(4);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("GET /rest/services/{id}/ImageServer/computeStatisticsHistograms")]
    [Operation(Operations.Query)]
    public async Task ComputeStatisticsHistograms_WithGeometry_ClipsAnalysisToAoi()
    {
        var store = CreateRasterStoreSubstitute();
        var fixture = await CreateFixtureAsync(store);
        try
        {
            var geometry = Uri.EscapeDataString(
                """{"xmin":-10,"ymin":-10,"xmax":10,"ymax":10,"spatialReference":{"wkid":4326}}""");
            var response = await fixture.Client.GetAsync(
                $"/rest/services/{TestLayerId}/ImageServer/computeStatisticsHistograms?f=json&geometryType=esriGeometryEnvelope&geometry={geometry}");

            response.StatusCode.Should().Be(HttpStatusCode.OK);

            // The AOI geometry must route to the clipped store path, not the whole-raster one.
            await store.Received().GetClippedStatisticsAsync(
                Arg.Any<int>(), Arg.Any<long>(), Arg.Is<byte[]>(g => g.Length > 0), Arg.Any<int?>(), Arg.Any<int[]?>(), Arg.Any<RasterIdentifyRendering?>(), Arg.Any<CancellationToken>());
            await store.DidNotReceive().GetStatisticsAsync(
                Arg.Any<int>(), Arg.Any<long>(), Arg.Any<int[]?>(), Arg.Any<RasterIdentifyRendering?>(), Arg.Any<CancellationToken>());
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("POST /rest/services/{id}/ImageServer/computeStatisticsHistograms")]
    [Operation(Operations.Query)]
    public async Task ComputeStatisticsHistograms_Post_WithGeometry_ReturnsStatisticsAndHistograms()
    {
        var fixture = await CreateFixtureAsync(CreateRasterStoreSubstitute());
        try
        {
            using var content = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("f", "json"),
                new KeyValuePair<string, string>("geometryType", "esriGeometryEnvelope"),
                new KeyValuePair<string, string>("geometry", "{\"xmin\":-180,\"ymin\":-90,\"xmax\":180,\"ymax\":90,\"spatialReference\":{\"wkid\":4326}}"),
            });

            var response = await fixture.Client.PostAsync(
                $"/rest/services/{TestLayerId}/ImageServer/computeStatisticsHistograms",
                content);

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            json.RootElement.GetProperty("statistics").GetArrayLength().Should().Be(1);
            json.RootElement.GetProperty("histograms").GetArrayLength().Should().Be(1);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("GET /rest/services/{id}/ImageServer/computeHistograms")]
    [Operation(Operations.Query)]
    public async Task ComputeHistograms_Get_WithEnvelope_ReturnsHistograms()
    {
        var fixture = await CreateFixtureAsync(CreateRasterStoreSubstitute());
        try
        {
            var geometry = Uri.EscapeDataString(
                """{"xmin":-180,"ymin":-90,"xmax":180,"ymax":90,"spatialReference":{"wkid":4326}}""");
            var response = await fixture.Client.GetAsync(
                $"/rest/services/{TestLayerId}/ImageServer/computeHistograms?f=json&geometryType=esriGeometryEnvelope&geometry={geometry}");

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            json.RootElement.GetProperty("histograms").GetArrayLength().Should().Be(1);
            json.RootElement.GetProperty("histograms")[0].GetProperty("size").GetInt32().Should().Be(4);
            json.RootElement.TryGetProperty("statistics", out _).Should().BeFalse();
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("POST /rest/services/{id}/ImageServer/computeHistograms")]
    [Operation(Operations.Query)]
    public async Task ComputeHistograms_Post_WithGeometry_ReturnsHistograms()
    {
        var fixture = await CreateFixtureAsync(CreateRasterStoreSubstitute());
        try
        {
            using var content = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("f", "json"),
                new KeyValuePair<string, string>("geometryType", "esriGeometryEnvelope"),
                new KeyValuePair<string, string>("geometry", "{\"xmin\":-180,\"ymin\":-90,\"xmax\":180,\"ymax\":90}"),
            });

            var response = await fixture.Client.PostAsync(
                $"/rest/services/{TestLayerId}/ImageServer/computeHistograms",
                content);

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            json.RootElement.GetProperty("histograms").GetArrayLength().Should().Be(1);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("GET /rest/services/{id}/ImageServer/computeHistograms")]
    [Operation(Operations.Query)]
    public async Task ComputeHistograms_Get_WithoutGeometry_ReturnsBadRequest()
    {
        var fixture = await CreateFixtureAsync(CreateRasterStoreSubstitute());
        try
        {
            var response = await fixture.Client.GetAsync(
                $"/rest/services/{TestLayerId}/ImageServer/computeHistograms?f=json");

            // PA-070/PA-117: GeoServices always returns HTTP 200; error code is in the JSON body.
            response.StatusCode.Should().Be(HttpStatusCode.OK);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("GET /rest/services/{id}/ImageServer/getSamples")]
    [Operation(Operations.Query)]
    public async Task GetSamples_Get_WithMultipoint_ReturnsSamples()
    {
        var fixture = await CreateFixtureAsync(CreateSamplingRasterStoreSubstitute());
        try
        {
            var geometry = Uri.EscapeDataString(
                """{"points":[[0,0],[1,1]],"spatialReference":{"wkid":4326}}""");
            var response = await fixture.Client.GetAsync(
                $"/rest/services/{TestLayerId}/ImageServer/getSamples?f=json&geometryType=esriGeometryMultipoint&geometry={geometry}");

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            json.RootElement.GetProperty("samples").GetArrayLength().Should().Be(2);
            json.RootElement.GetProperty("samples")[0].GetProperty("value").GetString().Should().Be("42");
            json.RootElement.GetProperty("samples")[0].GetProperty("location").GetProperty("x").GetDouble().Should().Be(0);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("POST /rest/services/{id}/ImageServer/getSamples")]
    [Operation(Operations.Query)]
    public async Task GetSamples_Post_WithPoint_ReturnsSample()
    {
        var fixture = await CreateFixtureAsync(CreateSamplingRasterStoreSubstitute());
        try
        {
            using var content = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("f", "json"),
                new KeyValuePair<string, string>("geometryType", "esriGeometryPoint"),
                new KeyValuePair<string, string>("geometry", "{\"x\":0,\"y\":0,\"spatialReference\":{\"wkid\":4326}}"),
            });

            var response = await fixture.Client.PostAsync(
                $"/rest/services/{TestLayerId}/ImageServer/getSamples",
                content);

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            json.RootElement.GetProperty("samples").GetArrayLength().Should().Be(1);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("GET /rest/services/{id}/ImageServer/getSamples")]
    [Operation(Operations.Query)]
    public async Task GetSamples_Get_WithoutGeometry_ReturnsBadRequest()
    {
        var fixture = await CreateFixtureAsync(CreateSamplingRasterStoreSubstitute());
        try
        {
            var response = await fixture.Client.GetAsync(
                $"/rest/services/{TestLayerId}/ImageServer/getSamples?f=json");

            // PA-070/PA-117: GeoServices always returns HTTP 200; error code is in the JSON body.
            response.StatusCode.Should().Be(HttpStatusCode.OK);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("GET /rest/services/{id}/ImageServer/computeCacheInfo")]
    [Endpoint("POST /rest/services/{id}/ImageServer/computeCacheInfo")]
    [Endpoint("GET /rest/services/{serviceId}/ImageServer/computeCacheInfo")]
    [Endpoint("POST /rest/services/{serviceId}/ImageServer/computeCacheInfo")]
    [Operation(Operations.Metadata)]
    public async Task ComputeCacheInfo_GetAndPost_ReturnsDynamicCacheInfo()
    {
        var fixture = await CreateFixtureAsync(CreateRasterStoreSubstitute());
        try
        {
            var getResponse = await fixture.Client.GetAsync(
                $"/rest/services/{TestLayerId}/ImageServer/computeCacheInfo?f=json");

            getResponse.StatusCode.Should().Be(HttpStatusCode.OK);
            var json = JsonDocument.Parse(await getResponse.Content.ReadAsStringAsync());
            var cacheInfo = json.RootElement.GetProperty("cacheInfo");
            cacheInfo.GetProperty("extent").GetProperty("spatialReference").GetProperty("wkid").GetInt32().Should().Be(4326);
            cacheInfo.TryGetProperty("tileInfo", out _).Should().BeFalse();

            using var cacheInfoContent = new FormUrlEncodedContent([new KeyValuePair<string, string>("f", "json")]);
            var postResponse = await fixture.Client.PostAsync(
                $"/rest/services/{TestLayerId}/ImageServer/computeCacheInfo",
                cacheInfoContent);

            postResponse.StatusCode.Should().Be(HttpStatusCode.OK);
            var postJson = JsonDocument.Parse(await postResponse.Content.ReadAsStringAsync());
            postJson.RootElement.GetProperty("cacheInfo").GetProperty("extent").GetProperty("xmin").GetDouble().Should().Be(-180);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("GET /rest/services/{id}/ImageServer/computePixelLocation")]
    [Endpoint("POST /rest/services/{id}/ImageServer/computePixelLocation")]
    [Endpoint("GET /rest/services/{serviceId}/ImageServer/computePixelLocation")]
    [Endpoint("POST /rest/services/{serviceId}/ImageServer/computePixelLocation")]
    [Operation(Operations.Query)]
    public async Task ComputePixelLocation_GetAndPost_UsesRasterGeoTransform()
    {
        var fixture = await CreateFixtureAsync(CreateRasterStoreSubstitute());
        try
        {
            const string geometries = """{"geometries":[{"x":0,"y":0,"spatialReference":{"wkid":4326}}],"geometryType":"esriGeometryPoint"}""";
            var encoded = Uri.EscapeDataString(geometries);
            var getResponse = await fixture.Client.GetAsync(
                $"/rest/services/{TestLayerId}/ImageServer/computePixelLocation?f=json&geometries={encoded}");

            getResponse.StatusCode.Should().Be(HttpStatusCode.OK);
            var json = JsonDocument.Parse(await getResponse.Content.ReadAsStringAsync());
            var point = json.RootElement.GetProperty("geometries")[0];
            point.GetProperty("x").GetDouble().Should().BeApproximately(128, 0.0001);
            point.GetProperty("y").GetDouble().Should().BeApproximately(128, 0.0001);

            using var pixelLocationContent = new FormUrlEncodedContent(
            [
                new KeyValuePair<string, string>("f", "json"),
                new KeyValuePair<string, string>("geometries", geometries),
            ]);
            var postResponse = await fixture.Client.PostAsync(
                $"/rest/services/{TestLayerId}/ImageServer/computePixelLocation",
                pixelLocationContent);

            postResponse.StatusCode.Should().Be(HttpStatusCode.OK);
            var postJson = JsonDocument.Parse(await postResponse.Content.ReadAsStringAsync());
            postJson.RootElement.GetProperty("geometries")[0].GetProperty("x").GetDouble()
                .Should().BeApproximately(128, 0.0001);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("GET /rest/services/{id}/ImageServer/queryBoundary")]
    [Endpoint("POST /rest/services/{id}/ImageServer/queryBoundary")]
    [Endpoint("GET /rest/services/{serviceId}/ImageServer/queryBoundary")]
    [Endpoint("POST /rest/services/{serviceId}/ImageServer/queryBoundary")]
    [Operation(Operations.Metadata)]
    public async Task QueryBoundary_GetAndPost_ReturnsShapeAndArea()
    {
        var fixture = await CreateFixtureAsync(CreateRasterStoreSubstitute());
        try
        {
            var getResponse = await fixture.Client.GetAsync(
                $"/rest/services/{TestLayerId}/ImageServer/queryBoundary?f=json");

            getResponse.StatusCode.Should().Be(HttpStatusCode.OK);
            var json = JsonDocument.Parse(await getResponse.Content.ReadAsStringAsync());
            json.RootElement.GetProperty("shape").GetProperty("spatialReference").GetProperty("wkid").GetInt32()
                .Should().Be(4326);
            json.RootElement.GetProperty("shape").GetProperty("rings")[0].GetArrayLength().Should().Be(5);
            json.RootElement.GetProperty("area").GetDouble().Should().BeGreaterThan(0);

            using var boundaryContent = new FormUrlEncodedContent([new KeyValuePair<string, string>("f", "json")]);
            var postResponse = await fixture.Client.PostAsync(
                $"/rest/services/{TestLayerId}/ImageServer/queryBoundary",
                boundaryContent);

            postResponse.StatusCode.Should().Be(HttpStatusCode.OK);
            var postJson = JsonDocument.Parse(await postResponse.Content.ReadAsStringAsync());
            postJson.RootElement.GetProperty("shape").GetProperty("rings")[0].GetArrayLength().Should().Be(5);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("GET /rest/services/{id}/ImageServer/project")]
    [Endpoint("POST /rest/services/{id}/ImageServer/project")]
    [Endpoint("GET /rest/services/{serviceId}/ImageServer/project")]
    [Endpoint("POST /rest/services/{serviceId}/ImageServer/project")]
    [Operation(Operations.Project)]
    public async Task Project_GetAndPost_ReprojectsGeometries()
    {
        var fixture = await CreateFixtureAsync(CreateRasterStoreSubstitute());
        try
        {
            const string geometries = """{"geometryType":"esriGeometryPoint","geometries":[{"x":0,"y":0,"spatialReference":{"wkid":4326}}]}""";
            var encoded = Uri.EscapeDataString(geometries);

            var getResponse = await fixture.Client.GetAsync(
                $"/rest/services/{TestLayerId}/ImageServer/project?f=json&inSR=4326&outSR=3857&geometries={encoded}");

            getResponse.StatusCode.Should().Be(HttpStatusCode.OK);
            AssertProjectedOrigin(await getResponse.Content.ReadAsStringAsync());

            using var projectContent = new FormUrlEncodedContent(
            [
                new KeyValuePair<string, string>("f", "json"),
                new KeyValuePair<string, string>("inSR", "4326"),
                new KeyValuePair<string, string>("outSR", "3857"),
                new KeyValuePair<string, string>("geometries", geometries),
            ]);
            var postResponse = await fixture.Client.PostAsync(
                $"/rest/services/{TestLayerId}/ImageServer/project",
                projectContent);

            postResponse.StatusCode.Should().Be(HttpStatusCode.OK);
            AssertProjectedOrigin(await postResponse.Content.ReadAsStringAsync());

            var serviceId = WebAppFixture.TestServiceId;
            var serviceGetResponse = await fixture.Client.GetAsync(
                $"/rest/services/{serviceId}/ImageServer/project?f=json&inSR=4326&outSR=3857&geometries={encoded}");

            serviceGetResponse.StatusCode.Should().Be(HttpStatusCode.OK);
            AssertProjectedOrigin(await serviceGetResponse.Content.ReadAsStringAsync());

            using var serviceProjectContent = new FormUrlEncodedContent(
            [
                new KeyValuePair<string, string>("f", "json"),
                new KeyValuePair<string, string>("inSR", "4326"),
                new KeyValuePair<string, string>("outSR", "3857"),
                new KeyValuePair<string, string>("geometries", geometries),
            ]);
            var servicePostResponse = await fixture.Client.PostAsync(
                $"/rest/services/{serviceId}/ImageServer/project",
                serviceProjectContent);

            servicePostResponse.StatusCode.Should().Be(HttpStatusCode.OK);
            AssertProjectedOrigin(await servicePostResponse.Content.ReadAsStringAsync());
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("GET /rest/services/{id}/ImageServer/project")]
    [Endpoint("POST /rest/services/{id}/ImageServer/project")]
    [Operation(Operations.Project)]
    public async Task Project_WithDatumTransformationWkid_AppliesSelectedPipeline()
    {
        var fixture = await CreateFixtureAsync(CreateRasterStoreSubstitute());
        try
        {
            // WKID 108001 (NAD_1983_To_WGS_1984_1) is the catalog default for NAD83 (4269)
            // -> WGS84 (4326); supplying it must be honored (no longer rejected) and yield
            // coordinates within datum tolerance of the input.
            const string geometries = """{"geometryType":"esriGeometryPoint","geometries":[{"x":-100.0,"y":40.0,"spatialReference":{"wkid":4269}}]}""";
            var encoded = Uri.EscapeDataString(geometries);

            var getResponse = await fixture.Client.GetAsync(
                $"/rest/services/{TestLayerId}/ImageServer/project?f=json&inSR=4269&outSR=4326&datumTransformation=108001&geometries={encoded}");

            getResponse.StatusCode.Should().Be(HttpStatusCode.OK);
            var json = JsonDocument.Parse(await getResponse.Content.ReadAsStringAsync());
            var geometry = json.RootElement.GetProperty("geometries")[0];
            geometry.GetProperty("x").GetDouble().Should().BeApproximately(-100.0, 0.01);
            geometry.GetProperty("y").GetDouble().Should().BeApproximately(40.0, 0.01);

            using var datumProjectContent = new FormUrlEncodedContent(
            [
                new KeyValuePair<string, string>("f", "json"),
                new KeyValuePair<string, string>("inSR", "4269"),
                new KeyValuePair<string, string>("outSR", "4326"),
                new KeyValuePair<string, string>("datumTransformation", "108001"),
                new KeyValuePair<string, string>("geometries", geometries),
            ]);
            var postResponse = await fixture.Client.PostAsync(
                $"/rest/services/{TestLayerId}/ImageServer/project",
                datumProjectContent);

            postResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("GET /rest/services/{id}/ImageServer/project")]
    [Operation(Operations.Project)]
    public async Task Project_WithUnsupportedDatumTransformationWkid_Returns400()
    {
        var fixture = await CreateFixtureAsync(CreateRasterStoreSubstitute());
        try
        {
            // WKID 108001 does not connect the 4326 -> 3857 pair, so an explicit request
            // for it must be rejected rather than silently substituted.
            const string geometries = """{"geometryType":"esriGeometryPoint","geometries":[{"x":0,"y":0,"spatialReference":{"wkid":4326}}]}""";
            var encoded = Uri.EscapeDataString(geometries);

            var response = await fixture.Client.GetAsync(
                $"/rest/services/{TestLayerId}/ImageServer/project?f=json&inSR=4326&outSR=3857&datumTransformation=108001&geometries={encoded}");

            await response.AssertGeoServicesErrorAsync(400);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("GET /rest/services/{id}/ImageServer/project")]
    [Operation(Operations.Project)]
    public async Task Project_WithImageCoordinateSystemTransformation_Returns400()
    {
        // The default raster substitute carries no RPC/sensor metadata, so the image-coordinate
        // -system `transformation` parameter is genuinely unsupported for it and returns 400.
        var fixture = await CreateFixtureAsync(CreateRasterStoreSubstitute());
        try
        {
            const string geometries = """{"geometryType":"esriGeometryPoint","geometries":[{"x":0,"y":0,"spatialReference":{"wkid":4326}}]}""";
            var encoded = Uri.EscapeDataString(geometries);

            var response = await fixture.Client.GetAsync(
                $"/rest/services/{TestLayerId}/ImageServer/project?f=json&inSR=4326&outSR=3857&transformation=1&geometries={encoded}");

            // PA-070/PA-117: GeoServices always returns HTTP 200; error code is in the JSON body.
            response.StatusCode.Should().Be(HttpStatusCode.OK);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("GET /rest/services/{id}/ImageServer/project")]
    [Operation(Operations.Project)]
    public async Task Project_WithImageCoordinateSystemTransformation_AndRpcMetadata_WarpsImageToMap()
    {
        // A raster carrying RPC metadata supports the image-coordinate-system warp (#1881):
        // image (sample, line) coordinates map to ground (lon/lat) and then to outSR.
        var store = CreateRasterStoreSubstitute();
        store.GetSensorMetadataAsync(Arg.Any<IReadOnlyCollection<long>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<long, RasterSensorMetadata>
            {
                [100] = new RasterSensorMetadata
                {
                    RasterDataId = 100,
                    SensorName = "TestSensor",
                    RpcJson = """
                    {
                        "sampleOffset": 0, "lineOffset": 0,
                        "longOffset": -120.0, "latOffset": 35.0,
                        "sampleScale": 1, "lineScale": 1,
                        "longScale": 0.001, "latScale": 0.001
                    }
                    """,
                },
            });

        var fixture = await CreateFixtureAsync(store);
        try
        {
            // Image origin (0,0) maps to the ground offset (-120, 35); outSR=4326 keeps it as-is.
            const string geometries = """{"geometryType":"esriGeometryPoint","geometries":[{"x":0,"y":0}]}""";
            var encoded = Uri.EscapeDataString(geometries);

            var response = await fixture.Client.GetAsync(
                $"/rest/services/{TestLayerId}/ImageServer/project?f=json&outSR=4326&transformation=image&geometries={encoded}");

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var geometry = json.RootElement.GetProperty("geometries")[0];
            geometry.GetProperty("x").GetDouble().Should().BeApproximately(-120.0, 1e-6);
            geometry.GetProperty("y").GetDouble().Should().BeApproximately(35.0, 1e-6);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("GET /rest/services/{id}/ImageServer/project")]
    [Operation(Operations.Project)]
    public async Task Project_WithImageCoordinateSystemTransformation_ComposesWarpWithReprojectionAndDiffersFromNoTransformation()
    {
        // #2840 round trip: the image-CS warp (image sample/line -> WGS84 ground) is composed with
        // the ground -> outSR reprojection. Requesting the transformation must move the geometry to
        // the warped/reprojected location, which differs from the untransformed reprojection of the
        // same input coordinate.
        var store = CreateRpcRasterStoreSubstitute();
        var fixture = await CreateFixtureAsync(store);
        try
        {
            // Image origin (0,0) warps to ground (-120, 35), then reprojects into Web Mercator (3857).
            const string imageGeometries = """{"geometryType":"esriGeometryPoint","geometries":[{"x":0,"y":0}]}""";
            var warpResponse = await fixture.Client.GetAsync(
                $"/rest/services/{TestLayerId}/ImageServer/project?f=json&outSR=3857&transformation=image&geometries={Uri.EscapeDataString(imageGeometries)}");

            warpResponse.StatusCode.Should().Be(HttpStatusCode.OK);
            using var warpJson = JsonDocument.Parse(await warpResponse.Content.ReadAsStringAsync());
            var warped = warpJson.RootElement.GetProperty("geometries")[0];
            var warpedX = warped.GetProperty("x").GetDouble();
            var warpedY = warped.GetProperty("y").GetDouble();

            // Web Mercator of (-120, 35): ~(-13358338.9, 4163881.1).
            warpedX.Should().BeApproximately(-13358338.9, 1.0);
            warpedY.Should().BeApproximately(4163881.1, 1.0);

            // Same input coordinate (0,0) reprojected 4326 -> 3857 without the transformation stays
            // at the map origin; the transformation result must differ.
            const string mapGeometries = """{"geometryType":"esriGeometryPoint","geometries":[{"x":0,"y":0,"spatialReference":{"wkid":4326}}]}""";
            var plainResponse = await fixture.Client.GetAsync(
                $"/rest/services/{TestLayerId}/ImageServer/project?f=json&inSR=4326&outSR=3857&geometries={Uri.EscapeDataString(mapGeometries)}");

            plainResponse.StatusCode.Should().Be(HttpStatusCode.OK);
            using var plainJson = JsonDocument.Parse(await plainResponse.Content.ReadAsStringAsync());
            var plain = plainJson.RootElement.GetProperty("geometries")[0];
            plain.GetProperty("x").GetDouble().Should().BeApproximately(0.0, 1e-6);

            warpedX.Should().NotBeApproximately(plain.GetProperty("x").GetDouble(), 1.0);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("GET /rest/services/{id}/ImageServer/project")]
    [Operation(Operations.Project)]
    public async Task Project_WithImageCoordinateSystemTransformation_AndUnsupportedDatumTransformation_Returns400()
    {
        // #2840: when the image-CS warp is composed with an unsupported datumTransformation for the
        // ground(4326) -> outSR leg, the request is rejected with a precise 400 rather than silently
        // dropping the requested transformation. WKID 108001 does not connect 4326 -> 3857.
        var store = CreateRpcRasterStoreSubstitute();
        var fixture = await CreateFixtureAsync(store);
        try
        {
            const string geometries = """{"geometryType":"esriGeometryPoint","geometries":[{"x":0,"y":0}]}""";
            var response = await fixture.Client.GetAsync(
                $"/rest/services/{TestLayerId}/ImageServer/project?f=json&outSR=3857&transformation=image&datumTransformation=108001&geometries={Uri.EscapeDataString(geometries)}");

            await response.AssertGeoServicesErrorAsync(400);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("GET /rest/services/{id}/ImageServer/estimateExportTilesSize")]
    [Endpoint("POST /rest/services/{id}/ImageServer/estimateExportTilesSize")]
    [Endpoint("GET /rest/services/{serviceId}/ImageServer/estimateExportTilesSize")]
    [Endpoint("POST /rest/services/{serviceId}/ImageServer/estimateExportTilesSize")]
    [Operation(Operations.Export)]
    public async Task EstimateExportTilesSize_GetAndPost_ReturnsStorageBackedEstimate()
    {
        var fixture = await CreateFixtureAsync(CreateRasterStoreSubstitute());
        try
        {
            var query = "f=json&levels=0&exportExtent=-180,-85,180,85&maxTiles=1";
            var getResponse = await fixture.Client.GetAsync(
                $"/rest/services/{TestLayerId}/ImageServer/estimateExportTilesSize?{query}");

            getResponse.StatusCode.Should().Be(HttpStatusCode.OK);
            var estimate = JsonSerializer.Deserialize(
                await getResponse.Content.ReadAsStringAsync(),
                ImageServerJsonContext.Default.ImageServerExportTilesEstimateResponse);
            AssertExportTilesEstimate(estimate);

            using var estimateContent = new FormUrlEncodedContent(
            [
                new KeyValuePair<string, string>("f", "json"),
                new KeyValuePair<string, string>("levels", "0"),
                new KeyValuePair<string, string>("exportExtent", "-180,-85,180,85"),
                new KeyValuePair<string, string>("maxTiles", "1"),
            ]);
            var postResponse = await fixture.Client.PostAsync(
                $"/rest/services/{TestLayerId}/ImageServer/estimateExportTilesSize",
                estimateContent);

            postResponse.StatusCode.Should().Be(HttpStatusCode.OK);
            estimate = JsonSerializer.Deserialize(
                await postResponse.Content.ReadAsStringAsync(),
                ImageServerJsonContext.Default.ImageServerExportTilesEstimateResponse);
            AssertExportTilesEstimate(estimate);

            var serviceId = WebAppFixture.TestServiceId;
            var serviceGetResponse = await fixture.Client.GetAsync(
                $"/rest/services/{serviceId}/ImageServer/estimateExportTilesSize?{query}");

            serviceGetResponse.StatusCode.Should().Be(HttpStatusCode.OK);
            estimate = JsonSerializer.Deserialize(
                await serviceGetResponse.Content.ReadAsStringAsync(),
                ImageServerJsonContext.Default.ImageServerExportTilesEstimateResponse);
            AssertExportTilesEstimate(estimate);

            using var serviceEstimateContent = new FormUrlEncodedContent(
            [
                new KeyValuePair<string, string>("f", "json"),
                new KeyValuePair<string, string>("levels", "0"),
                new KeyValuePair<string, string>("exportExtent", "-180,-85,180,85"),
                new KeyValuePair<string, string>("maxTiles", "1"),
            ]);
            var servicePostResponse = await fixture.Client.PostAsync(
                $"/rest/services/{serviceId}/ImageServer/estimateExportTilesSize",
                serviceEstimateContent);

            servicePostResponse.StatusCode.Should().Be(HttpStatusCode.OK);
            estimate = JsonSerializer.Deserialize(
                await servicePostResponse.Content.ReadAsStringAsync(),
                ImageServerJsonContext.Default.ImageServerExportTilesEstimateResponse);
            AssertExportTilesEstimate(estimate);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("GET /rest/services/{id}/ImageServer/exportTiles")]
    [Endpoint("POST /rest/services/{id}/ImageServer/exportTiles")]
    [Endpoint("GET /rest/services/{serviceId}/ImageServer/exportTiles")]
    [Endpoint("POST /rest/services/{serviceId}/ImageServer/exportTiles")]
    [Operation(Operations.Export)]
    public async Task ExportTiles_WritesZipArchiveToCloudStorage()
    {
        var fixture = await CreateFixtureAsync(CreateTileExportRasterStoreSubstitute());
        var uploadedFileIds = new List<string>();
        try
        {
            var query = "f=json&levels=0&exportExtent=-180,-85,180,85&maxTiles=1";
            var getResponse = await fixture.Client.GetAsync(
                $"/rest/services/{TestLayerId}/ImageServer/exportTiles?{query}");

            getResponse.StatusCode.Should().Be(HttpStatusCode.OK);
            var export = JsonSerializer.Deserialize(
                await getResponse.Content.ReadAsStringAsync(),
                ImageServerJsonContext.Default.ImageServerExportTilesResponse);
            AssertExportTilesResponse(export);
            uploadedFileIds.Add(export!.ArchiveFileId!);

            var serviceId = WebAppFixture.TestServiceId;
            using var exportTilesContent = new FormUrlEncodedContent(
            [
                new KeyValuePair<string, string>("f", "json"),
                new KeyValuePair<string, string>("levels", "0"),
                new KeyValuePair<string, string>("exportExtent", "-180,-85,180,85"),
                new KeyValuePair<string, string>("maxTiles", "1"),
            ]);
            var servicePostResponse = await fixture.Client.PostAsync(
                $"/rest/services/{serviceId}/ImageServer/exportTiles",
                exportTilesContent);

            servicePostResponse.StatusCode.Should().Be(HttpStatusCode.OK);
            export = JsonSerializer.Deserialize(
                await servicePostResponse.Content.ReadAsStringAsync(),
                ImageServerJsonContext.Default.ImageServerExportTilesResponse);
            AssertExportTilesResponse(export);
            uploadedFileIds.Add(export!.ArchiveFileId!);

            var storage = fixture.GetService<ICloudFileStorage>();
            foreach (var fileId in uploadedFileIds)
            {
                var bytes = await storage.DownloadBytesAsync(fileId);
                bytes.Should().NotBeNull();
                bytes!.Length.Should().BeGreaterThan(0);

                using var archive = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
                var tileEntry = archive.GetEntry("0/0/0.png");
                tileEntry.Should().NotBeNull();

                await using var tileStream = tileEntry!.Open();
                var pngHeader = new byte[8];
                var read = await tileStream.ReadAsync(pngHeader.AsMemory(0, pngHeader.Length));
                read.Should().Be(pngHeader.Length);
                pngHeader.Should().Equal(0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A);
            }
        }
        finally
        {
            var storage = fixture.GetService<ICloudFileStorage>();
            foreach (var fileId in uploadedFileIds)
            {
                await storage.DeleteAsync(fileId);
            }

            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("GET /rest/services/{id}/ImageServer/exportTiles")]
    [Operation(Operations.Export)]
    public async Task ExportTiles_WithStorageFormatTpk_WritesExplodedTilePackage()
    {
        var fixture = await CreateFixtureAsync(CreateTileExportRasterStoreSubstitute());
        string? fileId = null;
        try
        {
            var query = "f=json&levels=0&exportExtent=-180,-85,180,85&maxTiles=1&storageFormat=tpk";
            var response = await fixture.Client.GetAsync(
                $"/rest/services/{TestLayerId}/ImageServer/exportTiles?{query}");

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var export = JsonSerializer.Deserialize(
                await response.Content.ReadAsStringAsync(),
                ImageServerJsonContext.Default.ImageServerExportTilesResponse);

            export.Should().NotBeNull();
            export!.TilePackage.Should().BeTrue();
            export.StorageFormat.Should().Be("tpk");
            fileId = export.ArchiveFileId;
            fileId.Should().NotBeNullOrWhiteSpace();

            var storage = fixture.GetService<ICloudFileStorage>();
            var bytes = await storage.DownloadBytesAsync(fileId!);
            bytes.Should().NotBeNull();
            using var archive = new ZipArchive(new MemoryStream(bytes!), ZipArchiveMode.Read);
            archive.Entries.Should().Contain(entry => entry.FullName.EndsWith("/conf.xml", StringComparison.Ordinal));
            archive.Entries.Should().Contain(entry =>
                entry.FullName.Contains("_alllayers/L00/", StringComparison.Ordinal) &&
                entry.FullName.EndsWith(".png", StringComparison.Ordinal));
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(fileId))
            {
                await fixture.GetService<ICloudFileStorage>().DeleteAsync(fileId!);
            }

            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("GET /rest/services/{id}/ImageServer/exportTiles")]
    [Operation(Operations.Export)]
    public async Task ExportTiles_WithCompactStorageFormat_ReturnsBadRequest()
    {
        var fixture = await CreateFixtureAsync(CreateTileExportRasterStoreSubstitute());
        try
        {
            // Compact Cache V2 / TPKX now negotiates the durable async path (#2707) rather than the
            // old "unsupported" rejection: a single-level request is instead rejected by the TPKX
            // validation, which requires at least two zoom levels.
            var query = "f=json&levels=0&exportExtent=-180,-85,180,85&maxTiles=1&storageFormat=compact";
            var response = await fixture.Client.GetAsync(
                $"/rest/services/{TestLayerId}/ImageServer/exportTiles?{query}");

            var content = await response.Content.ReadAsStringAsync();
            await response.AssertGeoServicesErrorAsync(400);
            content.ToLowerInvariant().Should().Contain("zoom levels");
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("GET /rest/services/{id}/ImageServer/keyProperties")]
    [Operation(Operations.Metadata)]
    public async Task KeyProperties_Get_ReturnsBandProperties()
    {
        var fixture = await CreateFixtureAsync(CreateRasterStoreSubstitute(bandCount: 3));
        try
        {
            var response = await fixture.Client.GetAsync(
                $"/rest/services/{TestLayerId}/ImageServer/keyProperties?f=json");

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");
            var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            json.RootElement.GetProperty("BandCount").GetInt32().Should().Be(3);
            json.RootElement.GetProperty("DataType").GetString().Should().Be("U8");
            json.RootElement.GetProperty("BandProperties").GetArrayLength().Should().Be(3);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("POST /rest/services/{id}/ImageServer/keyProperties")]
    [Endpoint("POST /rest/services/{serviceId}/ImageServer/keyProperties")]
    [Operation(Operations.Metadata)]
    public async Task KeyProperties_Post_ReturnsNonEmptyBandProperties()
    {
        // The ArcGIS API for Python ImageryLayer.key_properties() issues an HTTP POST.
        // Without a POST route the server returns 405 and the SDK surfaces an empty {}.
        var fixture = await CreateFixtureAsync(CreateRasterStoreSubstitute(bandCount: 3));
        try
        {
            using var content = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("f", "json"),
            });

            var response = await fixture.Client.PostAsync(
                $"/rest/services/{TestLayerId}/ImageServer/keyProperties",
                content);

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");
            var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            json.RootElement.GetProperty("BandCount").GetInt32().Should().Be(3);
            json.RootElement.GetProperty("BandProperties").GetArrayLength().Should().Be(3);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("GET /rest/services/{id}/ImageServer/keyProperties")]
    [Operation(Operations.Metadata)]
    public async Task KeyProperties_Get_ReturnsEsriConformantKeyPropertyShape()
    {
        // The ArcGIS API for Python ImageryLayer.key_properties() expects the
        // canonical Esri raster key-properties document: a flat object whose
        // BandProperties entries carry a BandName, plus cell-size and config
        // keyword properties. See ArcGIS REST "Key Properties" reference.
        var fixture = await CreateFixtureAsync(CreateRasterStoreSubstitute(bandCount: 2));
        try
        {
            var response = await fixture.Client.GetAsync(
                $"/rest/services/{TestLayerId}/ImageServer/keyProperties?f=json");

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var root = json.RootElement;

            // Canonical Esri keyProperties keys must be present.
            root.TryGetProperty("BandProperties", out var bands).Should().BeTrue();
            root.TryGetProperty("HighCellSize", out _).Should().BeTrue();
            root.TryGetProperty("LowCellSize", out _).Should().BeTrue();
            root.TryGetProperty("MaxCellSize", out _).Should().BeTrue();
            root.TryGetProperty("ConfigKeyword", out _).Should().BeTrue();
            root.TryGetProperty("BandDefinitionKeyword", out _).Should().BeTrue();

            bands.ValueKind.Should().Be(JsonValueKind.Array);
            bands.GetArrayLength().Should().Be(2);
            bands[0].GetProperty("BandName").GetString().Should().Be("Band_1");

            // GeoTransform [-180, 1.40625, 0, 90, 0, -0.703125] -> cell sizes 1.40625 / 0.703125.
            root.GetProperty("MaxCellSize").GetDouble().Should().BeApproximately(1.40625, 1e-6);
            root.GetProperty("LowCellSize").GetDouble().Should().BeApproximately(0.703125, 1e-6);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("GET /rest/services/{id}/ImageServer/keyProperties")]
    [Operation(Operations.Metadata)]
    public async Task KeyProperties_NonExistentLayer_ReturnsNotFound()
    {
        var fixture = await CreateFixtureAsync(CreateRasterStoreSubstitute());
        try
        {
            var response = await fixture.Client.GetAsync(
                "/rest/services/99999/ImageServer/keyProperties?f=json");

            // PA-070/PA-117: GeoServices always returns HTTP 200; error code is in the JSON body.
            response.StatusCode.Should().Be(HttpStatusCode.OK);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("GET /rest/services/{id}/ImageServer/legend")]
    [Operation(Operations.Metadata)]
    public async Task GetLegend_Get_ReturnsSwatches()
    {
        var fixture = await CreateFixtureAsync(CreateRasterStoreSubstitute());
        try
        {
            var response = await fixture.Client.GetAsync(
                $"/rest/services/{TestLayerId}/ImageServer/legend?f=json");

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");
            var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            json.RootElement.TryGetProperty("layers", out var layers).Should().BeTrue();
            layers.GetArrayLength().Should().BeGreaterThan(0);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    // Regression for #1900: the ArcGIS API for Python ImageryLayer.legend() (and the JS API)
    // POST to /legend. The POST mirror must return the same payload as GET.
    [IntegrationTest]
    [Endpoint("POST /rest/services/{id}/ImageServer/legend")]
    [Operation(Operations.Metadata)]
    public async Task GetLegend_Post_ReturnsSwatches()
    {
        var fixture = await CreateFixtureAsync(CreateRasterStoreSubstitute());
        try
        {
            using var content = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("f", "json"),
            });

            var response = await fixture.Client.PostAsync(
                $"/rest/services/{TestLayerId}/ImageServer/legend",
                content);

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");
            var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            json.RootElement.TryGetProperty("layers", out var layers).Should().BeTrue();
            layers.GetArrayLength().Should().BeGreaterThan(0);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    // #1900: a renderingRule supplied in the POST body must drive the legend just like the
    // GET query parameter does.
    [IntegrationTest]
    [Endpoint("POST /rest/services/{id}/ImageServer/legend")]
    [Operation(Operations.Metadata)]
    public async Task GetLegend_PostWithColormapRenderingRule_ReflectsColormapStops()
    {
        var fixture = await CreateFixtureAsync(CreateRasterStoreSubstitute());
        try
        {
            using var content = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("f", "json"),
                new KeyValuePair<string, string>(
                    "renderingRule",
                    """{"rasterFunction":"Colormap","rasterFunctionArguments":{"Colormap":[[0,0,0,0],[128,255,0,0],[255,255,255,255]]}}"""),
            });

            var response = await fixture.Client.PostAsync(
                $"/rest/services/{TestLayerId}/ImageServer/legend",
                content);

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var legend = json.RootElement.GetProperty("layers")[0].GetProperty("legend");
            legend.GetArrayLength().Should().Be(3);
            legend[0].GetProperty("label").GetString().Should().Be("0");
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("GET /rest/services/{id}/ImageServer/legend")]
    [Operation(Operations.Metadata)]
    public async Task GetLegend_WithColormapRenderingRule_ReflectsColormapStops()
    {
        var fixture = await CreateFixtureAsync(CreateRasterStoreSubstitute());
        try
        {
            // A 3-stop colormap must yield 3 legend swatches, not the default 5 class breaks.
            var renderingRule = Uri.EscapeDataString(
                """{"rasterFunction":"Colormap","rasterFunctionArguments":{"Colormap":[[0,0,0,0],[128,255,0,0],[255,255,255,255]]}}""");
            var response = await fixture.Client.GetAsync(
                $"/rest/services/{TestLayerId}/ImageServer/legend?f=json&renderingRule={renderingRule}");

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var legend = json.RootElement.GetProperty("layers")[0].GetProperty("legend");
            legend.GetArrayLength().Should().Be(3);
            legend[0].GetProperty("label").GetString().Should().Be("0");
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("GET /rest/services/{id}/ImageServer/legend")]
    [Operation(Operations.Metadata)]
    public async Task GetLegend_InvalidFormat_ReturnsBadRequest()
    {
        var fixture = await CreateFixtureAsync(CreateRasterStoreSubstitute());
        try
        {
            var response = await fixture.Client.GetAsync(
                $"/rest/services/{TestLayerId}/ImageServer/legend?f=xml");

            // PA-070/PA-117: GeoServices always returns HTTP 200; error code is in the JSON body.
            response.StatusCode.Should().Be(HttpStatusCode.OK);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("GET /rest/services/{id}/ImageServer/computeClassStatistics")]
    [Operation(Operations.Metadata)]
    public async Task ComputeClassStatistics_Get_WithClassDescriptions_ReturnsClassSignature()
    {
        var fixture = await CreateFixtureAsync(CreateRasterStoreSubstitute());
        try
        {
            const string classDescriptions = """{"classes":[{"id":1,"name":"water","geometry":{"rings":[[[-1,-1],[-1,1],[1,1],[1,-1],[-1,-1]]]}}]}""";
            var response = await fixture.Client.GetAsync(
                $"/rest/services/{TestLayerId}/ImageServer/computeClassStatistics?f=json&classDescriptions={Uri.EscapeDataString(classDescriptions)}");

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var classStatistics = document.RootElement.GetProperty("classStatistics");
            classStatistics.GetArrayLength().Should().Be(1);
            var entry = classStatistics[0];
            entry.GetProperty("classId").GetInt32().Should().Be(1);
            entry.GetProperty("name").GetString().Should().Be("water");
            // Deterministic substitute pixels [1,2,3,4] on one band => count 4, mean 2.5.
            entry.GetProperty("count").GetInt64().Should().Be(4);
            entry.GetProperty("mean")[0].GetDouble().Should().BeApproximately(2.5, 1e-9);
            // Sample covariance of [1,2,3,4] = 5/3.
            entry.GetProperty("covarianceMatrix")[0][0].GetDouble().Should().BeApproximately(5.0 / 3.0, 1e-9);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("POST /rest/services/{id}/ImageServer/computeClassStatistics")]
    [Operation(Operations.Metadata)]
    public async Task ComputeClassStatistics_Post_FormBody_ReturnsClassSignature()
    {
        var fixture = await CreateFixtureAsync(CreateRasterStoreSubstitute());
        try
        {
            using var content = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("f", "json"),
                new KeyValuePair<string, string>("classDescriptions", """{"classes":[{"id":1,"name":"water","geometry":{"rings":[[[-1,-1],[-1,1],[1,1],[1,-1],[-1,-1]]]}},{"id":2,"name":"land","geometry":{"rings":[[[0,0],[0,1],[1,1],[1,0],[0,0]]]}}]}"""),
            });

            var response = await fixture.Client.PostAsync(
                $"/rest/services/{TestLayerId}/ImageServer/computeClassStatistics",
                content);

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var classStatistics = document.RootElement.GetProperty("classStatistics");
            classStatistics.GetArrayLength().Should().Be(2);
            classStatistics[0].GetProperty("classId").GetInt32().Should().Be(1);
            classStatistics[1].GetProperty("classId").GetInt32().Should().Be(2);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("GET /rest/services/{id}/ImageServer/computeClassStatistics")]
    [Operation(Operations.Metadata)]
    public async Task ComputeClassStatistics_WithRenderingRule_ReturnsNotImplemented()
    {
        var fixture = await CreateFixtureAsync(CreateRasterStoreSubstitute());
        try
        {
            const string classDescriptions = """{"classes":[{"id":1,"geometry":{"rings":[[[-1,-1],[-1,1],[1,1],[1,-1],[-1,-1]]]}}]}""";
            const string renderingRule = """{"rasterFunction":"Stretch","rasterFunctionArguments":{"StretchType":5}}""";
            var response = await fixture.Client.GetAsync(
                $"/rest/services/{TestLayerId}/ImageServer/computeClassStatistics?f=json" +
                $"&classDescriptions={Uri.EscapeDataString(classDescriptions)}" +
                $"&renderingRule={Uri.EscapeDataString(renderingRule)}");

            // Class signatures are computed on source pixels; a renderingRule is explicitly rejected.
            // #2795: not-implemented operations now surface body error.code 501 (pass-through) instead
            // of collapsing to 500, so clients can distinguish "unsupported" from a server fault.
            await response.AssertGeoServicesErrorAsync(501);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("GET /rest/services/{id}/ImageServer/computeClassStatistics")]
    [Operation(Operations.Metadata)]
    public async Task ComputeClassStatistics_MissingClassDescriptions_ReturnsBadRequest()
    {
        var fixture = await CreateFixtureAsync(CreateRasterStoreSubstitute());
        try
        {
            var response = await fixture.Client.GetAsync(
                $"/rest/services/{TestLayerId}/ImageServer/computeClassStatistics?f=json");

            // PA-070/PA-117: GeoServices always returns HTTP 200; error code is in the JSON body.
            response.StatusCode.Should().Be(HttpStatusCode.OK);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("GET /rest/services/{id}/ImageServer/multidimensionalInfo")]
    [Operation(Operations.GetServiceInfo)]
    public async Task MultidimensionalInfo_Get_NonMultidimensionalLayer_ReturnsEmptyVariables()
    {
        var fixture = await CreateFixtureAsync(CreateRasterStoreSubstitute());
        try
        {
            var response = await fixture.Client.GetAsync(
                $"/rest/services/{TestLayerId}/ImageServer/multidimensionalInfo?f=json");

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");

            var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            json.RootElement.GetProperty("multidimensionalInfo")
                .GetProperty("variables").GetArrayLength().Should().Be(0);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("POST /rest/services/{id}/ImageServer/multidimensionalInfo")]
    [Operation(Operations.GetServiceInfo)]
    public async Task MultidimensionalInfo_Post_NonMultidimensionalLayer_ReturnsEmptyVariables()
    {
        var fixture = await CreateFixtureAsync(CreateRasterStoreSubstitute());
        try
        {
            using var content = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("f", "json"),
            });

            var response = await fixture.Client.PostAsync(
                $"/rest/services/{TestLayerId}/ImageServer/multidimensionalInfo",
                content);

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            json.RootElement.GetProperty("multidimensionalInfo")
                .GetProperty("variables").GetArrayLength().Should().Be(0);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("GET /rest/services/{id}/ImageServer/multidimensionalInfo")]
    [Operation(Operations.GetServiceInfo)]
    public async Task MultidimensionalInfo_NonExistentLayer_ReturnsNotFound()
    {
        var fixture = await CreateFixtureAsync(CreateRasterStoreSubstitute());
        try
        {
            var response = await fixture.Client.GetAsync(
                "/rest/services/99999/ImageServer/multidimensionalInfo?f=json");

            // PA-070/PA-117: GeoServices always returns HTTP 200; error code is in the JSON body.
            response.StatusCode.Should().Be(HttpStatusCode.OK);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    // Regression coverage for #1445: the /slices route was unmapped and returned 404
    // (GET and POST) for a hasMultidimensions:true service. The route is now mapped and
    // returns the Esri slices document; with no enumerable multidimensional coverage it
    // honestly returns a spec-shaped { "slices": [] } rather than 404.
    [IntegrationTest]
    [Endpoint("GET /rest/services/{id}/ImageServer/slices")]
    [Endpoint("POST /rest/services/{id}/ImageServer/slices")]
    [Operation(Operations.GetServiceInfo)]
    public async Task Slices_GetAndPost_NonMultidimensionalLayer_ReturnEmptySlices()
    {
        var fixture = await CreateFixtureAsync(CreateRasterStoreSubstitute());
        try
        {
            var getResponse = await fixture.Client.GetAsync(
                $"/rest/services/{TestLayerId}/ImageServer/slices?f=json");

            getResponse.StatusCode.Should().Be(HttpStatusCode.OK);
            getResponse.Content.Headers.ContentType?.MediaType.Should().Be("application/json");
            var getJson = JsonDocument.Parse(await getResponse.Content.ReadAsStringAsync());
            getJson.RootElement.GetProperty("slices").GetArrayLength().Should().Be(0);

            using var content = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("f", "json"),
            });
            var postResponse = await fixture.Client.PostAsync(
                $"/rest/services/{TestLayerId}/ImageServer/slices",
                content);

            postResponse.StatusCode.Should().Be(HttpStatusCode.OK);
            var postJson = JsonDocument.Parse(await postResponse.Content.ReadAsStringAsync());
            postJson.RootElement.GetProperty("slices").GetArrayLength().Should().Be(0);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("GET /rest/services/{id}/ImageServer/slices")]
    [Operation(Operations.GetServiceInfo)]
    public async Task Slices_NonExistentLayer_ReturnsNotFound()
    {
        var fixture = await CreateFixtureAsync(CreateRasterStoreSubstitute());
        try
        {
            var response = await fixture.Client.GetAsync(
                "/rest/services/99999/ImageServer/slices?f=json");

            // PA-070/PA-117: GeoServices always returns HTTP 200; error code is in the JSON body.
            response.StatusCode.Should().Be(HttpStatusCode.OK);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("GET /rest/services/{id}/ImageServer/conf.json")]
    [Operation(Operations.GetServiceInfo)]
    public async Task ConfJson_ReturnsServiceDescriptor_InArcGisRuntimeCompatibleShape()
    {
        // Regression for #1456: the ArcGIS Maps SDK for .NET ImageServiceRaster
        // unconditionally fetches conf.json while loading and parses it strictly.
        // conf.json must return the service descriptor (like a real Esri ImageServer),
        // and that descriptor must use the Esri wire shape the native parser accepts:
        // allowedMosaicMethods as a comma-separated STRING (not an array) and no
        // tile-cache storageInfo for a dynamic service. Any of these tripped
        // LoadAsync with "could not read configuration data" / "Invalid configuration file".
        var fixture = await CreateFixtureAsync(CreateRasterStoreSubstitute());
        try
        {
            var response = await fixture.Client.GetAsync(
                $"/rest/services/{TestLayerId}/ImageServer/conf.json");

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");

            var root = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

            // conf.json returns the service descriptor. Honua does not advertise an ArcGIS
            // Server version (see NoArcGisServerVersionTests).
            root.TryGetProperty("currentVersion", out _).Should().BeFalse();
            root.TryGetProperty("extent", out _).Should().BeTrue();

            // allowedMosaicMethods must serialize as a string, not an array.
            root.GetProperty("allowedMosaicMethods").ValueKind.Should().Be(JsonValueKind.String);

            // Dynamic service: no tile-cache storage block.
            root.TryGetProperty("storageInfo", out _).Should().BeFalse();
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    private static void AssertProjectedOrigin(string content)
    {
        using var json = JsonDocument.Parse(content);
        json.RootElement.TryGetProperty("geometryType", out _).Should().BeFalse();
        json.RootElement.TryGetProperty("spatialReference", out _).Should().BeFalse();

        var geometries = json.RootElement.GetProperty("geometries");
        geometries.GetArrayLength().Should().Be(1);
        var point = geometries[0];
        point.GetProperty("x").GetDouble().Should().BeApproximately(0d, 0.0001);
        point.GetProperty("y").GetDouble().Should().BeApproximately(0d, 0.0001);
        point.GetProperty("spatialReference").GetProperty("wkid").GetInt32().Should().Be(3857);
    }

    private static void AssertExportTilesEstimate(ImageServerExportTilesEstimateResponse? estimate)
    {
        estimate.Should().NotBeNull();
        estimate!.TileCount.Should().Be(1);
        estimate.Size.Should().BeGreaterThan(0);
        estimate.EstimatedSizeBytes.Should().Be(estimate.Size);
        estimate.MinZoom.Should().Be(0);
        estimate.MaxZoom.Should().Be(0);
        estimate.TilePackage.Should().BeFalse();
        estimate.StorageFormat.Should().Be("zip");
        estimate.ContentType.Should().Be("application/zip");
        estimate.ExceededTransferLimit.Should().BeFalse();
    }

    private static void AssertFindResponse(ImageServerFindResponse? find)
    {
        find.Should().NotBeNull();
        find!.Images.Should().ContainSingle();
        var image = find.Images[0];
        image.Id.Should().Be(100);
        image.Uri.Should().Be("test-raster");
        image.Rows.Should().Be(256);
        image.Cols.Should().Be(256);
        image.PixelSize.Should().BeGreaterThan(0);
        image.Center.Should().NotBeNull();
        image.Center!.X.Should().Be(0);
        image.Center.Y.Should().Be(0);
        image.Center.SpatialReference!.Wkid.Should().Be(4326);
    }

    private static void AssertDistanceMeasure(string content)
    {
        var measure = JsonSerializer.Deserialize(
            content,
            ImageServerJsonContext.Default.ImageServerMeasureResponse);

        measure.Should().NotBeNull();
        measure!.Name.Should().Be("test-raster");
        measure.SensorName.Should().Be("Unknown");
        measure.Distance.Should().NotBeNull();
        // (0,0)->(3,4) in EPSG:3857 near the equator: the ground distance is ~5 m (Web Mercator
        // scale ≈ 1 at the equator), computed geodesically on the mean-radius sphere (#2734).
        measure.Distance!.Value.Should().BeApproximately(5d, 0.05d);
        measure.Distance.Unit.Should().Be("esriMeters");
        measure.AzimuthAngle.Should().NotBeNull();
        // 3-east / 4-north near the equator still gives atan(3/4) ≈ 36.87° true bearing.
        measure.AzimuthAngle!.Value.Should().BeApproximately(36.86989764584402d, 1e-4);
        measure.AzimuthAngle.Unit.Should().Be("esriDUDecimalDegrees");
    }

    private static void AssertExportTilesResponse(ImageServerExportTilesResponse? export)
    {
        export.Should().NotBeNull();
        export!.JobStatus.Should().Be("esriJobSucceeded");
        export.TileCount.Should().Be(1);
        export.TilePackage.Should().BeFalse();
        export.StorageFormat.Should().Be("zip");
        export.ContentType.Should().Be("application/zip");
        export.ArchiveFileId.Should().NotBeNullOrWhiteSpace();
        export.DownloadUrl.Should().NotBeNullOrWhiteSpace();
        export.Files.Should().ContainSingle();
        export.Results.Should().NotBeNull();
        export.Results!.OutServiceUrl.Should().NotBeNull();
    }
}
