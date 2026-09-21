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
        // Nothing was sampled and the request named no sr, so no reference is fabricated (#4064).
        responseJson.RootElement.GetProperty("location").TryGetProperty("spatialReference", out _).Should().BeFalse();
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

    // #4064: Esri identify reports a multi-band pixel as "v1, v2, v3" (invariant culture) and labels
    // the location with its spatial reference.
    [UnitTest]
    [Operation(Operations.Identify)]
    public async Task IdentifyAsync_MultiBandPixel_ReturnsEsriCommaSeparatedValueAndLocationSpatialReference()
    {
        _rasterStore.QueryRastersAsync(default, default, default)
            .ReturnsForAnyArgs([CreateTestRasterInfo()]);
        _rasterStore.IdentifyAsync(1, 100, 10, 20, 4326, Arg.Any<RasterIdentifyRendering?>(), Arg.Any<CancellationToken>())
            .Returns(new PixelValueResult
            {
                X = 10,
                Y = 20,
                Srid = 4326,
                HasData = true,
                BandValues = new Dictionary<int, object?> { [3] = 32.0, [1] = 128.0, [2] = 64.25 }
            });

        using var json = await ExecuteIdentifyJsonAsync(CreateRequest("10,20", sr: "4326"));

        json.RootElement.GetProperty("value").GetString().Should().Be("128, 64.25, 32");
        var location = json.RootElement.GetProperty("location");
        location.GetProperty("x").GetDouble().Should().Be(10);
        location.GetProperty("y").GetDouble().Should().Be(20);
        location.GetProperty("spatialReference").GetProperty("wkid").GetInt32().Should().Be(4326);
    }

    [UnitTest]
    [Operation(Operations.Identify)]
    public async Task IdentifyAsync_ProjectedPoint_LabelsLocationWithRequestSpatialReference()
    {
        _rasterStore.QueryRastersAsync(default, default, default)
            .ReturnsForAnyArgs([CreateTestRasterInfo()]);
        _rasterStore.IdentifyAsync(1, 100, 1113194.9, 1118889.97, 3857, Arg.Any<RasterIdentifyRendering?>(), Arg.Any<CancellationToken>())
            .Returns(new PixelValueResult
            {
                X = 1113194.9,
                Y = 1118889.97,
                Srid = 3857,
                HasData = true,
                BandValues = new Dictionary<int, object?> { [1] = 7.0 }
            });

        using var json = await ExecuteIdentifyJsonAsync(
            CreateRequest("{\"x\":1113194.9,\"y\":1118889.97,\"spatialReference\":{\"wkid\":3857}}"));

        json.RootElement.GetProperty("value").GetString().Should().Be("7");
        json.RootElement.GetProperty("location").GetProperty("spatialReference").GetProperty("wkid").GetInt32()
            .Should().Be(3857);
    }

    [UnitTest]
    [Operation(Operations.Identify)]
    public async Task IdentifyAsync_AllBandsNoData_ReturnsNoDataValue()
    {
        _rasterStore.QueryRastersAsync(default, default, default)
            .ReturnsForAnyArgs([CreateTestRasterInfo()]);
        _rasterStore.IdentifyAsync(1, 100, Arg.Any<double>(), Arg.Any<double>(), Arg.Any<int?>(), Arg.Any<RasterIdentifyRendering?>(), Arg.Any<CancellationToken>())
            .Returns(new PixelValueResult
            {
                X = 10,
                Y = 20,
                Srid = null,
                HasData = false,
                BandValues = new Dictionary<int, object?> { [1] = null, [2] = null, [3] = null }
            });

        using var json = await ExecuteIdentifyJsonAsync(CreateRequest("10,20"));

        json.RootElement.GetProperty("value").GetString().Should().Be("NoData");
        // An sr-less identify is sampled as WGS84 by the raster store.
        json.RootElement.GetProperty("location").GetProperty("spatialReference").GetProperty("wkid").GetInt32()
            .Should().Be(4326);
    }

    // #4064: esriMosaicLockRaster pins the identified catalog item, as exportImage does, instead of
    // returning the merged mosaic value.
    [UnitTest]
    [Operation(Operations.Identify)]
    public async Task IdentifyAsync_LockRasterMosaicRule_IdentifiesOnlyTheLockedRaster()
    {
        _rasterStore.QueryRastersAsync(default, default, default)
            .ReturnsForAnyArgs([CreateTestRasterInfo(), CreateTestRasterInfo() with { Id = 101, Name = "locked-raster" }]);
        _rasterStore.IdentifyAsync(1, 101, Arg.Any<double>(), Arg.Any<double>(), Arg.Any<int?>(), Arg.Any<RasterIdentifyRendering?>(), Arg.Any<CancellationToken>())
            .Returns(new PixelValueResult
            {
                X = 10,
                Y = 20,
                Srid = 4326,
                HasData = true,
                BandValues = new Dictionary<int, object?> { [1] = 2.0 }
            });
        var request = new IdentifyRequest
        {
            Geometry = "10,20",
            GeometryType = "esriGeometryPoint",
            Sr = "4326",
            MosaicRule = "{\"mosaicMethod\":\"esriMosaicLockRaster\",\"lockRasterIds\":[101]}",
            ReturnCatalogItems = true,
            F = "json"
        };

        using var json = await ExecuteIdentifyJsonAsync(request);

        json.RootElement.GetProperty("objectId").GetInt64().Should().Be(101);
        json.RootElement.GetProperty("name").GetString().Should().Be("locked-raster");
        json.RootElement.GetProperty("value").GetString().Should().Be("2");
        json.RootElement.GetProperty("catalogItems").EnumerateArray()
            .Select(item => item.GetProperty("id").GetInt64())
            .Should().Equal(101);
        await _rasterStore.DidNotReceiveWithAnyArgs().IdentifyMosaicAsync(
            default, default!, default, default, default, default, default, default, default, default);
        await _rasterStore.DidNotReceive().IdentifyAsync(
            1, 100, Arg.Any<double>(), Arg.Any<double>(), Arg.Any<int?>(), Arg.Any<RasterIdentifyRendering?>(), Arg.Any<CancellationToken>());
    }

    // #4064: identify resolves a contested pixel with the same ordering exportImage renders, so the
    // parsed method (and non-date attribute sort) reaches the mosaic identify call.
    [UnitTest]
    [Operation(Operations.Identify)]
    public async Task IdentifyAsync_OrderingMosaicRule_PassesParsedOrderingToMosaicIdentify()
    {
        _rasterStore.QueryRastersAsync(default, default, default)
            .ReturnsForAnyArgs([CreateTestRasterInfo(), CreateTestRasterInfo() with { Id = 101 }]);
        _rasterStore.IdentifyMosaicAsync(default, default!, default, default, default, default, default, default, default, default)
            .ReturnsForAnyArgs(new PixelValueResult
            {
                X = 10,
                Y = 20,
                Srid = 4326,
                HasData = true,
                BandValues = new Dictionary<int, object?> { [1] = 9.0 }
            });

        foreach (var (mosaicRule, ordering, attributeSort) in new (string, RasterMosaicOrdering, RasterMosaicAttributeSort?)[]
                 {
                     ("{\"mosaicMethod\":\"esriMosaicNorthwest\"}", RasterMosaicOrdering.Northwest, null),
                     ("{\"mosaicMethod\":\"esriMosaicNadir\"}", RasterMosaicOrdering.Nadir, null),
                     ("{\"mosaicMethod\":\"esriMosaicSeamline\"}", RasterMosaicOrdering.Seamline, null),
                     ("{\"mosaicMethod\":\"esriMosaicByAttribute\",\"sortField\":\"AcquisitionDate\",\"ascending\":true}", RasterMosaicOrdering.AcquisitionOldest, null),
                     ("{\"mosaicMethod\":\"esriMosaicByAttribute\",\"sortField\":\"OBJECTID\",\"ascending\":true}", RasterMosaicOrdering.Attribute, new RasterMosaicAttributeSort("id", true))
                 })
        {
            _rasterStore.ClearReceivedCalls();
            var request = new IdentifyRequest { Geometry = "10,20", Sr = "4326", MosaicRule = mosaicRule, F = "json" };

            using var json = await ExecuteIdentifyJsonAsync(request);

            json.RootElement.GetProperty("value").GetString().Should().Be("9");
            await _rasterStore.Received(1).IdentifyMosaicAsync(
                1,
                Arg.Is<long[]>(ids => ids.SequenceEqual(new long[] { 100, 101 })),
                RasterMergeStrategy.Newest,
                10,
                20,
                4326,
                null,
                ordering,
                attributeSort,
                Arg.Any<CancellationToken>());
        }
    }

    [UnitTest]
    [Operation(Operations.Identify)]
    public async Task IdentifyAsync_UnsupportedMosaicMethodOverSeveralRasters_ReturnsNotImplemented()
    {
        _rasterStore.QueryRastersAsync(default, default, default)
            .ReturnsForAnyArgs([CreateTestRasterInfo(), CreateTestRasterInfo() with { Id = 101 }]);

        foreach (var mosaicRule in new[]
                 {
                     "{\"mosaicMethod\":\"esriMosaicCenter\"}",
                     "{\"mosaicMethod\":\"esriMosaicByAttribute\",\"sortField\":\"AcquisitionDate\",\"sortValue\":\"2024/01/01\"}"
                 })
        {
            var context = CreateImageServerContext();
            var request = new IdentifyRequest { Geometry = "10,20", MosaicRule = mosaicRule, F = "json" };

            var result = await _handler.IdentifyAsync(context, 1, request);

            await AssertGeoServicesErrorAsync(context, result, StatusCodes.Status501NotImplemented);
        }

        await _rasterStore.DidNotReceiveWithAnyArgs().IdentifyMosaicAsync(
            default, default!, default, default, default, default, default, default, default, default);
    }

    [UnitTest]
    [Operation(Operations.Identify)]
    public async Task IdentifyAsync_MalformedMosaicRule_ReturnsBadRequest()
    {
        SetupSuccessfulIdentify();

        foreach (var mosaicRule in new[]
                 {
                     "{\"mosaicMethod\":",
                     "{\"mosaicMethod\":\"esriMosaicLockRaster\"}",
                     "{\"mosaicMethod\":\"esriMosaicLockRaster\",\"lockRasterIds\":[\"x\"]}"
                 })
        {
            var context = CreateImageServerContext();
            var request = new IdentifyRequest { Geometry = "10,20", MosaicRule = mosaicRule, F = "json" };

            var result = await _handler.IdentifyAsync(context, 1, request);

            await AssertGeoServicesErrorAsync(context, result, StatusCodes.Status400BadRequest);
        }
    }

    [Theory]
    [InlineData("{x: -122.498828, y: 37.838906}", null)]
    [InlineData("{x: -122.498828, y: 37.838906}", "4326")]
    [InlineData("{y:37.838906,x:-122.498828}", "4326")]
    [InlineData("{ x : -1.22498828e2 , y : 3.7838906e1 }", "4326")]
    [InlineData("{\"x\":-122.498828,\"y\":37.838906}", "4326")]
    [InlineData("-122.498828,37.838906", "4326")]
    [Operation(Operations.Identify)]
    public async Task IdentifyAsync_NativePointLiteralMatchesStrictJson(string geometry, string? sr)
    {
        _rasterStore.QueryRastersAsync(default, default, default)
            .ReturnsForAnyArgs([CreateTestRasterInfo()]);
        _rasterStore.IdentifyAsync(1, 100, Arg.Any<double>(), Arg.Any<double>(), Arg.Any<int?>(), Arg.Any<RasterIdentifyRendering?>(), Arg.Any<CancellationToken>())
            .Returns(new PixelValueResult
            {
                X = -122.498828,
                Y = 37.838906,
                Srid = 4326,
                HasData = true,
                BandValues = new Dictionary<int, object?> { [1] = 10.0 }
            });

        using var expected = await ExecuteIdentifyJsonAsync(
            CreateRequest("{\"x\":-122.498828,\"y\":37.838906}", sr));
        using var actual = await ExecuteIdentifyJsonAsync(CreateRequest(geometry, sr));
        actual.RootElement.GetRawText().Should().Be(expected.RootElement.GetRawText());
        actual.RootElement.GetProperty("value").GetString().Should().Be("10");
        await _rasterStore.Received(2).IdentifyAsync(1, 100, -122.498828, 37.838906,
            sr is null ? null : 4326, Arg.Any<RasterIdentifyRendering?>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("{x:NaN,y:37.838906}")]
    [InlineData("{x:Infinity,y:37.838906}")]
    [InlineData("{x:1e999,y:37.838906}")]
    [InlineData("{x:-122.498828,x:0,y:37.838906}")]
    [InlineData("{x:-122.498828}")]
    [InlineData("{x:-122.498828,y:37.838906,unknown:1}")]
    [InlineData("{x:-122.498828,y:37.838906};anything()")]
    [InlineData("{x:(-122.498828),y:37.838906}")]
    [Operation(Operations.Identify)]
    public async Task IdentifyAsync_NativePointLiteralRejectsInvalidInput(string geometry)
    {
        var context = CreateImageServerContext();
        var result = await _handler.IdentifyAsync(context, 1, CreateRequest(geometry));
        await AssertGeoServicesErrorAsync(context, result, StatusCodes.Status400BadRequest);
        await _rasterStore.DidNotReceiveWithAnyArgs().QueryRastersAsync(default, default!, default);
        await _rasterStore.DidNotReceiveWithAnyArgs().IdentifyAsync(default, default, default, default, default, default, default);
    }

    [Theory]
    [InlineData("esriGeometryEnvelope")]
    [InlineData("esriGeometryPolygon")]
    [Operation(Operations.Identify)]
    public async Task IdentifyAsync_PointLiteralDoesNotRelaxNonPointGeometry(string geometryType)
    {
        var context = CreateImageServerContext();
        var result = await _handler.IdentifyAsync(context, 1,
            CreateRequest("{x:-122.498828,y:37.838906}", geometryType: geometryType));
        await AssertGeoServicesErrorAsync(context, result, StatusCodes.Status400BadRequest);
        await _rasterStore.DidNotReceiveWithAnyArgs().QueryRastersAsync(default, default!, default);
    }


    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Operation(Operations.Identify)]
    public async Task IdentifyAsync_AdditiveResultsPreserveStandardPixelAndCatalog(bool returnCatalogItems)
    {
        SetupSuccessfulIdentify();
        using var json = await ExecuteIdentifyJsonAsync(new IdentifyRequest
        {
            Geometry = "10,20",
            Sr = "4326",
            ReturnCatalogItems = returnCatalogItems,
            ReturnGeometry = false,
            F = "json"
        });
        var root = json.RootElement;
        root.GetProperty("value").GetString().Should().Be("128, 64, 32");
        root.GetProperty("objectId").GetInt64().Should().Be(100);
        root.GetProperty("properties").GetProperty("Band_1").GetDouble().Should().Be(128);
        root.TryGetProperty("catalogItems", out var catalog).Should().Be(returnCatalogItems);
        if (returnCatalogItems)
        {
            catalog.GetArrayLength().Should().Be(1);
            catalog[0].GetProperty("id").GetInt64().Should().Be(100);
            catalog[0].TryGetProperty("footprint", out _).Should().BeFalse();
        }

        var results = root.GetProperty("results");
        results.GetArrayLength().Should().Be(1);
        var feature = results[0];
        feature.GetProperty("geometryType").GetString().Should().Be("esriGeometryPoint");
        feature.GetProperty("geometry").GetRawText().Should().Be(root.GetProperty("location").GetRawText());
        feature.GetProperty("layerName").GetString().Should().Be(root.GetProperty("name").GetString());
        feature.GetProperty("displayFieldName").GetString().Should().Be("Pixel Value");
        feature.GetProperty("attributes").GetProperty("Pixel Value").GetString().Should().Be("128, 64, 32");
        feature.GetProperty("attributes").GetProperty("Band_1").GetDouble().Should().Be(128);
    }

    [UnitTest]
    [Operation(Operations.Identify)]
    public async Task IdentifyAsync_AdditiveNoDataIsExplicitWithoutInventedRasterIdentity()
    {
        _rasterStore.QueryRastersAsync(default, default, default).ReturnsForAnyArgs(Array.Empty<RasterInfo>());
        using var json = await ExecuteIdentifyJsonAsync(CreateRequest("10,20"));
        var root = json.RootElement;
        root.GetProperty("value").GetString().Should().Be("NoData");
        root.TryGetProperty("objectId", out _).Should().BeFalse();
        var feature = root.GetProperty("results")[0];
        feature.GetProperty("attributes").GetProperty("Pixel Value").GetString().Should().Be("NoData");
        feature.GetProperty("attributes").GetProperty("HasData").GetBoolean().Should().BeFalse();
        feature.GetProperty("geometry").GetRawText().Should().Be(root.GetProperty("location").GetRawText());
        feature.GetProperty("geometry").TryGetProperty("spatialReference", out _).Should().BeFalse();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    [Operation(Operations.Identify)]
    public async Task IdentifyAsync_AdditiveMultidimensionalResultPreservesCanonicalSlice(bool hasData)
    {
        _zarrPointSliceReader.ReadAsync(
                1, 10, 20, 4326, Arg.Any<IReadOnlyList<ZarrPointSliceSelection>>(), Arg.Any<CancellationToken>())
            .Returns(new ZarrPointSliceReadResult(
                hasData ? ZarrPointSliceReadStatus.Success : ZarrPointSliceReadStatus.OutsideCoverage,
                hasData ? 1022 : null, "temperature", null));
        using var json = await ExecuteIdentifyJsonAsync(new IdentifyRequest
        {
            Geometry = "10,20",
            Sr = "4326",
            MultidimensionalDefinition = "[{\"variableName\":\"temperature\",\"dimensionName\":\"elevation\",\"values\":[333.3333]}]"
        });
        var root = json.RootElement;
        var expectedValue = hasData ? "1022" : "NoData";
        root.GetProperty("value").GetString().Should().Be(expectedValue);
        root.GetProperty("properties").GetProperty("HasData").GetBoolean().Should().Be(hasData);
        root.GetProperty("properties").TryGetProperty("Pixel Value", out _).Should().BeFalse();
        var feature = root.GetProperty("results")[0];
        feature.GetProperty("attributes").GetProperty("Pixel Value").GetString().Should().Be(expectedValue);
        feature.GetProperty("attributes").GetProperty("Variable").GetString().Should().Be("temperature");
        feature.GetProperty("geometry").GetRawText().Should().Be(root.GetProperty("location").GetRawText());
        await _rasterStore.DidNotReceiveWithAnyArgs().IdentifyAsync(default, default, default, default);
    }

    private async Task<JsonDocument> ExecuteIdentifyJsonAsync(IdentifyRequest request)
    {
        var context = CreateImageServerContext();
        var result = await _handler.IdentifyAsync(context, 1, request);
        await result.ExecuteAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        context.Response.Body.Position = 0;
        var json = await JsonDocument.ParseAsync(context.Response.Body);
        json.RootElement.TryGetProperty("error", out var error).Should().BeFalse(error.ToString());
        return json;
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
