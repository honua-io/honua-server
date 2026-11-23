// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.Domain;
using Honua.Protocols.GeoServices.ImageServer;
using Honua.Protocols.GeoServices.ImageServer.Handlers;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.ImageServer;

[Protocol(TestProtocols.ImageServer)]
public sealed class ImageServerTileRangeTests
{
    private static readonly byte[] SampleTileBytes = [0x49, 0x49, 0x2A, 0x00, 0x08, 0x00, 0x00, 0x00];

    [UnitTest]
    [Operation(Operations.GetTile)]
    public async Task GetImageTileAsync_CloudCogWithRangeHeader_ReturnsPartialContent()
    {
        var handler = CreateCloudCogHandler();
        var context = ImageServerTestFixtures.CreateContext("/rest/services/1/ImageServer/tile/0/0/0?format=cog");
        context.Request.Method = HttpMethods.Get;
        context.Request.Headers.Range = "bytes=0-3";

        var result = await handler.GetImageTileAsync(context, 1, 0, 0, 0, "cog");

        var fileResult = result.Should().BeOfType<FileContentHttpResult>().Subject;
        fileResult.EnableRangeProcessing.Should().BeTrue();
        await result.ExecuteAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status206PartialContent);
        context.Response.Headers.AcceptRanges.ToString().Should().Be("bytes");
        context.Response.Headers.ContentRange.ToString().Should().Be("bytes 0-3/8");
        context.Response.Body.Position = 0;
        ((MemoryStream)context.Response.Body).ToArray().Should().Equal(SampleTileBytes[..4]);
    }

    [UnitTheory]
    [Operation(Operations.GetTile)]
    [InlineData(24, 23)]
    [InlineData(28, 23)]
    [InlineData(11, 10)]
    public async Task RefuseLevelOutsideTileInfo_LevelBeyondMaxLevel_AnswersBadRequest(int level, int maxLevel)
    {
        var context = ContextWithMaxLevel($"/rest/services/1/ImageServer/tile/{level}/0/0", maxLevel);

        var refusal = ImageServerTileHandler.RefuseLevelOutsideTileInfo(context, level);

        refusal.Should().NotBeNull();
        await refusal!.ExecuteAsync(context);
        context.Response.Body.Position = 0;
        using var document = await JsonDocument.ParseAsync(context.Response.Body);
        document.RootElement.GetProperty("error").GetProperty("code").GetInt32().Should().Be(400);
    }

    [UnitTheory]
    [Operation(Operations.GetTile)]
    [InlineData(10, 10)]
    [InlineData(0, 10)]
    [InlineData(23, 23)]
    public void RefuseLevelOutsideTileInfo_AdvertisedLevel_IsAdmitted(int level, int maxLevel)
        => ImageServerTileHandler.RefuseLevelOutsideTileInfo(ContextWithMaxLevel("/rest/services/1/ImageServer/tile/0/0/0", maxLevel), level)
            .Should().BeNull();

    [UnitTest]
    [Operation(Operations.GetTile)]
    public void RefuseLevelOutsideTileInfo_DynamicServiceAdvertisingNoLevels_AdmitsTheFullGrid()
    {
        var context = ImageServerTestFixtures.CreateContext(
            "/rest/services/1/ImageServer/tile/28/0/0",
            services => services.Configure<ImageServerTileMetadataOptions>(options => options.MaxLevel = 10));

        ImageServerTileHandler.RefuseLevelOutsideTileInfo(context, 28).Should().BeNull();
    }

    // WMTS shares the renderer but advertises its own tile matrix set (levels 0-22), so a level above
    // TileMetadata:MaxLevel is still served through the handler.
    [UnitTest]
    [Operation(Operations.GetTile)]
    public async Task GetImageTileAsync_LevelAboveTileInfoMaxLevel_IsStillServedForWmts()
    {
        var handler = CreateCloudCogHandler(level: 11);
        var context = ContextWithMaxLevel("/rest/services/1/ImageServer/WMTS/tile/1.0.0/1/default/WebMercatorQuad/11/0/0", 10);
        context.Request.Method = HttpMethods.Get;

        var result = await handler.GetImageTileAsync(context, 1, 11, 0, 0, "cog");
        await result.ExecuteAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        ((MemoryStream)context.Response.Body).ToArray().Should().Equal(SampleTileBytes);
    }

    private static DefaultHttpContext ContextWithMaxLevel(string path, int maxLevel)
        => ImageServerTestFixtures.CreateContext(
            path,
            services => services.Configure<ImageServerTileMetadataOptions>(options =>
            {
                options.Enabled = true;
                options.MaxLevel = maxLevel;
            }));

    [UnitTest]
    [Operation(Operations.GetTile)]
    public async Task GetImageTileAsync_PostGisTileWithRangeHeader_ReturnsPartialContent()
    {
        var graphProvider = ImageServerTestFixtures.BuildGraphWithLayer(1);
        var rasterStore = Substitute.For<IRasterStore>();
        rasterStore.QueryRastersAsync(default, default, default)
            .ReturnsForAnyArgs([ImageServerTestFixtures.CreateWorldRgbRasterInfo()]);
        rasterStore.GetImageTileAsync(1, 100, 0, 0, 0, RasterFormat.TIFF, Arg.Any<CancellationToken>())
            .Returns(new RasterResult
            {
                Data = SampleTileBytes,
                ContentType = "image/tiff",
                Width = 256,
                Height = 256,
                Srid = 3857
            });

        var handler = new ImageServerTileHandler(
            graphProvider,
            rasterStore,
            NullLogger<ImageServerTileHandler>.Instance);
        var context = ImageServerTestFixtures.CreateContext("/rest/services/1/ImageServer/tile/0/0/0?format=tiff");
        context.Request.Method = HttpMethods.Get;
        context.Request.Headers.Range = "bytes=2-5";

        var result = await handler.GetImageTileAsync(context, 1, 0, 0, 0, "tiff");

        var fileResult = result.Should().BeOfType<FileContentHttpResult>().Subject;
        fileResult.EnableRangeProcessing.Should().BeTrue();
        await result.ExecuteAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status206PartialContent);
        context.Response.Headers.AcceptRanges.ToString().Should().Be("bytes");
        context.Response.Headers.ContentRange.ToString().Should().Be("bytes 2-5/8");
        context.Response.Body.Position = 0;
        ((MemoryStream)context.Response.Body).ToArray().Should().Equal(SampleTileBytes[2..6]);
    }

    [UnitTest]
    [Operation(Operations.GetTile)]
    public async Task GetImageTileAsync_HeadRequest_AdvertisesAcceptRangesAndContentLength()
    {
        var handler = CreateCloudCogHandler();
        var context = ImageServerTestFixtures.CreateContext("/rest/services/1/ImageServer/tile/0/0/0?format=cog");
        context.Request.Method = HttpMethods.Head;

        var result = await handler.GetImageTileAsync(context, 1, 0, 0, 0, "cog");
        await result.ExecuteAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        context.Response.Headers.AcceptRanges.ToString().Should().Be("bytes");
        context.Response.ContentLength.Should().Be(SampleTileBytes.Length);
        context.Response.Body.Length.Should().Be(0);
    }

    private static ImageServerTileHandler CreateCloudCogHandler(int level = 0)
    {
        var graphProvider = ImageServerTestFixtures.BuildGraphWithLayer(1);
        var rasterStore = Substitute.For<IRasterStore>();
        rasterStore.QueryRastersAsync(default, default, default)
            .ReturnsForAnyArgs([]);
        var cogResolver = Substitute.For<ICogTileResolver>();
        cogResolver.GetTileForLayerAsync(1, level, 0, 0, RasterFormat.COG, Arg.Any<CancellationToken>())
            .Returns(new CogTileLookup(
                new RasterResult
                {
                    Data = SampleTileBytes,
                    ContentType = "image/tiff",
                    Width = 256,
                    Height = 256,
                    Srid = 3857
                },
                EditionGateHit: false));

        return new ImageServerTileHandler(
            graphProvider,
            rasterStore,
            NullLogger<ImageServerTileHandler>.Instance,
            cogResolver);
    }
}
