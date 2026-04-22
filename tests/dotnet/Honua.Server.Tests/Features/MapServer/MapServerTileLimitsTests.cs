// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Configuration;
using Honua.Server.Features.MapServer.Models;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Features.MapServer;

[Collection("Database")]
[Protocol(Protocols.MapServer)]
public sealed class MapServerTileLimitsTests : IAsyncLifetime
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
    [Operation(Operations.Tile)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/tile/{z}/{y}/{x}")]
    public async Task Tile_Zoom23_WithMaxTileZoom24_ReturnsPngImage()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/tile/23/0/0");

        var content = await response.Content.ReadAsByteArrayAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, $"Response body: {System.Text.Encoding.UTF8.GetString(content)}");
        response.Content.Headers.ContentType?.MediaType.Should().Be("image/png");
        content.Length.Should().BeGreaterThan(0);
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer")]
    public async Task ServiceMetadata_WithMaxTileZoom24_AdvertisesZoom24InTileInfo()
    {
        var response = await _fixture.Client.GetAsync($"/rest/services/{WebAppFixture.TestServiceId}/MapServer");
        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, content);
        var service = JsonSerializer.Deserialize(content, MapServerJsonContext.Default.MapServerResponse);

        service.Should().NotBeNull();
        service!.TileInfo.Should().NotBeNull();
        service.TileInfo!.Lods.Should().NotBeNullOrEmpty();
        service.TileInfo.Lods.Max(lod => lod.Level).Should().Be(24);
    }
}
