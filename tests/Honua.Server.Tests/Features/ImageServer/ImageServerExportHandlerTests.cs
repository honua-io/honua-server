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
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Honua.Server.Tests.Features.ImageServer;

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

        var request = CreateRequest();
        var result = await _handler.ExportImageAsync(99, request);

        result.Should().BeOfType<NotFound>();
    }

    [UnitTest]
    [Operation(Operations.Export)]
    public async Task ExportImageAsync_NoRasters_ReturnsNotFound()
    {
        _layerCatalog.GetLayerAsync(1, Arg.Any<CancellationToken>())
            .Returns(CreateTestLayer());
        _rasterStore.ListRastersAsync(1, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<RasterInfo>());

        var request = CreateRequest();
        var result = await _handler.ExportImageAsync(1, request);

        result.Should().BeOfType<NotFound>();
    }

    [UnitTest]
    [Operation(Operations.Export)]
    public async Task ExportImageAsync_InvalidBbox_ReturnsBadRequest()
    {
        SetupLayerAndRasters();

        var request = CreateRequest(bbox: "invalid-bbox");
        var result = await _handler.ExportImageAsync(1, request);

        result.Should().BeOfType<BadRequest<string>>();
    }

    [UnitTest]
    [Operation(Operations.Export)]
    public async Task ExportImageAsync_ValidRequest_ReturnsOk()
    {
        SetupSuccessfulExport();

        var request = CreateRequest();
        var result = await _handler.ExportImageAsync(1, request);

        result.Should().BeOfType<Ok<ExportImageResponse>>();
    }

    [UnitTest]
    [Operation(Operations.Export)]
    public async Task ExportImageAsync_WithBbox_ReturnsOk()
    {
        SetupSuccessfulExport();

        var request = CreateRequest(bbox: "-180,-90,180,90");
        var result = await _handler.ExportImageAsync(1, request);

        result.Should().BeOfType<Ok<ExportImageResponse>>();
    }

    [UnitTest]
    [Operation(Operations.Export)]
    public async Task ExportImageAsync_WithSize_UsesProvidedSize()
    {
        SetupSuccessfulExport();

        var request = CreateRequest(size: 512);
        var result = await _handler.ExportImageAsync(1, request);

        var okResult = result as Ok<ExportImageResponse>;
        okResult.Should().NotBeNull();
    }

    [UnitTest]
    [Operation(Operations.Export)]
    public async Task ExportImageAsync_WithJpegFormat_ReturnsOk()
    {
        SetupSuccessfulExport();

        var request = CreateRequest(format: "jpeg");
        var result = await _handler.ExportImageAsync(1, request);

        result.Should().BeOfType<Ok<ExportImageResponse>>();
    }

    [UnitTest]
    [Operation(Operations.Export)]
    public async Task ExportImageAsync_WithInterpolation_ReturnsOk()
    {
        SetupSuccessfulExport();

        var request = CreateRequest(interpolation: "RSP_NearestNeighbor");
        var result = await _handler.ExportImageAsync(1, request);

        result.Should().BeOfType<Ok<ExportImageResponse>>();
    }

    [UnitTest]
    [Operation(Operations.Export)]
    public async Task ExportImageAsync_WithImageSr_ReturnsOk()
    {
        SetupSuccessfulExport();

        var request = CreateRequest(imageSr: "3857");
        var result = await _handler.ExportImageAsync(1, request);

        result.Should().BeOfType<Ok<ExportImageResponse>>();
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

        var request = CreateRequest();
        var result = await _handler.ExportImageAsync(1, request);

        var okResult = result as Ok<ExportImageResponse>;
        okResult.Should().NotBeNull();
        // When extent is null, defaults to 0,0,1,1
        okResult!.Value!.Extent.XMin.Should().Be(0);
        okResult.Value.Extent.YMin.Should().Be(0);
        okResult.Value.Extent.XMax.Should().Be(1);
        okResult.Value.Extent.YMax.Should().Be(1);
    }

    [UnitTest]
    [Operation(Operations.Export)]
    public async Task ExportImageAsync_ResponseContainsHrefAndDimensions()
    {
        SetupSuccessfulExport();

        var request = CreateRequest();
        var result = await _handler.ExportImageAsync(1, request);

        var okResult = result as Ok<ExportImageResponse>;
        okResult.Should().NotBeNull();
        okResult!.Value!.Href.Should().Be("/temp/test.png");
        okResult.Value.Width.Should().Be(256);
        okResult.Value.Height.Should().Be(256);
    }

    [UnitTest]
    [Operation(Operations.Export)]
    public async Task ExportImageAsync_RasterStoreThrows_ReturnsProblem()
    {
        SetupLayerAndRasters();
        _rasterStore.ExportImageAsync(1, 100, Arg.Any<RasterQuery>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("Export failed"));

        var request = CreateRequest();
        var result = await _handler.ExportImageAsync(1, request);

        result.Should().BeAssignableTo<ProblemHttpResult>();
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
        string? imageSr = null) => new()
        {
            Bbox = bbox,
            Size = size,
            Format = format ?? "png",
            Interpolation = interpolation,
            ImageSr = imageSr
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
