// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Configuration;
using Honua.Protocols.GeoServices.MapServer.Models;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.MapServer;

[Protocol(TestProtocols.MapServer)]
[Collection("Database")]
public sealed class MapServerTileLimitsTests : IClassFixture<MapServerTileLimitsTests.Fixture>
{
    public sealed class Fixture : IAsyncLifetime
    {
        private static readonly LimitsOptions Limits = new()
        {
            Tiles = new TileLimits
            {
                MaxTileZoom = 24
            }
        };

        public WebAppFixture App { get; } =
            new WebAppFixture().ReplaceService<IOptions<LimitsOptions>>(Options.Create(Limits));

        public Task InitializeAsync() => App.InitializeAsync();

        public Task DisposeAsync() => App.DisposeAsync();
    }

    private readonly WebAppFixture _fixture;

    public MapServerTileLimitsTests(Fixture fixture) => _fixture = fixture.App;

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
