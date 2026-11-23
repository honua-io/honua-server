// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Security.Domain;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Hosting;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.VectorTileServer;

// What a MapLibre client loads after the tiles (style, sprite, glyphs) and what ArcGIS clients load first (tilemap),
// requested anonymously through production authentication: the path a CDN sees.
[Collection("Database")]
[Protocol(TestProtocols.VectorTileServer)]
public sealed class VectorTileServerResourceSharedCacheTests : IAsyncLifetime
{
    private const string Service = "/rest/services/" + WebAppFixture.TestServiceId + "/VectorTileServer";
    private const string RootStyle = Service + "/resources/styles/root.json";
    private const string SpriteIndex = Service + "/resources/sprites/sprite.json";
    private const string SpriteImage = Service + "/resources/sprites/sprite@2x.png";
    private const string Glyphs = Service + "/resources/fonts/Open%20Sans%20Regular,Arial%20Unicode%20MS%20Regular/0-255.pbf";
    private const string TileMapRoot = Service + "/tilemap";
    private const string TileMapBlock = Service + "/tilemap/2/0/0/4/4";

    private readonly WebAppFixture _fixture = new WebAppFixture().ConfigureWebHost(builder =>
    {
        builder.UseSetting("HONUA_DEV_AUTH", "false");
        builder.UseSetting("HONUA_ADMIN_PASSWORD", WebAppFixture.SharedAdminPassword);
    });

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        AllowAnonymous(true);
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTheory]
    [Operation(Operations.GetTileMetadata)]
    [Endpoint("GET /rest/services/{serviceId}/VectorTileServer/resources/styles/{**resourcePath}")]
    [Endpoint("GET /rest/services/{serviceId}/VectorTileServer/resources/sprites/{spriteResource}")]
    [Endpoint("GET /rest/services/{serviceId}/VectorTileServer/resources/fonts/{fontstack}/{range}.pbf")]
    [Endpoint("GET /rest/services/{serviceId}/VectorTileServer/tilemap")]
    [Endpoint("GET /rest/services/{serviceId}/VectorTileServer/tilemap/{z:int}/{y:int}/{x:int}/{dimension:int}/{dimension2:int}")]
    [InlineData(RootStyle)]
    [InlineData(SpriteIndex)]
    [InlineData(SpriteImage)]
    [InlineData(Glyphs)]
    [InlineData(TileMapRoot)]
    [InlineData(TileMapBlock)]
    public async Task PublicService_AnonymousRequest_IsSharedWithACdnLifetime(string address)
    {
        using var client = _fixture.CreateClient();

        using var response = await client.GetAsync(address);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.CacheControl.Should().NotBeNull();
        response.Headers.CacheControl!.Public.Should().BeTrue();
        response.Headers.CacheControl.SharedMaxAge.Should().NotBeNull();
        response.Headers.Vary.Should().Contain("Origin");
    }

    [IntegrationTest]
    [Operation(Operations.GetTileMetadata)]
    [Endpoint("GET /rest/services/{serviceId}/VectorTileServer/resources/styles/{**resourcePath}")]
    public async Task SecuredService_AnonymousStyleRequest_IsRefusedAndNeverShared()
    {
        using var client = _fixture.CreateClient();
        AllowAnonymous(false);

        using var response = await client.GetAsync(RootStyle);

        (await ErrorCodeAsync(response)).Should().Be(499);
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
        response.Headers.CacheControl.Public.Should().BeFalse();
    }

    // Sprites, glyphs and the tilemap hold no layer data and answer without credentials by design
    // (docs/gis/vector-tile-server.md), so securing the service does not make them private.
    [IntegrationTheory]
    [Operation(Operations.GetTileMetadata)]
    [Endpoint("GET /rest/services/{serviceId}/VectorTileServer/resources/sprites/{spriteResource}")]
    [Endpoint("GET /rest/services/{serviceId}/VectorTileServer/resources/fonts/{fontstack}/{range}.pbf")]
    [Endpoint("GET /rest/services/{serviceId}/VectorTileServer/tilemap")]
    [InlineData(SpriteImage)]
    [InlineData(Glyphs)]
    [InlineData(TileMapRoot)]
    public async Task SecuredService_AnonymousLayerFreeResource_StaysShared(string address)
    {
        using var client = _fixture.CreateClient();
        AllowAnonymous(false);

        using var response = await client.GetAsync(address);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.CacheControl!.Public.Should().BeTrue();
    }

    [IntegrationTheory]
    [Operation(Operations.GetTileMetadata)]
    [Endpoint("GET /rest/services/{serviceId}/VectorTileServer/resources/sprites/{spriteResource}")]
    [Endpoint("GET /rest/services/{serviceId}/VectorTileServer/resources/fonts/{fontstack}/{range}.pbf")]
    [InlineData(Service + "/resources/sprites/sprite.svg")]
    [InlineData(Service + "/resources/sprites/..%2F..%2Fappsettings.json")]
    [InlineData(Service + "/resources/fonts/Wingdings/0-255.pbf")]
    [InlineData(Service + "/resources/fonts/..%2F..%2Fappsettings/0-255.pbf")]
    [InlineData(Service + "/resources/fonts/Arial%20Regular/65280-65535.pbf")]
    [InlineData(Service + "/resources/fonts/Arial%20Regular/0-65535.pbf")]
    public async Task PublicService_UnservedSpriteOrGlyphRange_IsNotFoundAndNeverShared(string address)
    {
        using var client = _fixture.CreateClient();

        using var response = await client.GetAsync(address);

        (await ErrorCodeAsync(response)).Should().Be(404);
        response.Headers.CacheControl!.Public.Should().BeFalse();
    }

    private static async Task<int> ErrorCodeAsync(HttpResponseMessage response)
    {
        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, content);
        using var document = JsonDocument.Parse(content);
        return document.RootElement.GetProperty("error").GetProperty("code").GetInt32();
    }

    private void AllowAnonymous(bool allowed)
    {
        var policy = new AccessPolicy { AllowAnonymous = allowed };
        _fixture.UpdateV2ServiceMetadata(WebAppFixture.TestServiceId, accessPolicy: policy);
        _fixture.UpdateV2ResourceMetadata(WebAppFixture.TestLayerId, accessPolicy: policy);
    }
}
