// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Core.Configuration;
using Honua.Core.Exceptions;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Tiles;
using Honua.Infrastructure.Monitoring;
using Honua.Infrastructure.Rendering;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Honua.Server.Tests.Features.Infrastructure.Rendering;

[Trait("Tier", "Fast")]
[Trait("Category", "Unit")]
public sealed class VectorTileSizeLimitTests
{
    [Theory]
    [InlineData("/ogc/tiles/collections/test/tiles/WebMercatorQuad/0/0/0", 413, false)]
    [InlineData("/ogc/tiles/collections/test/tiles/WebMercatorQuad/0/0/0", 413, true)]
    [InlineData("/rest/services/test/FeatureServer/0/tiles/0/0/0.pbf", 200, false)]
    [InlineData("/rest/services/test/FeatureServer/0/tiles/0/0/0.pbf", 200, true)]
    public async Task ExecuteAsync_OverBudget_UsesProtocolErrorWithoutCacheHeaders(string path, int status, bool providerRejects)
    {
        using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services };
        context.Request.Path = path;
        context.Response.Body = new MemoryStream();
        var result = await ExecuteAsync(context, 5, 4, providerRejects);
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

    [Theory]
    [InlineData("OgcTilesTile", "/ogc/tiles/test", 413)]
    [InlineData("OgcTilesDatasetTile", "/ogc/tiles/test", 413)]
    [InlineData("MvtTile", "/rest/services/test/FeatureServer/0/tiles/test", 200)]
    [InlineData("H3MvtTile", "/rest/services/test/FeatureServer/0/h3/tiles/test", 200)]
    public async Task CachedTile_LoweredBudget_DoesNotReplayOversizedResponse(string policy, string path, int status)
    {
        var limits = new LimitsOptions { Tiles = new TileLimits { MaxTileSize = 5 } };
        var invocations = 0;
        using var host = await new HostBuilder().ConfigureWebHost(web => web.UseTestServer()
            .ConfigureServices(services =>
            {
                services.AddRouting();
                services.AddSingleton<IOptions<LimitsOptions>>(Options.Create(limits));
                ObservabilityServiceCollectionExtensions.ConfigureOutputCaching(services, new ConfigurationBuilder().Build());
            })
            .Configure(app =>
            {
                app.UseRouting();
                app.UseOutputCache();
                app.UseEndpoints(endpoints => endpoints.MapGet(path, async (HttpContext context) =>
                {
                    Interlocked.Increment(ref invocations);
                    return await ExecuteAsync(context, 5, limits.Tiles.MaxTileSize);
                }).CacheOutput(policy));
            })).StartAsync();
        using var client = host.GetTestClient();
        using var first = await client.GetAsync(path);
        (await first.Content.ReadAsByteArrayAsync()).Length.Should().Be(5);
        using var cached = await client.GetAsync(path);
        (await cached.Content.ReadAsByteArrayAsync()).Length.Should().Be(5);
        invocations.Should().Be(1, "the second response must actually be served from the output cache");

        limits.Tiles.MaxTileSize = 4;
        using var rejected = await client.GetAsync(path);
        ((int)rejected.StatusCode).Should().Be(status);
        using var body = JsonDocument.Parse(await rejected.Content.ReadAsStringAsync());
        var code = status == 413
            ? body.RootElement.GetProperty("status")
            : body.RootElement.GetProperty("error").GetProperty("code");
        code.GetInt32().Should().Be(413);
        invocations.Should().Be(2, "the smaller budget must invalidate the previous cache lookup");
    }

    private static Task<IResult> ExecuteAsync(HttpContext context, int bytes, long budget, bool providerRejects = false)
    {
        var provider = Substitute.For<ITileProvider>();
        provider.GetMvtTileAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<int>(),
            Arg.Any<FeatureQuery?>(), Arg.Any<TileOptions>(), Arg.Any<TileLimits>(),
            Arg.Any<GridGeometry?>(), Arg.Any<CancellationToken>()).Returns(_ => providerRejects
                ? Task.FromException<byte[]?>(new TileSizeLimitExceededException())
                : Task.FromResult<byte[]?>(new byte[bytes]));
        return VectorTileExecution.ExecuteAsync(context, provider, 1, 0, 0, 0, new FeatureQuery(),
            new TileOptions(), new TileLimits { MaxTileSize = budget }, CancellationToken.None);
    }
}
