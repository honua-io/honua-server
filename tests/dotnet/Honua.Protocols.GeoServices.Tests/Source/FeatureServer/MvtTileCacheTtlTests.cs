// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using FluentAssertions;
using Honua.Core.Features.Tiles;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.FeatureServer;

/// <summary>
/// Serve-path cache-TTL integration coverage for the MVT tile endpoint.
/// Asserts the <c>Cache-Control: public, max-age=&lt;resolved&gt;</c> header is
/// driven by <see cref="TilesetTtlResolver" /> — the global
/// <see cref="TileOptions.CacheMaxAge" /> by default, and a per-tileset override
/// when one is configured for the requested tileset identity.
/// </summary>
[Protocol(TestProtocols.FeatureServer)]
[Operation(Operations.GetTile)]
[Collection("Database")]
public class MvtTileCacheTtlTests : IAsyncLifetime
{
    private const int TestLayerId = 0;

    // The standard fixture authenticates every request through the development
    // bypass. Disable it so requests without credentials exercise public caching.
    private readonly WebAppFixture _fixture = new WebAppFixture()
        .ConfigureWebHost(builder => builder.UseSetting("HONUA_DEV_AUTH", "false"));

    public Task InitializeAsync() => _fixture.InitializeAsync();

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Endpoint("GET /tiles/{layerId}/{z}/{x}/{y}.mvt")]
    public async Task GetTile_NoPerTilesetOverride_UsesGlobalCacheMaxAge()
    {
        // Default TileOptions.CacheMaxAge is 3600 (no per-tileset override configured).
        using var client = _fixture.CreateClient();
        var response = await client.GetAsync($"/tiles/{TestLayerId}/1/0/0.mvt");

        response.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.NoContent);
        response.Headers.CacheControl.Should().NotBeNull();
        response.Headers.CacheControl!.Public.Should().BeTrue();
        response.Headers.CacheControl.MaxAge.Should().Be(TimeSpan.FromSeconds(3600));
    }

    [IntegrationTest]
    [Endpoint("GET /tiles/{layerId}/{z}/{x}/{y}.mvt")]
    public async Task GetTile_AuthenticatedRequest_UsesPrivateCacheWithGlobalTtl()
    {
        using var client = _fixture.CreateAdminClient();
        var response = await client.GetAsync($"/tiles/{TestLayerId}/1/0/0.mvt");

        response.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.NoContent);
        response.Headers.CacheControl.Should().NotBeNull();
        response.Headers.CacheControl!.Private.Should().BeTrue();
        response.Headers.CacheControl.MaxAge.Should().Be(TimeSpan.FromSeconds(3600));
        response.Headers.Vary.Should().Contain("Authorization").And.Contain("X-API-Key");
    }
}

/// <summary>
/// Mirror of <see cref="MvtTileCacheTtlTests" /> running against a fixture whose
/// <see cref="TileOptions" /> pins a per-tileset TTL override for the FeatureServer
/// tileset identity under test. Verifies the override wins over the global default.
/// </summary>
[Protocol(TestProtocols.FeatureServer)]
[Operation(Operations.GetTile)]
[Collection("Database")]
public class MvtTileCacheTtlOverrideTests
    : IClassFixture<MvtTileCacheTtlOverrideTests.Fixture>
{
    private const int TestLayerId = 0;
    private const int OverrideTtlSeconds = 90;

    public sealed class Fixture : IAsyncLifetime
    {
        private static readonly TileOptions TileOptionsValue = BuildOptions();

        private static TileOptions BuildOptions()
        {
            // Identity must match what the GeoServices /tiles serve path passes to
            // the resolver: serviceId = "FeatureServer", layerId = route layer id,
            // tileMatrixSetId = the default WebMercatorQuad.
            var key = TilesetTtlResolver.BuildKey(
                "FeatureServer",
                TestLayerId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "WebMercatorQuad");

            return new TileOptions
            {
                CacheMaxAge = 3600,
                TilesetLifecycle = new Dictionary<string, TilesetCacheLifecycle>
                {
                    [key] = new TilesetCacheLifecycle { TtlSeconds = OverrideTtlSeconds }
                }
            };
        }

        public WebAppFixture App { get; } =
            new WebAppFixture()
                .ConfigureWebHost(builder => builder.UseSetting("HONUA_DEV_AUTH", "false"))
                .ReplaceService<IOptions<TileOptions>>(Options.Create(TileOptionsValue));

        public Task InitializeAsync() => App.InitializeAsync();

        public Task DisposeAsync() => App.DisposeAsync();
    }

    private readonly WebAppFixture _fixture;

    public MvtTileCacheTtlOverrideTests(Fixture fixture) => _fixture = fixture.App;

    [IntegrationTest]
    [Endpoint("GET /tiles/{layerId}/{z}/{x}/{y}.mvt")]
    public async Task GetTile_WithPerTilesetOverride_UsesOverrideTtl()
    {
        using var client = _fixture.CreateClient();
        var response = await client.GetAsync($"/tiles/{TestLayerId}/1/0/0.mvt");

        response.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.NoContent);
        response.Headers.CacheControl.Should().NotBeNull();
        response.Headers.CacheControl!.Public.Should().BeTrue();
        response.Headers.CacheControl.MaxAge.Should().Be(TimeSpan.FromSeconds(OverrideTtlSeconds));
    }
}
