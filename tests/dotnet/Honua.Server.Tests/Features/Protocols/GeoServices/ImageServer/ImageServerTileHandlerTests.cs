// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Catalog.Abstractions;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.Domain;
using Honua.Server.Features.Protocols.GeoServices.ImageServer.Handlers;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.ImageServer;

/// <summary>
/// Tests for ImageServerTileHandler functionality.
/// </summary>
[Protocol(TestProtocols.ImageServer)]
public class ImageServerTileHandlerTests
{
    private readonly ILayerCatalog _layerCatalog = Substitute.For<ILayerCatalog>();
    private readonly IRasterStore _rasterStore = Substitute.For<IRasterStore>();
    private readonly ImageServerTileHandler _handler;

    public ImageServerTileHandlerTests()
    {
        _handler = new ImageServerTileHandler(
            _layerCatalog,
            _rasterStore,
            NullLogger<ImageServerTileHandler>.Instance);
    }

    [UnitTest]
    [Operation(Operations.GetTile)]
    public async Task GetImageTileAsync_LayerNotFound_ReturnsNotFound()
    {
        _layerCatalog.GetLayerAsync(99, Arg.Any<CancellationToken>())
            .Returns((LayerDefinition?)null);

        var context = CreateImageServerContext();
        var result = await _handler.GetImageTileAsync(context, 99, 0, 0, 0, "png");
        await result.ExecuteAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status404NotFound);
    }

    [UnitTest]
    [Operation(Operations.GetTile)]
    public async Task GetImageTileAsync_NegativeLevel_ReturnsBadRequest()
    {
        _layerCatalog.GetLayerAsync(1, Arg.Any<CancellationToken>())
            .Returns(CreateTestLayer());

        var context = CreateImageServerContext();
        var result = await _handler.GetImageTileAsync(context, 1, -1, 0, 0, "png");
        await result.ExecuteAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
    }

    [UnitTest]
    [Operation(Operations.GetTile)]
    public async Task GetImageTileAsync_LevelExceedsMax_ReturnsBadRequest()
    {
        _layerCatalog.GetLayerAsync(1, Arg.Any<CancellationToken>())
            .Returns(CreateTestLayer());

        var context = CreateImageServerContext();
        var result = await _handler.GetImageTileAsync(context, 1, 29, 0, 0, "png");
        await result.ExecuteAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
    }

    [UnitTest]
    [Operation(Operations.GetTile)]
    public async Task GetImageTileAsync_RowExceedsBound_ReturnsBadRequest()
    {
        _layerCatalog.GetLayerAsync(1, Arg.Any<CancellationToken>())
            .Returns(CreateTestLayer());

        // At level 2, max row is 3 (2^2 - 1)
        var context = CreateImageServerContext();
        var result = await _handler.GetImageTileAsync(context, 1, 2, 4, 0, "png");
        await result.ExecuteAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
    }

    [UnitTest]
    [Operation(Operations.GetTile)]
    public async Task GetImageTileAsync_ColExceedsBound_ReturnsBadRequest()
    {
        _layerCatalog.GetLayerAsync(1, Arg.Any<CancellationToken>())
            .Returns(CreateTestLayer());

        // At level 1, max col is 1 (2^1 - 1)
        var context = CreateImageServerContext();
        var result = await _handler.GetImageTileAsync(context, 1, 1, 0, 2, "png");
        await result.ExecuteAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
    }

    [UnitTest]
    [Operation(Operations.GetTile)]
    public async Task GetImageTileAsync_UnsupportedFormat_ReturnsBadRequest()
    {
        _layerCatalog.GetLayerAsync(1, Arg.Any<CancellationToken>())
            .Returns(CreateTestLayer());

        var context = CreateImageServerContext();
        var result = await _handler.GetImageTileAsync(context, 1, 0, 0, 0, "bmp");
        await result.ExecuteAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        await _rasterStore.DidNotReceive()
            .QueryRastersAsync(Arg.Any<int>(), Arg.Any<RasterSelectionQuery>(), Arg.Any<CancellationToken>());
    }

    [UnitTest]
    [Operation(Operations.GetTile)]
    public async Task GetImageTileAsync_NoRasters_ReturnsNotFound()
    {
        _layerCatalog.GetLayerAsync(1, Arg.Any<CancellationToken>())
            .Returns(CreateTestLayer());
        _rasterStore.QueryRastersAsync(default, default, default)
            .ReturnsForAnyArgs(Array.Empty<RasterInfo>());

        var context = CreateImageServerContext();
        var result = await _handler.GetImageTileAsync(context, 1, 0, 0, 0, "png");
        await result.ExecuteAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status404NotFound);
    }

    [UnitTest]
    [Operation(Operations.GetTile)]
    public async Task GetImageTileAsync_TileNotFound_ReturnsNotFound()
    {
        _layerCatalog.GetLayerAsync(1, Arg.Any<CancellationToken>())
            .Returns(CreateTestLayer());
        _rasterStore.QueryRastersAsync(default, default, default)
            .ReturnsForAnyArgs([CreateTestRasterInfo()]);
        _rasterStore.GetImageTileAsync(1, 100, 0, 0, 0, RasterFormat.PNG, Arg.Any<CancellationToken>())
            .Returns((RasterResult?)null);

        var context = CreateImageServerContext();
        var result = await _handler.GetImageTileAsync(context, 1, 0, 0, 0, "png");
        await result.ExecuteAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status404NotFound);
    }

    [UnitTest]
    [Operation(Operations.GetTile)]
    public async Task GetImageTileAsync_ValidTile_ReturnsFileResult()
    {
        var tileData = new byte[] { 0x89, 0x50, 0x4E, 0x47 }; // PNG header
        _layerCatalog.GetLayerAsync(1, Arg.Any<CancellationToken>())
            .Returns(CreateTestLayer());
        _rasterStore.QueryRastersAsync(default, default, default)
            .ReturnsForAnyArgs([CreateTestRasterInfo()]);
        _rasterStore.GetImageTileAsync(1, 100, 0, 0, 0, RasterFormat.PNG, Arg.Any<CancellationToken>())
            .Returns(new RasterResult
            {
                Data = tileData,
                ContentType = "image/png",
                Width = 256,
                Height = 256,
                Srid = 3857
            });

        var context = CreateImageServerContext();
        var result = await _handler.GetImageTileAsync(context, 1, 0, 0, 0, "png");

        result.Should().BeOfType<FileContentHttpResult>();
    }

    [Trait("Category", "Unit")]
    [Operation(Operations.GetTile)]
    [Theory]
    [InlineData(0, 0, 0)]   // Level 0: 1x1 grid
    [InlineData(1, 0, 0)]   // Level 1: 2x2 grid
    [InlineData(1, 1, 1)]   // Level 1: max row/col
    [InlineData(2, 3, 3)]   // Level 2: 4x4 grid, max
    public async Task GetImageTileAsync_ValidCoordinates_DoesNotReturnBadRequest(int level, int row, int col)
    {
        _layerCatalog.GetLayerAsync(1, Arg.Any<CancellationToken>())
            .Returns(CreateTestLayer());
        _rasterStore.QueryRastersAsync(default, default, default)
            .ReturnsForAnyArgs([CreateTestRasterInfo()]);
        _rasterStore.GetImageTileAsync(1, 100, level, row, col, RasterFormat.PNG, Arg.Any<CancellationToken>())
            .Returns(new RasterResult
            {
                Data = [0x89],
                ContentType = "image/png",
                Width = 256,
                Height = 256,
                Srid = 3857
            });

        var context = CreateImageServerContext();
        var result = await _handler.GetImageTileAsync(context, 1, level, row, col, "png");
        await result.ExecuteAsync(context);

        context.Response.StatusCode.Should().NotBe(StatusCodes.Status400BadRequest);
    }

    private static DefaultHttpContext CreateImageServerContext()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        var context = new DefaultHttpContext();
        context.RequestServices = services.BuildServiceProvider();
        context.Request.Path = "/rest/services/1/ImageServer/tile/0/0/0";
        context.Response.Body = new MemoryStream();
        return context;
    }

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
}
