// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Core.Configuration;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Tiles;
using Honua.Infrastructure.Rendering;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Honua.Server.Tests.Features.Infrastructure.Rendering;

[Trait("Tier", "Fast")]
[Trait("Category", "Unit")]
public sealed class VectorTileSizeLimitTests
{
    [Theory]
    [InlineData("/ogc/tiles/collections/test/tiles/WebMercatorQuad/0/0/0", 413)]
    [InlineData("/rest/services/test/FeatureServer/0/tiles/0/0/0.pbf", 200)]
    public async Task ExecuteAsync_OverBudget_UsesProtocolErrorWithoutCacheHeaders(string path, int status)
    {
        using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services };
        context.Request.Path = path;
        context.Response.Body = new MemoryStream();
        var result = await ExecuteAsync(context, 5, 4);
        await result.ExecuteAsync(context);

        context.Response.StatusCode.Should().Be(status);
        context.Response.Headers.CacheControl.ToString().Should().NotContain("max-age");
        context.Response.Body.Position = 0;
        using var body = await JsonDocument.ParseAsync(context.Response.Body);
        if (path.StartsWith("/rest", StringComparison.Ordinal))
        {
            body.RootElement.GetProperty("error").GetProperty("code").GetInt32().Should().Be(413);
        }
        else
        {
            context.Response.ContentType.Should().StartWith("application/problem+json");
            body.RootElement.GetProperty("status").GetInt32().Should().Be(413);
        }
    }

    [Theory]
    [InlineData(0, 204)]
    [InlineData(3, 200)]
    [InlineData(4, 200)]
    public async Task ExecuteAsync_WithinBudget_PreservesTileResponse(int bytes, int status)
    {
        using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services };
        context.Response.Body = new MemoryStream();
        var result = await ExecuteAsync(context, bytes, 4);
        await result.ExecuteAsync(context);
        context.Response.StatusCode.Should().Be(status);
        context.Response.Body.Length.Should().Be(bytes);
    }

    private static Task<IResult> ExecuteAsync(HttpContext context, int bytes, long budget)
    {
        var provider = Substitute.For<ITileProvider>();
        provider.GetMvtTileAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<int>(),
            Arg.Any<FeatureQuery?>(), Arg.Any<TileOptions>(), Arg.Any<TileLimits>(),
            Arg.Any<GridGeometry?>(), Arg.Any<CancellationToken>()).Returns(new byte[bytes]);
        return VectorTileExecution.ExecuteAsync(context, provider, 1, 0, 0, 0, new FeatureQuery(),
            new TileOptions(), new TileLimits { MaxTileSize = budget }, CancellationToken.None);
    }
}
