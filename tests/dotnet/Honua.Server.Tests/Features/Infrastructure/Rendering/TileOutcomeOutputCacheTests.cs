// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Configuration;
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

/// <summary>
/// Regression for honua-server#4918. ASP.NET Core's default output-cache policy stores 200
/// responses only, so the two deterministic non-200 tile outcomes — 204 for a tile the layer has
/// no features in, and 413 for a tile whose encoded MVT exceeds <c>Limits:Tiles:MaxTileSize</c> —
/// were recomputed on every request even though the tile endpoints all carry <c>CacheOutput</c>.
/// In the 2026.1 capacity soak that was the entire tile workload: 56% of requests answered 204 and
/// 44% answered 413 after a ~1.4 s PostGIS encode of a 1,492,055-byte tile that was then
/// discarded, so the advertised tile cache stored nothing and tiles_load blew the frozen p95/p99
/// capacity lock (run 34923808977).
/// </summary>
[Trait("Tier", "Fast")]
[Trait("Category", "Unit")]
public sealed class TileOutcomeOutputCacheTests
{
    private const string OgcTilePath = "/ogc/tiles/collections/0/tiles/WebMercatorQuad/0/0/0";
    private const string MvtTilePath = "/rest/services/test/FeatureServer/0/tiles/0/0/0.pbf";

    [Theory]
    [InlineData("OgcTilesTile", OgcTilePath)]
    [InlineData("OgcTilesDatasetTile", OgcTilePath)]
    [InlineData("MvtTile", MvtTilePath)]
    [InlineData("H3MvtTile", MvtTilePath)]
    public async Task EmptyTile_IsServedFromTheOutputCache(string policy, string path)
    {
        var state = new TileEndpointState { Bytes = 0, Budget = 512_000 };
        using var host = await StartHostAsync(policy, path, state);
        using var client = host.GetTestClient();

        using var first = await client.GetAsync(path);
        ((int)first.StatusCode).Should().Be(StatusCodes.Status204NoContent);
        using var second = await client.GetAsync(path);
        ((int)second.StatusCode).Should().Be(StatusCodes.Status204NoContent);

        state.Invocations.Should().Be(
            1,
            "an empty tile is a pure function of the cache key, so the second request must not re-run the encode");
    }

    // The GeoServices tile routes carry the refusal in an Esri error envelope over HTTP 200, so
    // only the OGC routes answer a 413 the default output-cache policy would refuse to store.
    [Theory]
    [InlineData("OgcTilesTile", OgcTilePath, StatusCodes.Status413PayloadTooLarge)]
    [InlineData("OgcTilesDatasetTile", OgcTilePath, StatusCodes.Status413PayloadTooLarge)]
    [InlineData("MvtTile", MvtTilePath, StatusCodes.Status200OK)]
    [InlineData("H3MvtTile", MvtTilePath, StatusCodes.Status200OK)]
    public async Task OverBudgetTile_IsServedFromTheOutputCache(string policy, string path, int transportStatus)
    {
        var state = new TileEndpointState { Bytes = 5, Budget = 4 };
        using var host = await StartHostAsync(policy, path, state);
        using var client = host.GetTestClient();

        using var first = await client.GetAsync(path);
        ((int)first.StatusCode).Should().Be(transportStatus);
        using var second = await client.GetAsync(path);
        ((int)second.StatusCode).Should().Be(transportStatus);
        await AssertPayloadTooLargeBodyAsync(second, path);

        state.Invocations.Should().Be(
            1,
            "an over-budget tile costs a full encode, so the refusal must be replayed from the cache instead of re-encoded");
    }

    [Fact]
    public async Task CachedRefusal_IsNotReplayedAfterTheBudgetIsRaised()
    {
        var state = new TileEndpointState { Bytes = 5, Budget = 4 };
        using var host = await StartHostAsync("OgcTilesTile", OgcTilePath, state);
        using var client = host.GetTestClient();

        using var refused = await client.GetAsync(OgcTilePath);
        ((int)refused.StatusCode).Should().Be(StatusCodes.Status413PayloadTooLarge);

        state.Limits.Tiles.MaxTileSize = 512_000;
        using var served = await client.GetAsync(OgcTilePath);
        ((int)served.StatusCode).Should().Be(StatusCodes.Status200OK);
        (await served.Content.ReadAsByteArrayAsync()).Length.Should().Be(5);
        state.Invocations.Should().Be(
            2,
            "the cache key carries the enforced byte budget, so a raised budget cannot replay the refusal");
    }

    [Fact]
    public async Task AuthenticatedRequest_DoesNotPopulateTheSharedTileCache()
    {
        var state = new TileEndpointState { Bytes = 0, Budget = 512_000 };
        using var host = await StartHostAsync("OgcTilesTile", OgcTilePath, state, authenticated: true);
        using var client = host.GetTestClient();

        using var first = await client.GetAsync(OgcTilePath);
        ((int)first.StatusCode).Should().Be(StatusCodes.Status204NoContent);
        using var second = await client.GetAsync(OgcTilePath);
        ((int)second.StatusCode).Should().Be(StatusCodes.Status204NoContent);

        state.Invocations.Should().Be(
            2,
            "caching non-200 tile outcomes must not widen the anonymous-only boundary the tile cache already enforces");
    }

    [Fact]
    public async Task EmptyTileForARequestCarryingAToken_IsNotStored()
    {
        var state = new TileEndpointState { Bytes = 0, Budget = 512_000 };
        using var host = await StartHostAsync("OgcTilesTile", OgcTilePath, state);
        using var client = host.GetTestClient();

        using var first = await client.GetAsync($"{OgcTilePath}?token=a");
        ((int)first.StatusCode).Should().Be(StatusCodes.Status204NoContent);
        using var second = await client.GetAsync($"{OgcTilePath}?token=a");
        ((int)second.StatusCode).Should().Be(StatusCodes.Status204NoContent);

        state.Invocations.Should().Be(
            2,
            "re-enabling storage for 204/413 outcomes must not store a response produced for a credentialed request");
    }

    [Fact]
    public async Task FailedTile_IsNotCached()
    {
        var state = new TileEndpointState { Bytes = 0, Budget = 512_000, Fail = true };
        using var host = await StartHostAsync("OgcTilesTile", OgcTilePath, state);
        using var client = host.GetTestClient();

        using var first = await client.GetAsync(OgcTilePath);
        ((int)first.StatusCode).Should().Be(StatusCodes.Status500InternalServerError);
        using var second = await client.GetAsync(OgcTilePath);
        ((int)second.StatusCode).Should().Be(StatusCodes.Status500InternalServerError);

        state.Invocations.Should().Be(
            2,
            "only the deterministic 204/413 tile outcomes are cacheable; a server error must be retried");
    }

    private static async Task AssertPayloadTooLargeBodyAsync(HttpResponseMessage response, string path)
    {
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var status = path.StartsWith("/rest", StringComparison.Ordinal)
            ? body.RootElement.GetProperty("error").GetProperty("code")
            : body.RootElement.GetProperty("status");
        status.GetInt32().Should().Be(StatusCodes.Status413PayloadTooLarge);
    }

    private static Task<IHost> StartHostAsync(
        string policy,
        string path,
        TileEndpointState state,
        bool authenticated = false)
        => new HostBuilder().ConfigureWebHost(web => web.UseTestServer()
            .ConfigureServices(services =>
            {
                services.AddRouting();
                services.AddSingleton<IOptions<LimitsOptions>>(Options.Create(state.Limits));
                ObservabilityServiceCollectionExtensions.ConfigureOutputCaching(services, new ConfigurationBuilder().Build());
            })
            .Configure(app =>
            {
                app.Use(async (context, next) =>
                {
                    if (authenticated)
                    {
                        context.User = new ClaimsPrincipal(new ClaimsIdentity("test"));
                    }

                    await next(context);
                });
                app.UseRouting();
                app.UseOutputCache();
                app.UseEndpoints(endpoints => endpoints.MapGet(path, async (HttpContext context) =>
                {
                    Interlocked.Increment(ref state.Invocations);
                    if (state.Fail)
                    {
                        context.Response.StatusCode = StatusCodes.Status500InternalServerError;
                        return;
                    }

                    var result = await ExecuteAsync(context, state.Bytes, state.Budget);
                    await result.ExecuteAsync(context);
                }).CacheOutput(policy));
            })).StartAsync();

    private static Task<IResult> ExecuteAsync(HttpContext context, int bytes, long budget)
    {
        var provider = Substitute.For<ITileProvider>();
        provider.GetMvtTileAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<int>(),
            Arg.Any<FeatureQuery?>(), Arg.Any<TileOptions>(), Arg.Any<TileLimits>(),
            Arg.Any<GridGeometry?>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<byte[]?>(new byte[bytes]));
        return VectorTileExecution.ExecuteAsync(context, provider, 1, 0, 0, 0, new FeatureQuery(),
            new TileOptions(), new TileLimits { MaxTileSize = budget }, CancellationToken.None);
    }

    private sealed class TileEndpointState
    {
        public int Invocations;

        /// <summary>
        /// The live options instance the output-cache policies read. Mutating
        /// <see cref="Budget"/> therefore moves subsequent requests to a different cache key,
        /// exactly as an operator's configuration change does at runtime.
        /// </summary>
        public LimitsOptions Limits { get; } = new() { Tiles = new TileLimits() };

        public int Bytes { get; init; }

        public long Budget
        {
            get => Limits.Tiles.MaxTileSize;
            init => Limits.Tiles.MaxTileSize = value;
        }

        public bool Fail { get; init; }
    }
}
