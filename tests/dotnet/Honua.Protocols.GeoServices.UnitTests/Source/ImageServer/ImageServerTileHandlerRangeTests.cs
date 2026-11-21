// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.Domain;
using Honua.Core.Features.Tiles;
using Honua.Protocols.GeoServices.ImageServer.Handlers;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.ImageServer;

[Protocol(TestProtocols.ImageServer)]
public class ImageServerTileHandlerRangeTests
{
    private const string CogContentType = "image/tiff; application=geotiff; profile=cloud-optimized";

    private readonly TestMetadataV2GraphProvider _graphProvider = ImageServerTestFixtures.BuildGraphWithLayer(1);
    private readonly IRasterStore _rasterStore = Substitute.For<IRasterStore>();
    private readonly ImageServerTileHandler _handler;

    public ImageServerTileHandlerRangeTests()
    {
        _handler = new ImageServerTileHandler(
            _graphProvider,
            _rasterStore,
            NullLogger<ImageServerTileHandler>.Instance);
    }

    [UnitTest]
    [Operation(Operations.GetTile)]
    public async Task GetImageTileAsync_WithRangeHeader_ReturnsPartialContentAndContentRange()
    {
        var tileData = new byte[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9 };
        SetupRasterTile(tileData, RasterFormat.COG, CogContentType);

        var context = CreateContext(HttpMethods.Get, "bytes=2-5");
        var result = await _handler.GetImageTileAsync(context, 1, 0, 0, 0, "cog");
        await result.ExecuteAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status206PartialContent);
        context.Response.Headers.AcceptRanges.ToString().Should().Be("bytes");
        context.Response.Headers.ContentRange.ToString().Should().Be("bytes 2-5/10");
        context.Response.ContentLength.Should().Be(4);
        ((MemoryStream)context.Response.Body).ToArray().Should().Equal(2, 3, 4, 5);
    }

    [UnitTest]
    [Operation(Operations.GetTile)]
    public async Task GetImageTileAsync_HeadRequest_ReturnsAcceptRangesAndContentLength()
    {
        var tileData = new byte[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9 };
        SetupRasterTile(tileData, RasterFormat.COG, CogContentType);

        var context = CreateContext(HttpMethods.Head);
        var result = await _handler.GetImageTileAsync(context, 1, 0, 0, 0, "cog");
        await result.ExecuteAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        context.Response.Headers.AcceptRanges.ToString().Should().Be("bytes");
        context.Response.ContentLength.Should().Be(tileData.Length);
        ((MemoryStream)context.Response.Body).ToArray().Should().BeEmpty();
    }

    [UnitTest]
    [Operation(Operations.GetTile)]
    public async Task GetImageTileAsync_UnsatisfiableRangeHeader_ReturnsRangeNotSatisfiable()
    {
        var tileData = new byte[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9 };
        SetupRasterTile(tileData, RasterFormat.COG, CogContentType);

        var context = CreateContext(HttpMethods.Get, "bytes=100-200");
        var result = await _handler.GetImageTileAsync(context, 1, 0, 0, 0, "cog");
        await result.ExecuteAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status416RangeNotSatisfiable);
        context.Response.Headers.ContentRange.ToString().Should().Be("bytes */10");
    }

    [UnitTest]
    [Operation(Operations.GetTile)]
    public async Task GetImageTileAsync_CogFallback_WithRangeHeader_ReturnsPartialContent()
    {
        var tileData = new byte[] { 10, 11, 12, 13, 14, 15, 16, 17 };
        var cogResolver = Substitute.For<ICogTileResolver>();
        cogResolver.GetTileForLayerAsync(1, 0, 0, 0, RasterFormat.COG, Arg.Any<CancellationToken>())
            .Returns(new CogTileLookup(CreateTileResult(tileData, CogContentType), false));
        var handler = new ImageServerTileHandler(
            _graphProvider,
            _rasterStore,
            NullLogger<ImageServerTileHandler>.Instance,
            cogResolver);
        _rasterStore.QueryRastersAsync(default, default, default)
            .ReturnsForAnyArgs(Array.Empty<RasterInfo>());

        var context = CreateContext(HttpMethods.Get, "bytes=0-3");
        var result = await handler.GetImageTileAsync(context, 1, 0, 0, 0, "cog");
        await result.ExecuteAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status206PartialContent);
        context.Response.Headers.AcceptRanges.ToString().Should().Be("bytes");
        context.Response.Headers.ContentRange.ToString().Should().Be("bytes 0-3/8");
        ((MemoryStream)context.Response.Body).ToArray().Should().Equal(10, 11, 12, 13);
    }

    [UnitTest]
    [Operation(Operations.GetTile)]
    public async Task GetGridImageTileAsync_WithRangeHeader_ReturnsPartialContentAndContentRange()
    {
        var tileData = new byte[] { 20, 21, 22, 23, 24, 25 };
        _rasterStore.QueryRastersAsync(default, default, default)
            .ReturnsForAnyArgs([ImageServerTestFixtures.CreateWorldRgbRasterInfo()]);
        _rasterStore.GetImageTileAsync(default, default, default(RasterTileWindow), default, default)
            .ReturnsForAnyArgs(CreateTileResult(tileData, "image/png"));

        var registry = new TileMatrixSetRegistry(new TileMatrixSetDefinitionOptions());
        registry.TryGetGeometry(TileMatrixSetRegistry.WebMercatorQuadId, maxLevel: 4, out var grid).Should().BeTrue();

        var context = CreateContext(HttpMethods.Get, "bytes=1-3");
        var result = await _handler.GetGridImageTileAsync(context, 1, grid!, 0, 0, 0, "png");
        await result.ExecuteAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status206PartialContent);
        context.Response.Headers.AcceptRanges.ToString().Should().Be("bytes");
        context.Response.Headers.ContentRange.ToString().Should().Be("bytes 1-3/6");
        ((MemoryStream)context.Response.Body).ToArray().Should().Equal(21, 22, 23);
    }

    private void SetupRasterTile(byte[] data, RasterFormat format, string contentType)
    {
        _rasterStore.QueryRastersAsync(default, default, default)
            .ReturnsForAnyArgs([ImageServerTestFixtures.CreateWorldRgbRasterInfo()]);
        _rasterStore.GetImageTileAsync(1, 100, 0, 0, 0, format, Arg.Any<CancellationToken>())
            .Returns(CreateTileResult(data, contentType));
    }

    private static RasterResult CreateTileResult(byte[] data, string contentType) => new()
    {
        Data = data,
        ContentType = contentType,
        Width = 256,
        Height = 256,
        Srid = 3857
    };

    private static DefaultHttpContext CreateContext(string method, string? range = null)
    {
        var context = ImageServerTestFixtures.CreateContext("/rest/services/1/ImageServer/tile/0/0/0");
        context.Request.Method = method;
        if (range is not null)
        {
            context.Request.Headers.Range = range;
        }

        return context;
    }
}
