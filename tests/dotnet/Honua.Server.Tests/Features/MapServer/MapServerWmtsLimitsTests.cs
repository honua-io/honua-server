// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using FluentAssertions;
using Honua.Core.Configuration;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Features.MapServer;

[Collection("Database")]
[Protocol(Protocols.MapServer)]
public sealed class MapServerWmtsLimitsTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new();

    private readonly LimitsOptions _limits = new()
    {
        Tiles = new TileLimits
        {
            MaxTileZoom = 24
        }
    };

    public async Task InitializeAsync()
    {
        _fixture.ReplaceService<IOptions<LimitsOptions>>(Options.Create(_limits));
        await _fixture.InitializeAsync();
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.Wmts)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/WMTS")]
    public async Task Wmts_GetTile_Zoom23_WithMaxTileZoom24_ReturnsPng()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/WMTS?SERVICE=WMTS&REQUEST=GetTile&VERSION=1.0.0&LAYER={WebAppFixture.TestLayerId}&STYLE=default&FORMAT=image/png&TILEMATRIXSET=WebMercatorQuad&TILEMATRIX=23&TILEROW=0&TILECOL=0");

        var content = await response.Content.ReadAsByteArrayAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, $"Response body: {System.Text.Encoding.UTF8.GetString(content)}");
        response.Content.Headers.ContentType?.MediaType.Should().Be("image/png");
        content.Length.Should().BeGreaterThan(0);
    }

    [IntegrationTest]
    [Operation(Operations.Wmts)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/WMTS")]
    public async Task Wmts_GetCapabilities_WithMaxTileZoom24_AdvertisesTileMatrix24()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/WMTS?SERVICE=WMTS&REQUEST=GetCapabilities");

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, content);
        content.Should().Contain("<TileMatrix>24</TileMatrix>");
        content.Should().Contain("<ows:Identifier>24</ows:Identifier>");
    }
}
