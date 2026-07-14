// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.TestKit.Infrastructure;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.Domain;
using Honua.Protocols.GeoServices.ImageServer.Handlers;
using Honua.Protocols.GeoServices.ImageServer.Models;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.ImageServer;

/// <summary>
/// Tests for ImageServerIdentifyHandler functionality.
/// </summary>
[Protocol(TestProtocols.ImageServer)]
public class ImageServerIdentifyHandlerTests
{
    private readonly TestMetadataV2GraphProvider _graphProvider = BuildGraphWithLayer(1);
    private readonly IRasterStore _rasterStore = Substitute.For<IRasterStore>();
    private readonly IZarrPointSliceReader _zarrPointSliceReader = Substitute.For<IZarrPointSliceReader>();
    private readonly ImageServerIdentifyHandler _handler;

    public ImageServerIdentifyHandlerTests()
    {
        _handler = new ImageServerIdentifyHandler(
            _graphProvider,
            _rasterStore,
            _zarrPointSliceReader,
            NullLogger<ImageServerIdentifyHandler>.Instance);
    }

    [UnitTest]
    [Operation(Operations.Identify)]
    public async Task IdentifyAsync_LayerNotFound_ReturnsNotFound()
    {

        var context = CreateImageServerContext();
        var request = CreateRequest("10,20");
        var result = await _handler.IdentifyAsync(context, 99, request);
        await AssertGeoServicesErrorAsync(context, result, StatusCodes.Status404NotFound);
    }

    [UnitTest]
    [Operation(Operations.Identify)]
    public async Task IdentifyAsync_NoRasters_ReturnsOkNoData()
    {
        // ArcGIS ImageServer identify returns a 200 NoData document (not 404) when the
        // requested location does not intersect any raster, matching getSamples. Returning
        // 404 broke imageService.identify for out-of-extent points in the Esri-SDK matrix.
        // POST /rest/services/0/ImageServer/identify
        // GET /rest/services/0/ImageServer/identify
        _rasterStore.QueryRastersAsync(default, default, default)
            .ReturnsForAnyArgs(Array.Empty<RasterInfo>());

        var context = CreateImageServerContext();
        var request = CreateRequest("10,20");
        var result = await _handler.IdentifyAsync(context, 1, request);
        await result.ExecuteAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        context.Response.Body.Position = 0;

        using var responseJson = await JsonDocument.ParseAsync(context.Response.Body);
        responseJson.RootElement.GetProperty("value").GetString().Should().Be("NoData");
        responseJson.RootElement.GetProperty("properties").GetProperty("HasData").GetBoolean().Should().BeFalse();
        responseJson.RootElement.GetProperty("location").GetProperty("x").GetDouble().Should().Be(10);
        responseJson.RootElement.GetProperty("location").GetProperty("y").GetDouble().Should().Be(20);
    }

    [UnitTest]
    [Operation(Operations.Identify)]
    public async Task IdentifyAsync_InvalidGeometryString_ReturnsBadRequest()
    {
        _rasterStore.GetPrimaryRasterInfoAsync(1, Arg.Any<CancellationToken>())
            .Returns(CreateTestRasterInfo());

        var context = CreateImageServerContext();
        var request = CreateRequest("invalid-geometry");
        var result = await _handler.IdentifyAsync(context, 1, request);
        await AssertGeoServicesErrorAsync(context, result, StatusCodes.Status400BadRequest);
    }

    [UnitTest]
    [Operation(Operations.Identify)]
    public async Task IdentifyAsync_CommaGeometryWithExtraCoordinate_ReturnsBadRequest()
    {
        _rasterStore.GetPrimaryRasterInfoAsync(1, Arg.Any<CancellationToken>())
            .Returns(CreateTestRasterInfo());

        var context = CreateImageServerContext();
        var request = CreateRequest("10,20,30");
        var result = await _handler.IdentifyAsync(context, 1, request);
        await AssertGeoServicesErrorAsync(context, result, StatusCodes.Status400BadRequest);
        await _rasterStore.DidNotReceive()
            .IdentifyAsync(1, 100, Arg.Any<double>(), Arg.Any<double>(), Arg.Any<int?>(), Arg.Any<RasterIdentifyRendering?>(), Arg.Any<CancellationToken>());
    }

    [UnitTest]
    [Operation(Operations.Identify)]
    public async Task IdentifyAsync_CommaGeometryExceedingLimit_ReturnsBadRequest()
    {
        _rasterStore.GetPrimaryRasterInfoAsync(1, Arg.Any<CancellationToken>())
            .Returns(CreateTestRasterInfo());

        var oversizedGeometry = $"10,20,{new string('x', 1200)}";
        var context = CreateImageServerContext();
        var request = CreateRequest(oversizedGeometry);
        var result = await _handler.IdentifyAsync(context, 1, request);
        await AssertGeoServicesErrorAsync(context, result, StatusCodes.Status400BadRequest);
        await _rasterStore.DidNotReceive()
            .IdentifyAsync(1, 100, Arg.Any<double>(), Arg.Any<double>(), Arg.Any<int?>(), Arg.Any<RasterIdentifyRendering?>(), Arg.Any<CancellationToken>());
    }

    [UnitTest]
    [Operation(Operations.Identify)]
    public async Task IdentifyAsync_InvalidSpatialReference_ReturnsBadRequest()
    {
        _rasterStore.GetPrimaryRasterInfoAsync(1, Arg.Any<CancellationToken>())
            .Returns(CreateTestRasterInfo());

        var context = CreateImageServerContext();
        var request = CreateRequest("10,20", sr: "invalid-srid");
        var result = await _handler.IdentifyAsync(context, 1, request);
        await AssertGeoServicesErrorAsync(context, result, StatusCodes.Status400BadRequest);
    }

    [UnitTest]
    [Operation(Operations.Identify)]
    public async Task IdentifyAsync_UnsupportedGeometryType_ReturnsBadRequest()
    {
        _rasterStore.GetPrimaryRasterInfoAsync(1, Arg.Any<CancellationToken>())
            .Returns(CreateTestRasterInfo());

        var context = CreateImageServerContext();
        var request = CreateRequest("10,20", geometryType: "esriGeometryPolyline");
        var result = await _handler.IdentifyAsync(context, 1, request);
        await AssertGeoServicesErrorAsync(context, result, StatusCodes.Status400BadRequest);
    }

    [UnitTest]
    [Operation(Operations.Identify)]
    public async Task IdentifyAsync_EnvelopeGeometry_IdentifiesAtCentroid()
    {
        // Envelope geometry identifies at the bounding-box centroid; the store is
        // queried/identified at that representative location.
        _rasterStore.QueryRastersAsync(default, default, default)
            .ReturnsForAnyArgs([CreateTestRasterInfo()]);
        _rasterStore.IdentifyAsync(1, 100, Arg.Any<double>(), Arg.Any<double>(), Arg.Any<int?>(), Arg.Any<RasterIdentifyRendering?>(), Arg.Any<CancellationToken>())
            .Returns(new PixelValueResult
            {
                X = 5,
                Y = 5,
                Srid = 4326,
                HasData = true,
                BandValues = new Dictionary<int, object?> { [1] = 42.0 },
            });

        var context = CreateImageServerContext();
        var request = CreateRequest(
            "{\"xmin\":0,\"ymin\":0,\"xmax\":10,\"ymax\":10}",
            geometryType: "esriGeometryEnvelope");
        var result = await _handler.IdentifyAsync(context, 1, request);

        result.Should().BeOfType<JsonHttpResult<IdentifyResponse>>();
        await _rasterStore.Received().IdentifyAsync(
            1, 100, 5.0, 5.0, Arg.Any<int?>(), Arg.Any<RasterIdentifyRendering?>(), Arg.Any<CancellationToken>());
    }

    [UnitTest]
    [Operation(Operations.Identify)]
    public async Task IdentifyAsync_PolygonGeometry_IdentifiesAtCentroid()
    {
        _rasterStore.QueryRastersAsync(default, default, default)
            .ReturnsForAnyArgs([CreateTestRasterInfo()]);
        _rasterStore.IdentifyAsync(1, 100, Arg.Any<double>(), Arg.Any<double>(), Arg.Any<int?>(), Arg.Any<RasterIdentifyRendering?>(), Arg.Any<CancellationToken>())
            .Returns(new PixelValueResult
            {
                X = 1,
                Y = 1,
                Srid = 4326,
                HasData = true,
                BandValues = new Dictionary<int, object?> { [1] = 7.0 },
            });

        var context = CreateImageServerContext();
        var request = CreateRequest(
            "{\"rings\":[[[0,0],[0,2],[2,2],[2,0],[0,0]]]}",
            geometryType: "esriGeometryPolygon");
        var result = await _handler.IdentifyAsync(context, 1, request);

        result.Should().BeOfType<JsonHttpResult<IdentifyResponse>>();
        await _rasterStore.Received().IdentifyAsync(
            1, 100, 1.0, 1.0, Arg.Any<int?>(), Arg.Any<RasterIdentifyRendering?>(), Arg.Any<CancellationToken>());
    }

    [UnitTest]
    [Operation(Operations.Identify)]
    public async Task IdentifyAsync_ReturnCatalogItemsWithReturnGeometry_IncludesFootprint()
    {
        SetupSuccessfulIdentify();

        var context = CreateImageServerContext();
        var request = new IdentifyRequest
        {
            Geometry = "10,20",
            GeometryType = "esriGeometryPoint",
            ReturnCatalogItems = true,
            ReturnGeometry = true,
            F = "json",
        };
        var result = await _handler.IdentifyAsync(context, 1, request);

        var jsonResult = result.Should().BeOfType<JsonHttpResult<IdentifyResponse>>().Which;
        jsonResult.Value!.CatalogItems.Should().HaveCount(1);
        jsonResult.Value.CatalogItems![0].Footprint.Should().NotBeNull();
    }

    [UnitTest]
    [Operation(Operations.Identify)]
    public async Task IdentifyAsync_ReturnGeometryFalse_OmitsCatalogItemFootprint()
    {
        SetupSuccessfulIdentify();

        var context = CreateImageServerContext();
        var request = new IdentifyRequest
        {
            Geometry = "10,20",
            GeometryType = "esriGeometryPoint",
            ReturnCatalogItems = true,
            ReturnGeometry = false,
            F = "json",
        };
        var result = await _handler.IdentifyAsync(context, 1, request);

        var jsonResult = result.Should().BeOfType<JsonHttpResult<IdentifyResponse>>().Which;
        jsonResult.Value!.CatalogItems.Should().HaveCount(1);
        jsonResult.Value.CatalogItems![0].Footprint.Should().BeNull();
    }

    [UnitTest]
    [Operation(Operations.Identify)]
    public async Task IdentifyAsync_PixelSize_EchoedInProperties()
    {
        SetupSuccessfulIdentify();

        var context = CreateImageServerContext();
        var request = new IdentifyRequest
        {
            Geometry = "10,20",
            GeometryType = "esriGeometryPoint",
            PixelSize = 30,
            F = "json",
        };
        var result = await _handler.IdentifyAsync(context, 1, request);

        var jsonResult = result.Should().BeOfType<JsonHttpResult<IdentifyResponse>>().Which;
        jsonResult.Value!.Properties.Should().ContainKey("PixelSize");
        jsonResult.Value.Properties!["PixelSize"].Should().Be(30);
    }

    [UnitTest]
    [Operation(Operations.Identify)]
    public async Task IdentifyAsync_CommaCoordinates_ReturnsOk()
    {
        SetupSuccessfulIdentify();

        var context = CreateImageServerContext();
        var request = CreateRequest("10.5,20.3");
        var result = await _handler.IdentifyAsync(context, 1, request);

        result.Should().BeOfType<JsonHttpResult<IdentifyResponse>>();
    }

    [UnitTest]
    [Operation(Operations.Identify)]
    public async Task IdentifyAsync_JsonGeometry_ReturnsOk()
    {
        SetupSuccessfulIdentify();

        var context = CreateImageServerContext();
        var request = CreateRequest("{\"x\":10.5,\"y\":20.3}");
        var result = await _handler.IdentifyAsync(context, 1, request);

        result.Should().BeOfType<JsonHttpResult<IdentifyResponse>>();
    }

    [UnitTest]
    [Operation(Operations.Identify)]
    public async Task IdentifyAsync_JsonGeometryTooLarge_ReturnsBadRequest()
    {
        _rasterStore.GetPrimaryRasterInfoAsync(1, Arg.Any<CancellationToken>())
            .Returns(CreateTestRasterInfo());

        // JSON geometry exceeding 1000 char limit
        var padding = new string(' ', 1001);
        var context = CreateImageServerContext();
        var request = CreateRequest($"{{\"x\":10,\"y\":20,\"padding\":\"{padding}\"}}");
        var result = await _handler.IdentifyAsync(context, 1, request);
        await AssertGeoServicesErrorAsync(context, result, StatusCodes.Status400BadRequest);
    }

    [UnitTest]
    [Operation(Operations.Identify)]
    public async Task IdentifyAsync_InvalidJson_ReturnsBadRequest()
    {
        _rasterStore.GetPrimaryRasterInfoAsync(1, Arg.Any<CancellationToken>())
            .Returns(CreateTestRasterInfo());

        var context = CreateImageServerContext();
        var request = CreateRequest("{invalid-json}");
        var result = await _handler.IdentifyAsync(context, 1, request);
        await AssertGeoServicesErrorAsync(context, result, StatusCodes.Status400BadRequest);
    }

    [UnitTest]
    [Operation(Operations.Identify)]
    public async Task IdentifyAsync_ValidRequest_ResponseContainsBandValues()
    {
        SetupSuccessfulIdentify();

        var context = CreateImageServerContext();
        var request = CreateRequest("10,20");
        var result = await _handler.IdentifyAsync(context, 1, request);

        var jsonResult = result.Should().BeOfType<JsonHttpResult<IdentifyResponse>>().Which;
        jsonResult.Value!.Properties.Should().ContainKey("BandCount");
        jsonResult.Value.Properties.Should().ContainKey("HasData");
    }

    [UnitTest]
    [Operation(Operations.Identify)]
    public async Task IdentifyAsync_ValidRequest_ExecuteAsyncSerializesProperties()
    {
        SetupSuccessfulIdentify();

        var context = CreateImageServerContext();
        var request = CreateRequest("10,20");
        var result = await _handler.IdentifyAsync(context, 1, request);

        await result.ExecuteAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        context.Response.Body.Position = 0;

        using var responseJson = await JsonDocument.ParseAsync(context.Response.Body);
        var properties = responseJson.RootElement.GetProperty("properties");

        properties.GetProperty("HasData").GetBoolean().Should().BeTrue();
        properties.GetProperty("BandCount").GetInt32().Should().Be(3);
        properties.GetProperty("Coordinates").GetString().Should().Be("10.5, 20.3");
        properties.GetProperty("Band_1").GetDouble().Should().Be(128.0);
    }

    [UnitTest]
    [Operation(Operations.Identify)]
    public async Task IdentifyAsync_WithRenderingRule_PassesRenderingToStore()
    {
        // A renderingRule changes the identify contract: the store is invoked with a
        // RasterIdentifyRendering so the returned value reflects the rendered pixel.
        _rasterStore.QueryRastersAsync(default, default, default)
            .ReturnsForAnyArgs([CreateTestRasterInfo()]);
        _rasterStore.IdentifyAsync(
                1, 100, Arg.Any<double>(), Arg.Any<double>(), Arg.Any<int?>(),
                Arg.Any<RasterIdentifyRendering?>(), Arg.Any<CancellationToken>())
            .Returns(new PixelValueResult
            {
                X = 10,
                Y = 20,
                Srid = 4326,
                HasData = true,
                BandValues = new Dictionary<int, object?> { [1] = 200.0 },
            });

        var context = CreateImageServerContext();
        var request = new IdentifyRequest
        {
            Geometry = "10,20",
            GeometryType = "esriGeometryPoint",
            RenderingRule = """{"rasterFunction":"Stretch","rasterFunctionArguments":{"StretchType":5}}""",
            F = "json",
        };
        var result = await _handler.IdentifyAsync(context, 1, request);

        result.Should().BeOfType<JsonHttpResult<IdentifyResponse>>();
        await _rasterStore.Received().IdentifyAsync(
            1, 100, Arg.Any<double>(), Arg.Any<double>(), Arg.Any<int?>(),
            Arg.Is<RasterIdentifyRendering?>(r => r != null && r.Value.Stretch != null),
            Arg.Any<CancellationToken>());
    }

    [UnitTest]
    [Operation(Operations.Identify)]
    public async Task IdentifyAsync_WithNotImplementedRenderingRule_Returns501()
    {
        _rasterStore.QueryRastersAsync(default, default, default)
            .ReturnsForAnyArgs([CreateTestRasterInfo()]);

        var context = CreateImageServerContext();
        var request = new IdentifyRequest
        {
            Geometry = "10,20",
            GeometryType = "esriGeometryPoint",
            // Histogram-equalize stretch (type 4) is recognized but unimplemented.
            RenderingRule = """{"rasterFunction":"Stretch","rasterFunctionArguments":{"StretchType":4}}""",
            F = "json",
        };
        var result = await _handler.IdentifyAsync(context, 1, request);
        // #2795: not-implemented operations surface body error.code 501 (pass-through), not the 500 collapse.
        await AssertGeoServicesErrorAsync(context, result, StatusCodes.Status501NotImplemented);
    }

    [UnitTest]
    [Operation(Operations.Identify)]
    public async Task IdentifyAsync_WithIdentityRenderingRule_PreservesRawValueContract()
    {
        // An Identity-only chain is an executable no-op: the store is called with a null
        // rendering so the raw source value is returned (no contract change).
        SetupSuccessfulIdentify();

        var context = CreateImageServerContext();
        var request = new IdentifyRequest
        {
            Geometry = "10.5,20.3",
            GeometryType = "esriGeometryPoint",
            RenderingRule = """{"rasterFunction":"Identity"}""",
            F = "json",
        };
        var result = await _handler.IdentifyAsync(context, 1, request);

        result.Should().BeOfType<JsonHttpResult<IdentifyResponse>>();
        await _rasterStore.Received().IdentifyAsync(
            1, 100, Arg.Any<double>(), Arg.Any<double>(), Arg.Any<int?>(),
            Arg.Is<RasterIdentifyRendering?>(r => r == null),
            Arg.Any<CancellationToken>());
    }

    [UnitTest]
    [Operation(Operations.Identify)]
    public async Task IdentifyAsync_WithMultidimensionalDefinition_ReturnsCanonicalSliceValue()
    {
        _zarrPointSliceReader.ReadAsync(
                1, 10, 20, 4326, Arg.Any<IReadOnlyList<ZarrPointSliceSelection>>(), Arg.Any<CancellationToken>())
            .Returns(new ZarrPointSliceReadResult(
                ZarrPointSliceReadStatus.Success, 1022, "temperature", null));
        var request = new IdentifyRequest
        {
            Geometry = "10,20",
            GeometryType = "esriGeometryPoint",
            Sr = "4326",
            MultidimensionalDefinition =
                "[{\"variableName\":\"temperature\",\"dimensionName\":\"elevation\",\"values\":[333.3333]}]",
        };
        var context = CreateImageServerContext();

        var result = await _handler.IdentifyAsync(context, 1, request);
        await result.ExecuteAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        context.Response.Body.Position = 0;
        using var json = await JsonDocument.ParseAsync(context.Response.Body);
        json.RootElement.GetProperty("value").GetString().Should().Be("1022");
        await _rasterStore.DidNotReceiveWithAnyArgs().IdentifyAsync(default, default, default, default);
    }

    [UnitTest]
    [Operation(Operations.Identify)]
    public async Task IdentifyAsync_WithUnavailableMultidimensionalReader_ReturnsNotImplemented()
    {
        _zarrPointSliceReader.ReadAsync(
                1, 10, 20, null, Arg.Any<IReadOnlyList<ZarrPointSliceSelection>>(), Arg.Any<CancellationToken>())
            .Returns(new ZarrPointSliceReadResult(
                ZarrPointSliceReadStatus.ReaderUnavailable,
                null,
                "temperature",
                "The storage reader for this multidimensional coverage is not configured."));
        var request = new IdentifyRequest
        {
            Geometry = "10,20",
            MultidimensionalDefinition = "[{\"dimensionName\":\"elevation\",\"values\":[10]}]",
        };
        var context = CreateImageServerContext();

        var result = await _handler.IdentifyAsync(context, 1, request);
        // #2795: an unavailable multidimensional reader is NotImplemented (501); GeoServices passes it
        // through as body error.code 501 (pass-through) rather than collapsing to 500. Consolidated onto
        // the shared GeoServicesErrorAssertions helper (asserts transport 200 + body code).
        await AssertGeoServicesErrorAsync(context, result, StatusCodes.Status501NotImplemented);
    }

    [UnitTest]
    [Operation(Operations.Identify)]
    public async Task IdentifyAsync_WithOutOfRangeSliceCoordinate_ReturnsBadRequest()
    {
        _zarrPointSliceReader.ReadAsync(
                1, 10, 20, null, Arg.Any<IReadOnlyList<ZarrPointSliceSelection>>(), Arg.Any<CancellationToken>())
            .Returns(new ZarrPointSliceReadResult(
                ZarrPointSliceReadStatus.InvalidSelection,
                null,
                "temperature",
                "The requested coordinate is outside the coverage axis 'elevation'."));
        var request = new IdentifyRequest
        {
            Geometry = "10,20",
            MultidimensionalDefinition = "[{\"dimensionName\":\"elevation\",\"values\":[9999]}]",
        };
        var context = CreateImageServerContext();

        var result = await _handler.IdentifyAsync(context, 1, request);
        await result.ExecuteAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        context.Response.Body.Position = 0;
        using var json = await JsonDocument.ParseAsync(context.Response.Body);
        json.RootElement.GetProperty("error").GetProperty("code").GetInt32()
            .Should().Be(StatusCodes.Status400BadRequest);
    }

    private static DefaultHttpContext CreateImageServerContext()
    {
        var services = new ServiceCollection();
        services.AddSingleton<Microsoft.Extensions.Logging.ILoggerFactory>(NullLoggerFactory.Instance);

        var context = new DefaultHttpContext();
        context.RequestServices = services.BuildServiceProvider();
        context.Request.Path = "/rest/services/1/ImageServer/identify";
        context.Response.Body = new MemoryStream();
        return context;
    }

    private void SetupSuccessfulIdentify()
    {
        _rasterStore.QueryRastersAsync(default, default, default)
            .ReturnsForAnyArgs([CreateTestRasterInfo()]);
        _rasterStore.IdentifyAsync(1, 100, Arg.Any<double>(), Arg.Any<double>(), Arg.Any<int?>(), Arg.Any<RasterIdentifyRendering?>(), Arg.Any<CancellationToken>())
            .Returns(new PixelValueResult
            {
                X = 10.5,
                Y = 20.3,
                Srid = 4326,
                HasData = true,
                BandValues = new Dictionary<int, object?> { [1] = 128.0, [2] = 64.0, [3] = 32.0 }
            });
    }

    private static IdentifyRequest CreateRequest(string geometry, string? sr = null, string? geometryType = "esriGeometryPoint") => new()
    {
        Geometry = geometry,
        GeometryType = geometryType,
        Sr = sr,
        F = "json"
    };

    private static TestMetadataV2GraphProvider BuildGraphWithLayer(int layerIndex)
        => new TestMetadataV2GraphBuilder()
            .AddResource($"resource-{layerIndex}", "test-layer", MetadataV2ResourceType.RasterDataset)
            .AddService($"service-{layerIndex}", $"image-svc-{layerIndex}", protocols: [ServiceProtocols.ImageServer])
            .AddPublication(
                $"publication-{layerIndex}",
                $"service-{layerIndex}",
                $"resource-{layerIndex}",
                layerIndex: layerIndex,
                serviceLocalId: "test-layer",
                publicationType: MetadataV2PublicationType.EsriImageLayer)
            .BuildProvider();

    private static RasterInfo CreateTestRasterInfo() => new()
    {
        Id = 100,
        LayerId = 1,
        Name = "test-raster",
        Width = 1024,
        Height = 1024,
        BandCount = 3,
        PixelType = "uint8",
        Srid = 4326,
        GeoTransform = [0, 1, 0, 0, 0, -1],
        Extent = new RasterExtent { XMin = -180, YMin = -90, XMax = 180, YMax = 90, Srid = 4326 },
        CreatedAt = DateTime.UtcNow
    };
}
