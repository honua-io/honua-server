// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.Domain;
using Honua.Protocols.GeoServices.ImageServer.Handlers;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
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

    private static ImageServerTileHandler CreateCloudCogHandler()
    {
        var graphProvider = ImageServerTestFixtures.BuildGraphWithLayer(1);
        var rasterStore = Substitute.For<IRasterStore>();
        rasterStore.QueryRastersAsync(default, default, default)
            .ReturnsForAnyArgs([]);
        var cogResolver = Substitute.For<ICogTileResolver>();
        cogResolver.GetTileForLayerAsync(1, 0, 0, 0, RasterFormat.COG, Arg.Any<CancellationToken>())
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
