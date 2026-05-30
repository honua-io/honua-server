// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.TestKit.Infrastructure;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.Domain;
using Honua.Protocols.GeoServices.ImageServer.Handlers;
using Honua.Protocols.GeoServices.ImageServer.Models;
using Honua.Infrastructure.Services;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using System.Security.Claims;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.ImageServer;

/// <summary>
/// Tests for ImageServerExportHandler functionality.
/// </summary>
[Protocol(TestProtocols.ImageServer)]
public class ImageServerExportHandlerTests
{
    private readonly TestMetadataV2GraphProvider _graphProvider = BuildGraphWithLayer(1);
    private readonly IRasterStore _rasterStore = Substitute.For<IRasterStore>();
    private readonly ITemporaryFileService _temporaryFileService = Substitute.For<ITemporaryFileService>();
    private readonly ImageServerExportHandler _handler;

    public ImageServerExportHandlerTests()
    {
        _handler = new ImageServerExportHandler(
            _graphProvider,
            _rasterStore,
            _temporaryFileService,
            NullLogger<ImageServerExportHandler>.Instance);
    }

    [UnitTest]
    [Operation(Operations.Export)]
    public async Task ExportImageAsync_LayerNotFound_ReturnsNotFound()
    {

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
        _rasterStore.QueryRastersAsync(default, default, default)
            .ReturnsForAnyArgs(Array.Empty<RasterInfo>());

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
        var request = CreateRequest(rawSize: "512");
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
        SetupLayerAndRasters();
        RasterQuery? capturedQuery = null;
        _rasterStore.ExportImageAsync(1, 100, Arg.Any<RasterQuery>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                capturedQuery = callInfo.ArgAt<RasterQuery>(2);
                return CreateTestRasterResult();
            });
        _temporaryFileService.StoreTemporaryFileAsync(
            Arg.Any<byte[]>(),
            Arg.Any<string>(),
            Arg.Any<TimeSpan?>(),
            Arg.Any<ClaimsPrincipal?>(),
            Arg.Any<CancellationToken>())
            .Returns("/temp/test.png");
        _rasterStore.GetExtentAsync(1, 100, Arg.Any<CancellationToken>())
            .Returns(new RasterExtent { XMin = -180, YMin = -90, XMax = 180, YMax = 90, Srid = 4326 });

        var context = CreateImageServerContext();
        var request = CreateRequest(rawSize: "512,256");
        var result = await _handler.ExportImageAsync(context, 1, request);

        result.Should().BeOfType<JsonHttpResult<ExportImageResponse>>();
        capturedQuery.Should().NotBeNull();
        capturedQuery!.Value.OutputWidth.Should().Be(512);
        capturedQuery.Value.OutputHeight.Should().Be(256);
    }

    [UnitTest]
    [Operation(Operations.Export)]
    public async Task ExportImageAsync_WithoutSize_UsesArcGisDefaultDimensions()
    {
        SetupLayerAndRasters();
        RasterQuery? capturedQuery = null;
        _rasterStore.ExportImageAsync(1, 100, Arg.Any<RasterQuery>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                capturedQuery = callInfo.ArgAt<RasterQuery>(2);
                return CreateTestRasterResult();
            });
        _temporaryFileService.StoreTemporaryFileAsync(
            Arg.Any<byte[]>(),
            Arg.Any<string>(),
            Arg.Any<TimeSpan?>(),
            Arg.Any<ClaimsPrincipal?>(),
            Arg.Any<CancellationToken>())
            .Returns("/temp/test.png");
        _rasterStore.GetExtentAsync(1, 100, Arg.Any<CancellationToken>())
            .Returns(new RasterExtent { XMin = -10, YMin = -10, XMax = 10, YMax = 30, Srid = 4326 });

        var context = CreateImageServerContext();
        var request = CreateRequest();
        var result = await _handler.ExportImageAsync(context, 1, request);

        result.Should().BeOfType<JsonHttpResult<ExportImageResponse>>();
        capturedQuery.Should().NotBeNull();
        capturedQuery!.Value.OutputWidth.Should().Be(400);
        capturedQuery.Value.OutputHeight.Should().Be(400);
    }

    [UnitTest]
    [Operation(Operations.Export)]
    public async Task ExportImageAsync_WithRenderingRule_ReturnsNotImplemented()
    {
        SetupLayerAndRasters();

        var context = CreateImageServerContext();
        var request = CreateRequest(renderingRule: "{\"rasterFunction\":\"Identity\"}");
        var result = await _handler.ExportImageAsync(context, 1, request);
        await result.ExecuteAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status501NotImplemented);
        await _rasterStore.DidNotReceive()
            .ExportImageAsync(1, 100, Arg.Any<RasterQuery>(), Arg.Any<CancellationToken>());
    }

    [UnitTest]
    [Operation(Operations.Export)]
    public async Task ExportImageAsync_WithMosaicRule_ExportsMosaic()
    {
        _rasterStore.QueryRastersAsync(default, default, default)
            .ReturnsForAnyArgs(
            [
                CreateTestRasterInfo(),
                CreateTestRasterInfo() with { Id = 101, Name = "second-raster" }
            ]);

        long[]? capturedRasterIds = null;
        RasterMergeStrategy? capturedMergeStrategy = null;
        _rasterStore.ExportMosaicAsync(
                1,
                Arg.Any<long[]>(),
                Arg.Any<RasterMergeStrategy>(),
                Arg.Any<RasterQuery>(),
                Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                capturedRasterIds = callInfo.ArgAt<long[]>(1);
                capturedMergeStrategy = callInfo.ArgAt<RasterMergeStrategy>(2);
                return CreateTestRasterResult();
            });
        _temporaryFileService.StoreTemporaryFileAsync(
            Arg.Any<byte[]>(),
            Arg.Any<string>(),
            Arg.Any<TimeSpan?>(),
            Arg.Any<ClaimsPrincipal?>(),
            Arg.Any<CancellationToken>())
            .Returns("/temp/test.png");

        var context = CreateImageServerContext();
        var request = CreateRequest(mosaicRule: "{\"mergeStrategy\":\"max\"}");
        var result = await _handler.ExportImageAsync(context, 1, request);

        result.Should().BeOfType<JsonHttpResult<ExportImageResponse>>();
        capturedRasterIds.Should().NotBeNull();
        capturedRasterIds!.Should().Equal(100L, 101L);
        capturedMergeStrategy.Should().NotBeNull();
        capturedMergeStrategy!.Value.Should().Be(RasterMergeStrategy.Max);
        await _rasterStore.Received(1)
            .ExportMosaicAsync(
                1,
                Arg.Any<long[]>(),
                RasterMergeStrategy.Max,
                Arg.Any<RasterQuery>(),
                Arg.Any<CancellationToken>());
        await _rasterStore.DidNotReceive()
            .ExportImageAsync(1, 100, Arg.Any<RasterQuery>(), Arg.Any<CancellationToken>());
    }

    [UnitTest]
    [Operation(Operations.Export)]
    public async Task ExportImageAsync_WithMosaicRuleAndSingleRaster_AllowsExport()
    {
        _rasterStore.QueryRastersAsync(default, default, default)
            .ReturnsForAnyArgs([CreateTestRasterInfo() with { Width = 100, Height = 20000 }]);
        _rasterStore.ExportImageAsync(1, 100, Arg.Any<RasterQuery>(), Arg.Any<CancellationToken>())
            .Returns(CreateTestRasterResult());
        _temporaryFileService.StoreTemporaryFileAsync(
            Arg.Any<byte[]>(),
            Arg.Any<string>(),
            Arg.Any<TimeSpan?>(),
            Arg.Any<ClaimsPrincipal?>(),
            Arg.Any<CancellationToken>())
            .Returns("/temp/test.png");
        _rasterStore.GetExtentAsync(1, 100, Arg.Any<CancellationToken>())
            .Returns(new RasterExtent { XMin = -180, YMin = -90, XMax = 180, YMax = 90, Srid = 4326 });

        var context = CreateImageServerContext();
        var request = CreateRequest(mosaicRule: "{\"mosaicMethod\":\"esriMosaicLockRaster\",\"lockRasterIds\":[8]}");
        var result = await _handler.ExportImageAsync(context, 1, request);

        result.Should().BeOfType<JsonHttpResult<ExportImageResponse>>();
    }

    [UnitTest]
    [Operation(Operations.Export)]
    public async Task ExportImageAsync_WithExplicitPixelType_ReturnsNotImplemented()
    {
        SetupLayerAndRasters();

        var context = CreateImageServerContext();
        var request = CreateRequest(pixelType: "U8");
        var result = await _handler.ExportImageAsync(context, 1, request);
        await result.ExecuteAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status501NotImplemented);
        await _rasterStore.DidNotReceive()
            .ExportImageAsync(1, 100, Arg.Any<RasterQuery>(), Arg.Any<CancellationToken>());
    }

    [UnitTest]
    [Operation(Operations.Export)]
    public async Task ExportImageAsync_WithUnknownPixelType_AllowsExport()
    {
        SetupSuccessfulExport();

        var context = CreateImageServerContext();
        var request = CreateRequest(pixelType: "UNKNOWN");
        var result = await _handler.ExportImageAsync(context, 1, request);

        result.Should().BeOfType<JsonHttpResult<ExportImageResponse>>();
    }

    [UnitTest]
    [Operation(Operations.Export)]
    public async Task ExportImageAsync_WithImageFormat_ReturnsInlineBytesWithoutTemporaryStorage()
    {
        SetupLayerAndRasters();
        _rasterStore.ExportImageAsync(1, 100, Arg.Any<RasterQuery>(), Arg.Any<CancellationToken>())
            .Returns(CreateTestRasterResult());

        var context = CreateImageServerContext();
        var request = CreateRequest(responseFormat: "image");
        var result = await _handler.ExportImageAsync(context, 1, request);
        await result.ExecuteAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        context.Response.ContentType.Should().Be("image/png");
        context.Response.Body.Position = 0;
        using var reader = new BinaryReader(context.Response.Body);
        reader.ReadBytes((int)context.Response.Body.Length).Should().Equal(CreateTestRasterResult().Data);

        await _temporaryFileService.DidNotReceive()
            .StoreTemporaryFileAsync(
                Arg.Any<byte[]>(),
                Arg.Any<string>(),
                Arg.Any<TimeSpan?>(),
                Arg.Any<ClaimsPrincipal?>(),
                Arg.Any<CancellationToken>());
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
    public async Task ExportImageAsync_WithTiffCompression_MapsCompressionIntoRasterQuery()
    {
        SetupLayerAndRasters();
        RasterQuery? capturedQuery = null;
        _rasterStore.ExportImageAsync(1, 100, Arg.Any<RasterQuery>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                capturedQuery = callInfo.ArgAt<RasterQuery>(2);
                return CreateTestRasterResult() with { ContentType = "image/tiff" };
            });
        _temporaryFileService.StoreTemporaryFileAsync(
            Arg.Any<byte[]>(),
            Arg.Any<string>(),
            Arg.Any<TimeSpan?>(),
            Arg.Any<ClaimsPrincipal?>(),
            Arg.Any<CancellationToken>())
            .Returns("/temp/test.tiff");
        _rasterStore.GetExtentAsync(1, 100, Arg.Any<CancellationToken>())
            .Returns(new RasterExtent { XMin = -180, YMin = -90, XMax = 180, YMax = 90, Srid = 4326 });

        var context = CreateImageServerContext();
        var request = CreateRequest(format: "tiff", compression: "LZ77", compressionQuality: 80);
        var result = await _handler.ExportImageAsync(context, 1, request);

        result.Should().BeOfType<JsonHttpResult<ExportImageResponse>>();
        capturedQuery.Should().NotBeNull();
        capturedQuery!.Value.OutputFormat.Should().Be(RasterFormat.TIFF);
        capturedQuery.Value.TiffCompression.Should().Be(TiffCompression.Deflate);
        capturedQuery.Value.Quality.Should().Be(80);
    }

    [UnitTest]
    [Operation(Operations.Export)]
    public async Task ExportImageAsync_ForwardsCallerPrincipalToTemporaryStorage()
    {
        SetupSuccessfulExport();
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, "image-export-user")
        ], "test"));

        var context = CreateImageServerContext();
        context.User = principal;

        var request = CreateRequest();
        var result = await _handler.ExportImageAsync(context, 1, request);

        result.Should().BeOfType<JsonHttpResult<ExportImageResponse>>();
        await _temporaryFileService.Received(1).StoreTemporaryFileAsync(
            Arg.Any<byte[]>(),
            Arg.Any<string>(),
            Arg.Any<TimeSpan?>(),
            Arg.Is<ClaimsPrincipal?>(candidate => ReferenceEquals(candidate, principal)),
            Arg.Any<CancellationToken>());
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
    public async Task ExportImageAsync_NullExtent_FallsBackToSelectedRasterExtent()
    {
        SetupLayerAndRasters();
        _rasterStore.ExportImageAsync(1, 100, Arg.Any<RasterQuery>(), Arg.Any<CancellationToken>())
            .Returns(CreateTestRasterResult());
        _temporaryFileService.StoreTemporaryFileAsync(
            Arg.Any<byte[]>(),
            Arg.Any<string>(),
            Arg.Any<TimeSpan?>(),
            Arg.Any<System.Security.Claims.ClaimsPrincipal?>(),
            Arg.Any<CancellationToken>())
            .Returns("/temp/test.png");
        _rasterStore.GetExtentAsync(1, 100, Arg.Any<CancellationToken>())
            .Returns((RasterExtent?)null);

        var context = CreateImageServerContext();
        var request = CreateRequest();
        var result = await _handler.ExportImageAsync(context, 1, request);

        var jsonResult = result as JsonHttpResult<ExportImageResponse>;
        jsonResult.Should().NotBeNull();
        jsonResult!.Value!.Extent.XMin.Should().Be(-180);
        jsonResult.Value.Extent.YMin.Should().Be(-90);
        jsonResult.Value.Extent.XMax.Should().Be(180);
        jsonResult.Value.Extent.YMax.Should().Be(90);
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
            Arg.Any<byte[]>(),
            Arg.Any<string>(),
            Arg.Any<TimeSpan?>(),
            Arg.Any<System.Security.Claims.ClaimsPrincipal?>(),
            Arg.Any<CancellationToken>())
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
            Arg.Any<System.Security.Claims.ClaimsPrincipal?>(),
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
        _rasterStore.QueryRastersAsync(default, default, default)
            .ReturnsForAnyArgs([CreateTestRasterInfo()]);
    }

    private void SetupSuccessfulExport()
    {
        SetupLayerAndRasters();
        _rasterStore.ExportImageAsync(1, 100, Arg.Any<RasterQuery>(), Arg.Any<CancellationToken>())
            .Returns(CreateTestRasterResult());
        _temporaryFileService.StoreTemporaryFileAsync(
            Arg.Any<byte[]>(),
            Arg.Any<string>(),
            Arg.Any<TimeSpan?>(),
            Arg.Any<System.Security.Claims.ClaimsPrincipal?>(),
            Arg.Any<CancellationToken>())
            .Returns("/temp/test.png");
        _rasterStore.GetExtentAsync(1, 100, Arg.Any<CancellationToken>())
            .Returns(new RasterExtent { XMin = -180, YMin = -90, XMax = 180, YMax = 90, Srid = 4326 });
    }

    private static ExportImageRequest CreateRequest(
        string? bbox = null,
        int? size = null,
        string? rawSize = null,
        string? format = null,
        string? compression = null,
        int? compressionQuality = null,
        string? interpolation = null,
        string? imageSr = null,
        string? bboxSr = null,
        string? responseFormat = null,
        string? pixelType = null,
        string? renderingRule = null,
        string? mosaicRule = null) => new()
        {
            Bbox = bbox,
            Size = rawSize ?? (size.HasValue
                ? $"{size.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)},{size.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)}"
                : null),
            Format = format ?? "png",
            Compression = compression,
            CompressionQuality = compressionQuality ?? 75,
            Interpolation = interpolation,
            ImageSr = imageSr,
            BboxSr = bboxSr,
            F = responseFormat ?? "json",
            PixelType = pixelType,
            RenderingRule = renderingRule,
            MosaicRule = mosaicRule,
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

    private static RasterResult CreateTestRasterResult() => new()
    {
        Data = new byte[] { 0x89, 0x50, 0x4E, 0x47 },
        ContentType = "image/png",
        Width = 256,
        Height = 256,
        Srid = 4326
    };
}
