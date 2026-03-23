// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Catalog.Abstractions;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.Domain;
using Honua.Server.Features.ImageServer.Handlers;
using Honua.Server.Features.ImageServer.Models;
using Honua.Server.Features.Infrastructure.Services;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Honua.Server.Tests.Features.ImageServer;

/// <summary>
/// Tests for ImageServerExportHandler functionality.
/// </summary>
[Protocol(Protocols.ImageServer)]
public class ImageServerExportHandlerTests
{
    private readonly ILayerCatalog _layerCatalog = Substitute.For<ILayerCatalog>();
    private readonly IRasterStore _rasterStore = Substitute.For<IRasterStore>();
    private readonly ITemporaryFileService _temporaryFileService = Substitute.For<ITemporaryFileService>();
    private readonly ImageServerExportHandler _handler;

    public ImageServerExportHandlerTests()
    {
        _handler = new ImageServerExportHandler(
            _layerCatalog,
            _rasterStore,
            _temporaryFileService,
            NullLogger<ImageServerExportHandler>.Instance);
    }

    [UnitTest]
    [Operation(Operations.Export)]
    public async Task ExportImageAsync_LayerNotFound_ReturnsNotFound()
    {
        _layerCatalog.GetLayerAsync(99, Arg.Any<CancellationToken>())
            .Returns((LayerDefinition?)null);

        var context = CreateImageServerContext();
        var request = CreateRequest();
        var result = await _handler.ExportImageAsync(context, 99, request);
        await result.ExecuteAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status404NotFound);
    }

    [UnitTest]
    [Operation(Operations.Export)]
    public async Task ExportImageAsync_NoRasters_ReturnsNotFound()
    {
        _layerCatalog.GetLayerAsync(1, Arg.Any<CancellationToken>())
            .Returns(CreateTestLayer());
        _rasterStore.ListRastersAsync(1, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<RasterInfo>());

        var context = CreateImageServerContext();
        var request = CreateRequest();
        var result = await _handler.ExportImageAsync(context, 1, request);
        await result.ExecuteAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status404NotFound);
    }

    [UnitTest]
    [Operation(Operations.Export)]
    public async Task ExportImageAsync_InvalidBbox_ReturnsBadRequest()
    {
        SetupLayerAndRasters();

        var context = CreateImageServerContext();
        var request = CreateRequest(bbox: "invalid-bbox");
        var result = await _handler.ExportImageAsync(context, 1, request);
        await result.ExecuteAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
    }

    [UnitTest]
    [Operation(Operations.Export)]
    public async Task ExportImageAsync_InvalidSize_ReturnsBadRequest()
    {
        SetupLayerAndRasters();

        var context = CreateImageServerContext();
        var request = CreateRequest(size: 0);
        var result = await _handler.ExportImageAsync(context, 1, request);
        await result.ExecuteAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        await _rasterStore.DidNotReceive()
            .ExportImageAsync(1, 100, Arg.Any<RasterQuery>(), Arg.Any<CancellationToken>());
    }

    [UnitTest]
    [Operation(Operations.Export)]
    public async Task ExportImageAsync_UnsupportedOutputFormat_ReturnsBadRequest()
    {
        SetupLayerAndRasters();

        var context = CreateImageServerContext();
        var request = CreateRequest(format: "bmp");
        var result = await _handler.ExportImageAsync(context, 1, request);
        await result.ExecuteAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        await _rasterStore.DidNotReceive()
            .ExportImageAsync(1, 100, Arg.Any<RasterQuery>(), Arg.Any<CancellationToken>());
    }

    [UnitTest]
    [Operation(Operations.Export)]
    public async Task ExportImageAsync_ValidRequest_ReturnsOk()
    {
        SetupSuccessfulExport();

        var context = CreateImageServerContext();
        var request = CreateRequest();
        var result = await _handler.ExportImageAsync(context, 1, request);

        result.Should().BeOfType<JsonHttpResult<ExportImageResponse>>();
    }

    [UnitTest]
    [Operation(Operations.Export)]
    public async Task ExportImageAsync_WithBbox_ReturnsOk()
    {
        SetupSuccessfulExport();

        var context = CreateImageServerContext();
        var request = CreateRequest(bbox: "-180,-90,180,90");
        var result = await _handler.ExportImageAsync(context, 1, request);

        result.Should().BeOfType<JsonHttpResult<ExportImageResponse>>();
    }

    [UnitTest]
    [Operation(Operations.Export)]
    public async Task ExportImageAsync_WithSize_UsesProvidedSize()
    {
        SetupSuccessfulExport();

        var context = CreateImageServerContext();
        var request = CreateRequest(size: 512);
        var result = await _handler.ExportImageAsync(context, 1, request);

        var jsonResult = result as JsonHttpResult<ExportImageResponse>;
        jsonResult.Should().NotBeNull();
    }

    [UnitTest]
    [Operation(Operations.Export)]
    public async Task ExportImageAsync_WithExtremeAspectRatio_ClampsOutputDimensions()
    {
        _layerCatalog.GetLayerAsync(1, Arg.Any<CancellationToken>())
            .Returns(CreateTestLayer());
        _rasterStore.ListRastersAsync(1, Arg.Any<CancellationToken>())
            .Returns([CreateTestRasterInfo() with { Width = 100, Height = 20000 }]);

        RasterQuery? capturedQuery = null;
        _rasterStore.ExportImageAsync(1, 100, Arg.Any<RasterQuery>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                capturedQuery = callInfo.ArgAt<RasterQuery>(2);
                return CreateTestRasterResult();
            });
        _temporaryFileService.StoreTemporaryFileAsync(
            Arg.Any<byte[]>(), Arg.Any<string>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>())
            .Returns("/temp/test.png");
        _rasterStore.GetExtentAsync(1, 100, Arg.Any<CancellationToken>())
            .Returns(new RasterExtent { XMin = -180, YMin = -90, XMax = 180, YMax = 90, Srid = 4326 });

        var context = CreateImageServerContext();
        var request = CreateRequest(size: 4096);
        var result = await _handler.ExportImageAsync(context, 1, request);

        result.Should().BeOfType<JsonHttpResult<ExportImageResponse>>();
        capturedQuery.Should().NotBeNull();
        capturedQuery!.Value.OutputWidth.Should().BeGreaterThan(0).And.BeLessOrEqualTo(4096);
        capturedQuery.Value.OutputHeight.Should().BeGreaterThan(0).And.BeLessOrEqualTo(4096);
    }

    [UnitTest]
    [Operation(Operations.Export)]
    public async Task ExportImageAsync_WithJpegFormat_ReturnsOk()
    {
        SetupSuccessfulExport();

        var context = CreateImageServerContext();
        var request = CreateRequest(format: "jpeg");
        var result = await _handler.ExportImageAsync(context, 1, request);

        result.Should().BeOfType<JsonHttpResult<ExportImageResponse>>();
    }

    [UnitTest]
    [Operation(Operations.Export)]
    public async Task ExportImageAsync_WithInterpolation_ReturnsOk()
    {
        SetupSuccessfulExport();

        var context = CreateImageServerContext();
        var request = CreateRequest(interpolation: "RSP_NearestNeighbor");
        var result = await _handler.ExportImageAsync(context, 1, request);

        result.Should().BeOfType<JsonHttpResult<ExportImageResponse>>();
    }

    [UnitTest]
    [Operation(Operations.Export)]
    public async Task ExportImageAsync_WithImageSr_ReturnsOk()
    {
        SetupSuccessfulExport();

        var context = CreateImageServerContext();
        var request = CreateRequest(imageSr: "3857");
        var result = await _handler.ExportImageAsync(context, 1, request);

        result.Should().BeOfType<JsonHttpResult<ExportImageResponse>>();
    }

    [UnitTest]
    [Operation(Operations.Export)]
    public async Task ExportImageAsync_WithBboxSr_PassesClipRegionSridToRasterStore()
    {
        SetupSuccessfulExport();
        RasterQuery? capturedQuery = null;
        _rasterStore.ExportImageAsync(1, 100, Arg.Any<RasterQuery>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                capturedQuery = callInfo.ArgAt<RasterQuery>(2);
                return CreateTestRasterResult();
            });

        var context = CreateImageServerContext();
        var request = CreateRequest(
            bbox: "-20037508,-20037508,20037508,20037508",
            bboxSr: "3857");
        var result = await _handler.ExportImageAsync(context, 1, request);

        result.Should().BeOfType<JsonHttpResult<ExportImageResponse>>();
        capturedQuery.Should().NotBeNull();
        var clipRegion = capturedQuery!.Value.ClipRegion;
        clipRegion.HasValue.Should().BeTrue();
        if (!clipRegion.HasValue)
        {
            throw new InvalidOperationException("Clip region should be set when bbox is provided.");
        }

        clipRegion.Value.Srid.Should().Be(3857);
    }

    [UnitTest]
    [Operation(Operations.Export)]
    public async Task ExportImageAsync_NullExtent_UsesDefaultExtent()
    {
        SetupLayerAndRasters();
        _rasterStore.ExportImageAsync(1, 100, Arg.Any<RasterQuery>(), Arg.Any<CancellationToken>())
            .Returns(CreateTestRasterResult());
        _temporaryFileService.StoreTemporaryFileAsync(
            Arg.Any<byte[]>(), Arg.Any<string>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>())
            .Returns("/temp/test.png");
        _rasterStore.GetExtentAsync(1, 100, Arg.Any<CancellationToken>())
            .Returns((RasterExtent?)null);

        var context = CreateImageServerContext();
        var request = CreateRequest();
        var result = await _handler.ExportImageAsync(context, 1, request);

        var jsonResult = result as JsonHttpResult<ExportImageResponse>;
        jsonResult.Should().NotBeNull();
        // When extent is null, defaults to 0,0,1,1
        jsonResult!.Value!.Extent.XMin.Should().Be(0);
        jsonResult.Value.Extent.YMin.Should().Be(0);
        jsonResult.Value.Extent.XMax.Should().Be(1);
        jsonResult.Value.Extent.YMax.Should().Be(1);
    }

    [UnitTest]
    [Operation(Operations.Export)]
    public async Task ExportImageAsync_ResponseContainsHrefAndDimensions()
    {
        SetupSuccessfulExport();

        var context = CreateImageServerContext();
        var request = CreateRequest();
        var result = await _handler.ExportImageAsync(context, 1, request);

        var jsonResult = result as JsonHttpResult<ExportImageResponse>;
        jsonResult.Should().NotBeNull();
        jsonResult!.Value!.Href.Should().Be("/temp/test.png");
        jsonResult.Value.Width.Should().Be(256);
        jsonResult.Value.Height.Should().Be(256);
    }

    [UnitTest]
    [Operation(Operations.Export)]
    public async Task ExportImageAsync_UsesExportedExtentWhenAvailable()
    {
        SetupLayerAndRasters();
        _rasterStore.ExportImageAsync(1, 100, Arg.Any<RasterQuery>(), Arg.Any<CancellationToken>())
            .Returns(CreateTestRasterResult() with
            {
                Srid = 3857,
                Extent = new RasterExtent
                {
                    XMin = -1000,
                    YMin = -500,
                    XMax = 1000,
                    YMax = 500,
                    Srid = 3857
                }
            });
        _temporaryFileService.StoreTemporaryFileAsync(
            Arg.Any<byte[]>(), Arg.Any<string>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>())
            .Returns("/temp/test.png");

        var context = CreateImageServerContext();
        var request = CreateRequest();
        var result = await _handler.ExportImageAsync(context, 1, request);

        var jsonResult = result as JsonHttpResult<ExportImageResponse>;
        jsonResult.Should().NotBeNull();
        jsonResult!.Value!.Extent.XMin.Should().Be(-1000);
        jsonResult.Value.Extent.YMin.Should().Be(-500);
        jsonResult.Value.Extent.XMax.Should().Be(1000);
        jsonResult.Value.Extent.YMax.Should().Be(500);
        jsonResult.Value.Extent.SpatialReference.Wkid.Should().Be(3857);

        await _rasterStore.DidNotReceive()
            .GetExtentAsync(1, 100, Arg.Any<CancellationToken>());
    }

    [UnitTest]
    [Operation(Operations.Export)]
    public async Task ExportImageAsync_RasterStoreThrows_ReturnsServerError()
    {
        SetupLayerAndRasters();
        _rasterStore.ExportImageAsync(1, 100, Arg.Any<RasterQuery>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("Export failed"));

        var context = CreateImageServerContext();
        var request = CreateRequest();
        var result = await _handler.ExportImageAsync(context, 1, request);
        await result.ExecuteAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status500InternalServerError);
    }

    [UnitTest]
    [Operation(Operations.Export)]
    public async Task ExportImageAsync_TemporaryStorageLimitExceeded_ReturnsServiceUnavailable()
    {
        SetupLayerAndRasters();
        _rasterStore.ExportImageAsync(1, 100, Arg.Any<RasterQuery>(), Arg.Any<CancellationToken>())
            .Returns(CreateTestRasterResult());
        _temporaryFileService.StoreTemporaryFileAsync(
            Arg.Any<byte[]>(),
            Arg.Any<string>(),
            Arg.Any<TimeSpan?>(),
            Arg.Any<CancellationToken>())
            .ThrowsAsync(new TemporaryStorageLimitExceededException("Storage is full", retryAfterSeconds: 42));

        var context = CreateImageServerContext();
        var request = CreateRequest();
        var result = await _handler.ExportImageAsync(context, 1, request);
        await result.ExecuteAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
        context.Response.Headers["Retry-After"].ToString().Should().Be("42");
    }

    private static DefaultHttpContext CreateImageServerContext()
    {
        var services = new ServiceCollection();
        services.AddSingleton<Microsoft.Extensions.Logging.ILoggerFactory>(NullLoggerFactory.Instance);

        var context = new DefaultHttpContext();
        context.RequestServices = services.BuildServiceProvider();
        context.Request.Path = "/rest/services/1/ImageServer/exportImage";
        context.Response.Body = new MemoryStream();
        return context;
    }

    private void SetupLayerAndRasters()
    {
        _layerCatalog.GetLayerAsync(1, Arg.Any<CancellationToken>())
            .Returns(CreateTestLayer());
        _rasterStore.ListRastersAsync(1, Arg.Any<CancellationToken>())
            .Returns(new[] { CreateTestRasterInfo() });
    }

    private void SetupSuccessfulExport()
    {
        SetupLayerAndRasters();
        _rasterStore.ExportImageAsync(1, 100, Arg.Any<RasterQuery>(), Arg.Any<CancellationToken>())
            .Returns(CreateTestRasterResult());
        _temporaryFileService.StoreTemporaryFileAsync(
            Arg.Any<byte[]>(), Arg.Any<string>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>())
            .Returns("/temp/test.png");
        _rasterStore.GetExtentAsync(1, 100, Arg.Any<CancellationToken>())
            .Returns(new RasterExtent { XMin = -180, YMin = -90, XMax = 180, YMax = 90, Srid = 4326 });
    }

    private static ExportImageRequest CreateRequest(
        string? bbox = null,
        int? size = null,
        string? format = null,
        string? interpolation = null,
        string? imageSr = null,
        string? bboxSr = null) => new()
        {
            Bbox = bbox,
            Size = size,
            Format = format ?? "png",
            Interpolation = interpolation,
            ImageSr = imageSr,
            BboxSr = bboxSr
        };

    private static LayerDefinition CreateTestLayer()
        => LayerDefinition.CreateBasic(1, "test-layer", GeometryType.Point);

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

    private static RasterResult CreateTestRasterResult() => new()
    {
        Data = new byte[] { 0x89, 0x50, 0x4E, 0x47 },
        ContentType = "image/png",
        Width = 256,
        Height = 256,
        Srid = 4326
    };
}
