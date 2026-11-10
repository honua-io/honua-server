// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using FluentAssertions;
using Honua.Core.Features.Tiles;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.FeatureServer;

// /tiles requires authentication on this fixture, so every response is private, no-store
// whatever lifetime is configured.
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
    public async Task GetTile_NoPerTilesetOverride_IsNeverShared()
    {
        using var client = _fixture.CreateClient();
        var response = await client.GetAsync($"/tiles/{TestLayerId}/1/0/0.mvt");

        response.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.NoContent);
        response.Headers.CacheControl.Should().NotBeNull();
        response.Headers.CacheControl!.Private.Should().BeTrue();
        response.Headers.CacheControl.NoStore.Should().BeTrue();
    }

    [IntegrationTest]
    [Endpoint("GET /tiles/{layerId}/{z}/{x}/{y}.mvt")]
    public async Task GetTile_AuthenticatedRequest_IsNeverShared()
    {
        using var client = _fixture.CreateAdminClient();
        var response = await client.GetAsync($"/tiles/{TestLayerId}/1/0/0.mvt");

        response.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.NoContent);
        response.Headers.CacheControl.Should().NotBeNull();
        response.Headers.CacheControl!.Private.Should().BeTrue();
        response.Headers.CacheControl.NoStore.Should().BeTrue();
    }
}

// A per-tileset lifetime override for the FeatureServer tileset must not make a credentialed
// tile storable.
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
    public async Task GetTile_WithPerTilesetOverride_IsStillNeverShared()
    {
        using var client = _fixture.CreateClient();
        var response = await client.GetAsync($"/tiles/{TestLayerId}/1/0/0.mvt");

        response.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.NoContent);
        response.Headers.CacheControl.Should().NotBeNull();
        response.Headers.CacheControl!.Private.Should().BeTrue();
        response.Headers.CacheControl.NoStore.Should().BeTrue();
    }
}
