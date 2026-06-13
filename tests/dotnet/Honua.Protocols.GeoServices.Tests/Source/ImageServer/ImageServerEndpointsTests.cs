// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.IO.Compression;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.Domain;
using Honua.Protocols.GeoServices.ImageServer.Models;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
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
        store.GetStatisticsAsync(Arg.Any<int>(), Arg.Any<long>(), Arg.Any<int[]?>(), Arg.Any<CancellationToken>())
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
        store.GetHistogramsAsync(Arg.Any<int>(), Arg.Any<long>(), Arg.Any<int[]?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
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
        store.GetClippedStatisticsAsync(Arg.Any<int>(), Arg.Any<long>(), Arg.Any<byte[]>(), Arg.Any<int?>(), Arg.Any<int[]?>(), Arg.Any<CancellationToken>())
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
        store.GetClippedHistogramsAsync(Arg.Any<int>(), Arg.Any<long>(), Arg.Any<byte[]>(), Arg.Any<int?>(), Arg.Any<int[]?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
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
        return store;
    }

    private static async Task<WebAppFixture> CreateFixtureAsync(IRasterStore rasterStore)
    {
        var fixture = new WebAppFixture()
            .ConfigureServices(services => services.AddSingleton(rasterStore));
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

            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
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
            var renderingRule = Uri.EscapeDataString("""{"rasterFunction":"Hillshade"}""");
            var response = await fixture.Client.GetAsync(
                $"/rest/services/{TestLayerId}/ImageServer/exportImage?f=image&bbox=-180,-90,180,90&renderingRule={renderingRule}");

            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
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
    public async Task Wmts_GetTile_WithUnsupportedFormat_ReturnsXmlException()
    {
        var fixture = await CreateFixtureAsync(CreateTileExportRasterStoreSubstitute());
        try
        {
            var response = await fixture.Client.GetAsync(
                $"/rest/services/{TestLayerId}/ImageServer/WMTS?SERVICE=WMTS&REQUEST=GetTile&VERSION=1.0.0&LAYER={TestLayerId}&STYLE=default&FORMAT=image/gif&TILEMATRIXSET=WebMercatorQuad&TILEMATRIX=0&TILEROW=0&TILECOL=0");

            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            response.Content.Headers.ContentType?.MediaType.Should().Be("application/xml");
            var content = await response.Content.ReadAsStringAsync();
            content.Should().Contain("InvalidParameterValue");
            content.Should().Contain("FORMAT must be image/png or image/jpeg.");
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
    [Endpoint("POST /rest/services/{id}/ImageServer/query")]
    [Operation(Operations.Query)]
    public async Task QueryCatalog_Post_FormBody_ReturnsFeatureCollection()
    {
        var fixture = await CreateFixtureAsync(CreateRasterStoreSubstitute());
        try
        {
            var content = new FormUrlEncodedContent(new[]
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

            response.StatusCode.Should().Be(HttpStatusCode.NotFound);
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

            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
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

            var postResponse = await fixture.Client.PostAsync(
                $"/rest/services/{TestLayerId}/ImageServer/find",
                new FormUrlEncodedContent(
                [
                    new KeyValuePair<string, string>("f", "json"),
                    new KeyValuePair<string, string>("toGeometry", targetGeometry),
                    new KeyValuePair<string, string>("maxCount", "1"),
                ]));

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

            var servicePostResponse = await fixture.Client.PostAsync(
                $"/rest/services/{serviceId}/ImageServer/find",
                new FormUrlEncodedContent(
                [
                    new KeyValuePair<string, string>("f", "json"),
                    new KeyValuePair<string, string>("toGeometry", targetGeometry),
                    new KeyValuePair<string, string>("objectIds", "[100]"),
                ]));

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
    public async Task Find_MissingToGeometry_ReturnsBadRequest()
    {
        var fixture = await CreateFixtureAsync(CreateRasterStoreSubstitute());
        try
        {
            var response = await fixture.Client.GetAsync(
                $"/rest/services/{TestLayerId}/ImageServer/find?f=json");

            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
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

            var postResponse = await fixture.Client.PostAsync(
                $"/rest/services/{TestLayerId}/ImageServer/measure",
                new FormUrlEncodedContent(
                [
                    new KeyValuePair<string, string>("f", "json"),
                    new KeyValuePair<string, string>("measureOperation", "esriMensurationDistanceAndAngle"),
                    new KeyValuePair<string, string>("geometryType", "esriGeometryPoint"),
                    new KeyValuePair<string, string>("fromGeometry", fromGeometry),
                    new KeyValuePair<string, string>("toGeometry", toGeometry),
                    new KeyValuePair<string, string>("linearUnit", "esriMeters"),
                    new KeyValuePair<string, string>("angularUnit", "esriDUDecimalDegrees"),
                ]));

            postResponse.StatusCode.Should().Be(HttpStatusCode.OK);
            AssertDistanceMeasure(await postResponse.Content.ReadAsStringAsync());

            var serviceId = WebAppFixture.TestServiceId;
            var serviceGetResponse = await fixture.Client.GetAsync(
                $"/rest/services/{serviceId}/ImageServer/measure?{query}");

            serviceGetResponse.StatusCode.Should().Be(HttpStatusCode.OK);
            AssertDistanceMeasure(await serviceGetResponse.Content.ReadAsStringAsync());

            var servicePostResponse = await fixture.Client.PostAsync(
                $"/rest/services/{serviceId}/ImageServer/measure",
                new FormUrlEncodedContent(
                [
                    new KeyValuePair<string, string>("f", "json"),
                    new KeyValuePair<string, string>("measureOperation", "esriMensurationDistanceAndAngle"),
                    new KeyValuePair<string, string>("geometryType", "esriGeometryPoint"),
                    new KeyValuePair<string, string>("fromGeometry", fromGeometry),
                    new KeyValuePair<string, string>("toGeometry", toGeometry),
                ]));

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
            measure.Area!.Value.Should().BeApproximately(50d, 1e-9);
            measure.Area.Unit.Should().Be("esriSquareMeters");
            measure.Perimeter.Should().NotBeNull();
            measure.Perimeter!.Value.Should().BeApproximately(30d, 1e-9);
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
    public async Task Measure_HeightOperation_ReturnsNotImplemented()
    {
        var fixture = await CreateFixtureAsync(CreateRasterStoreSubstitute());
        try
        {
            const string fromGeometry = """{"x":0,"y":0,"spatialReference":{"wkid":3857}}""";
            const string toGeometry = """{"x":3,"y":4,"spatialReference":{"wkid":3857}}""";
            var response = await fixture.Client.GetAsync(
                $"/rest/services/{TestLayerId}/ImageServer/measure?f=json&measureOperation=esriMensurationHeightFromBaseAndTop&geometryType=esriGeometryPoint&fromGeometry={Uri.EscapeDataString(fromGeometry)}&toGeometry={Uri.EscapeDataString(toGeometry)}");

            response.StatusCode.Should().Be(HttpStatusCode.NotImplemented);
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

            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
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
                Arg.Any<int>(), Arg.Any<long>(), Arg.Is<byte[]>(g => g.Length > 0), Arg.Any<int?>(), Arg.Any<int[]?>(), Arg.Any<CancellationToken>());
            await store.DidNotReceive().GetStatisticsAsync(
                Arg.Any<int>(), Arg.Any<long>(), Arg.Any<int[]?>(), Arg.Any<CancellationToken>());
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
            var content = new FormUrlEncodedContent(new[]
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
            var content = new FormUrlEncodedContent(new[]
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

            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
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
            var content = new FormUrlEncodedContent(new[]
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

            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
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

            var postResponse = await fixture.Client.PostAsync(
                $"/rest/services/{TestLayerId}/ImageServer/computeCacheInfo",
                new FormUrlEncodedContent([new KeyValuePair<string, string>("f", "json")]));

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

            var postResponse = await fixture.Client.PostAsync(
                $"/rest/services/{TestLayerId}/ImageServer/computePixelLocation",
                new FormUrlEncodedContent(
                [
                    new KeyValuePair<string, string>("f", "json"),
                    new KeyValuePair<string, string>("geometries", geometries),
                ]));

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

            var postResponse = await fixture.Client.PostAsync(
                $"/rest/services/{TestLayerId}/ImageServer/queryBoundary",
                new FormUrlEncodedContent([new KeyValuePair<string, string>("f", "json")]));

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

            var postResponse = await fixture.Client.PostAsync(
                $"/rest/services/{TestLayerId}/ImageServer/project",
                new FormUrlEncodedContent(
                [
                    new KeyValuePair<string, string>("f", "json"),
                    new KeyValuePair<string, string>("inSR", "4326"),
                    new KeyValuePair<string, string>("outSR", "3857"),
                    new KeyValuePair<string, string>("geometries", geometries),
                ]));

            postResponse.StatusCode.Should().Be(HttpStatusCode.OK);
            AssertProjectedOrigin(await postResponse.Content.ReadAsStringAsync());

            var serviceId = WebAppFixture.TestServiceId;
            var serviceGetResponse = await fixture.Client.GetAsync(
                $"/rest/services/{serviceId}/ImageServer/project?f=json&inSR=4326&outSR=3857&geometries={encoded}");

            serviceGetResponse.StatusCode.Should().Be(HttpStatusCode.OK);
            AssertProjectedOrigin(await serviceGetResponse.Content.ReadAsStringAsync());

            var servicePostResponse = await fixture.Client.PostAsync(
                $"/rest/services/{serviceId}/ImageServer/project",
                new FormUrlEncodedContent(
                [
                    new KeyValuePair<string, string>("f", "json"),
                    new KeyValuePair<string, string>("inSR", "4326"),
                    new KeyValuePair<string, string>("outSR", "3857"),
                    new KeyValuePair<string, string>("geometries", geometries),
                ]));

            servicePostResponse.StatusCode.Should().Be(HttpStatusCode.OK);
            AssertProjectedOrigin(await servicePostResponse.Content.ReadAsStringAsync());
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

            var postResponse = await fixture.Client.PostAsync(
                $"/rest/services/{TestLayerId}/ImageServer/estimateExportTilesSize",
                new FormUrlEncodedContent(
                [
                    new KeyValuePair<string, string>("f", "json"),
                    new KeyValuePair<string, string>("levels", "0"),
                    new KeyValuePair<string, string>("exportExtent", "-180,-85,180,85"),
                    new KeyValuePair<string, string>("maxTiles", "1"),
                ]));

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

            var servicePostResponse = await fixture.Client.PostAsync(
                $"/rest/services/{serviceId}/ImageServer/estimateExportTilesSize",
                new FormUrlEncodedContent(
                [
                    new KeyValuePair<string, string>("f", "json"),
                    new KeyValuePair<string, string>("levels", "0"),
                    new KeyValuePair<string, string>("exportExtent", "-180,-85,180,85"),
                    new KeyValuePair<string, string>("maxTiles", "1"),
                ]));

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
            var servicePostResponse = await fixture.Client.PostAsync(
                $"/rest/services/{serviceId}/ImageServer/exportTiles",
                new FormUrlEncodedContent(
                [
                    new KeyValuePair<string, string>("f", "json"),
                    new KeyValuePair<string, string>("levels", "0"),
                    new KeyValuePair<string, string>("exportExtent", "-180,-85,180,85"),
                    new KeyValuePair<string, string>("maxTiles", "1"),
                ]));

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
            var content = new FormUrlEncodedContent(new[]
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

            response.StatusCode.Should().Be(HttpStatusCode.NotFound);
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

            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("GET /rest/services/{id}/ImageServer/computeClassStatistics")]
    [Operation(Operations.Metadata)]
    public async Task ComputeClassStatistics_Get_WithClassDescriptions_ReturnsNotImplemented()
    {
        var fixture = await CreateFixtureAsync(CreateRasterStoreSubstitute());
        try
        {
            const string classDescriptions = """{"classes":[{"id":1,"name":"water","geometry":{"rings":[[[-1,-1],[-1,1],[1,1],[1,-1],[-1,-1]]]}}]}""";
            var response = await fixture.Client.GetAsync(
                $"/rest/services/{TestLayerId}/ImageServer/computeClassStatistics?f=json&classDescriptions={Uri.EscapeDataString(classDescriptions)}");

            response.StatusCode.Should().Be(HttpStatusCode.NotImplemented);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("POST /rest/services/{id}/ImageServer/computeClassStatistics")]
    [Operation(Operations.Metadata)]
    public async Task ComputeClassStatistics_Post_FormBody_ReturnsNotImplemented()
    {
        var fixture = await CreateFixtureAsync(CreateRasterStoreSubstitute());
        try
        {
            var content = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("f", "json"),
                new KeyValuePair<string, string>("classDescriptions", """{"classes":[{"id":1,"name":"water","geometry":{"rings":[[[-1,-1],[-1,1],[1,1],[1,-1],[-1,-1]]]}}]}"""),
            });

            var response = await fixture.Client.PostAsync(
                $"/rest/services/{TestLayerId}/ImageServer/computeClassStatistics",
                content);

            response.StatusCode.Should().Be(HttpStatusCode.NotImplemented);
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

            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
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
            var content = new FormUrlEncodedContent(new[]
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

            response.StatusCode.Should().Be(HttpStatusCode.NotFound);
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

            var content = new FormUrlEncodedContent(new[]
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

            response.StatusCode.Should().Be(HttpStatusCode.NotFound);
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

            // conf.json returns the service descriptor.
            root.TryGetProperty("currentVersion", out _).Should().BeTrue();
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
        measure.Distance!.Value.Should().BeApproximately(5d, 1e-9);
        measure.Distance.Unit.Should().Be("esriMeters");
        measure.AzimuthAngle.Should().NotBeNull();
        measure.AzimuthAngle!.Value.Should().BeApproximately(36.86989764584402d, 1e-9);
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
