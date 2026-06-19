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
public class MvtTileCacheTtlTests : IClassFixture<WebAppFixture>
{
    private const int TestLayerId = 0;

    private readonly WebAppFixture _fixture;

    public MvtTileCacheTtlTests(WebAppFixture fixture)
    {
        _fixture = fixture;
    }

    [IntegrationTest]
    [Endpoint("GET /tiles/{layerId}/{z}/{x}/{y}.mvt")]
    public async Task GetTile_NoPerTilesetOverride_UsesGlobalCacheMaxAge()
    {
        // Default TileOptions.CacheMaxAge is 3600 (no per-tileset override configured).
        var response = await _fixture.Client.GetAsync($"/tiles/{TestLayerId}/1/0/0.mvt");

        response.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.NoContent);
        response.Headers.CacheControl.Should().NotBeNull();
        response.Headers.CacheControl!.Public.Should().BeTrue();
        response.Headers.CacheControl.MaxAge.Should().Be(TimeSpan.FromSeconds(3600));
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
            new WebAppFixture().ReplaceService<IOptions<TileOptions>>(Options.Create(TileOptionsValue));

        public Task InitializeAsync() => App.InitializeAsync();

        public Task DisposeAsync() => App.DisposeAsync();
    }

    private readonly WebAppFixture _fixture;

    public MvtTileCacheTtlOverrideTests(Fixture fixture) => _fixture = fixture.App;

    [IntegrationTest]
    [Endpoint("GET /tiles/{layerId}/{z}/{x}/{y}.mvt")]
    public async Task GetTile_WithPerTilesetOverride_UsesOverrideTtl()
    {
        var response = await _fixture.Client.GetAsync($"/tiles/{TestLayerId}/1/0/0.mvt");

        response.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.NoContent);
        response.Headers.CacheControl.Should().NotBeNull();
        response.Headers.CacheControl!.Public.Should().BeTrue();
        response.Headers.CacheControl.MaxAge.Should().Be(TimeSpan.FromSeconds(OverrideTtlSeconds));
    }
}
