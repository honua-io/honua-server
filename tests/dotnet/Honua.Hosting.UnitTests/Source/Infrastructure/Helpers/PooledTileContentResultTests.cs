// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Tiles;
using Honua.Infrastructure.Caching;
using Honua.Infrastructure.Helpers;
using Honua.Infrastructure.SharedCaching;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Hosting.UnitTests.Infrastructure.Helpers;

public sealed class PooledTileContentResultTests
{
    [UnitTest]
    public async Task ExecuteAsync_WritesExactBytesAndDisposesPooledBuffer()
    {
        var tile = PooledTileData.Rent(5);
        new byte[] { 1, 2, 3, 4, 5 }.CopyTo(tile.WritableMemory.Span);
        var result = new PooledTileContentResult(tile, "application/vnd.mapbox-vector-tile");
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();

        await result.ExecuteAsync(context);

        Assert.Equal("application/vnd.mapbox-vector-tile", context.Response.ContentType);
        Assert.Equal(5, context.Response.ContentLength);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5 }, ((MemoryStream)context.Response.Body).ToArray());
        Assert.Throws<ObjectDisposedException>(() => _ = tile.WritableMemory);
    }

    [UnitTest]
    public async Task ExecuteAsync_HeadRequest_OmitsBodyAndDisposesPooledBuffer()
    {
        var tile = PooledTileData.Rent(4);
        new byte[] { 9, 8, 7, 6 }.CopyTo(tile.WritableMemory.Span);
        var result = new PooledTileContentResult(tile, "application/vnd.mapbox-vector-tile");
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Head;
        context.Response.Body = new MemoryStream();

        await result.ExecuteAsync(context);

        Assert.Equal(4, context.Response.ContentLength);
        Assert.Empty(((MemoryStream)context.Response.Body).ToArray());
        Assert.Throws<ObjectDisposedException>(() => _ = tile.WritableMemory);
    }

    [UnitTest]
    public async Task SharedResponseETag_NotModified_Returns304AndDisposesPooledBuffer()
    {
        using var services = new ServiceCollection()
            .AddSingleton<IETagService>(new ETagService())
            .BuildServiceProvider();

        var firstTile = PooledTileData.Rent(3);
        new byte[] { 10, 20, 30 }.CopyTo(firstTile.WritableMemory.Span);
        var firstContext = new DefaultHttpContext { RequestServices = services };
        firstContext.Request.Method = HttpMethods.Get;
        firstContext.Response.Body = new MemoryStream();
        var wrappedFirst = (IResult)(await SharedResponseETag.ApplyAsync(
            firstContext, new PooledTileContentResult(firstTile, "application/vnd.mapbox-vector-tile")))!;
        await wrappedFirst.ExecuteAsync(firstContext);
        var etag = firstContext.Response.Headers.ETag.ToString();
        Assert.False(string.IsNullOrEmpty(etag));

        var secondTile = PooledTileData.Rent(3);
        new byte[] { 10, 20, 30 }.CopyTo(secondTile.WritableMemory.Span);
        var secondContext = new DefaultHttpContext { RequestServices = services };
        secondContext.Request.Method = HttpMethods.Get;
        secondContext.Request.Headers.IfNoneMatch = etag;
        secondContext.Response.Body = new MemoryStream();
        var wrappedSecond = (IResult)(await SharedResponseETag.ApplyAsync(
            secondContext, new PooledTileContentResult(secondTile, "application/vnd.mapbox-vector-tile")))!;
        await wrappedSecond.ExecuteAsync(secondContext);

        Assert.Equal(StatusCodes.Status304NotModified, secondContext.Response.StatusCode);
        Assert.Empty(((MemoryStream)secondContext.Response.Body).ToArray());
        Assert.Throws<ObjectDisposedException>(() => _ = secondTile.WritableMemory);
    }
}
