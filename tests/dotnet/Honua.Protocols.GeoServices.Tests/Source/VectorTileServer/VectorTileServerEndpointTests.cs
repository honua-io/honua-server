// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Protocols.GeoServices.VectorTileServer.Models;
using Honua.TestKit;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.VectorTileServer;

/// <summary>
/// Integration tests for the GeoServices VectorTileServer service-metadata foundation
/// (honua-server#1777). The service is resolved by NAME against an EsriVectorTileLayer
/// publication seeded into the default Metadata v2 test graph. The tile / resources /
/// tileMap routes are stubbed (501) in the foundation and asserted here so the API-surface
/// coverage gate is satisfied before the parallel wave fills them in.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.VectorTileServer)]
public sealed class VectorTileServerEndpointTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new();

    public async Task InitializeAsync() => await _fixture.InitializeAsync();

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("GET /rest/services/{serviceId}/VectorTileServer")]
    public async Task VectorTileServer_Metadata_ReturnsServiceDescriptor()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/VectorTileServer?f=json");

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, content);

        var metadata = JsonSerializer.Deserialize(
            content, VectorTileServerJsonContext.Default.VectorTileServerMetadataResponse);

        metadata.Should().NotBeNull();
        metadata!.Name.Should().Be(WebAppFixture.TestServiceId);
        metadata.CurrentVersion.Should().BeGreaterThan(0);
        metadata.Capabilities.Should().Be("TilesOnly");
        metadata.Type.Should().Be("indexedVector");
        metadata.ExportTilesAllowed.Should().BeFalse();
        metadata.Tiles.Should().ContainSingle().Which.Should().Be("tile/{z}/{y}/{x}.pbf");
        metadata.DefaultStyles.Should().Be("resources/styles");
        metadata.TileMap.Should().Be("tilemap");

        metadata.TileInfo.Should().NotBeNull();
        metadata.TileInfo!.Rows.Should().Be(512);
        metadata.TileInfo.Cols.Should().Be(512);
        metadata.TileInfo.Format.Should().Be("pbf");
        metadata.TileInfo.Origin.Should().NotBeNull();
        metadata.TileInfo.SpatialReference.Should().NotBeNull();
        metadata.TileInfo.SpatialReference!.Wkid.Should().Be(102100);
        metadata.TileInfo.SpatialReference.LatestWkid.Should().Be(3857);
        metadata.TileInfo.Lods.Should().NotBeNullOrEmpty();
        metadata.TileInfo.Lods![0].Level.Should().Be(0);
        metadata.TileInfo.Lods[0].Scale.Should().BeApproximately(559082264.0287178, 1e-3);
        metadata.TileInfo.Lods[1].Scale.Should().BeApproximately(559082264.0287178 / 2.0, 1e-3);

        metadata.MinLod.Should().Be(0);
        metadata.MaxLod.Should().Be(metadata.TileInfo.Lods[^1].Level);
        metadata.FullExtent.Should().NotBeNull();
        metadata.InitialExtent.Should().NotBeNull();
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("POST /rest/services/{serviceId}/VectorTileServer")]
    public async Task VectorTileServer_Metadata_Post_ReturnsServiceDescriptor()
    {
        using var body = new StringContent("f=json", Encoding.UTF8, "application/x-www-form-urlencoded");
        var response = await _fixture.Client.PostAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/VectorTileServer", body);

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, content);

        var metadata = JsonSerializer.Deserialize(
            content, VectorTileServerJsonContext.Default.VectorTileServerMetadataResponse);
        metadata.Should().NotBeNull();
        metadata!.Name.Should().Be(WebAppFixture.TestServiceId);
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("GET /rest/services/{serviceId}/VectorTileServer")]
    public async Task VectorTileServer_Metadata_UnknownService_ReturnsNotFound()
    {
        var response = await _fixture.Client.GetAsync(
            "/rest/services/does-not-exist/VectorTileServer?f=json");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Operation(Operations.GetTile)]
    [Endpoint("GET /rest/services/{serviceId}/VectorTileServer/tile/{z}/{y}/{x}.pbf")]
    public async Task VectorTileServer_Tile_InRange_ReturnsMvtBytesWithCacheHeader()
    {
        // Zoom 1, tile (0,0) covers a quadrant of the world; the seeded "test" service
        // resolves its EsriVectorTileLayer publication -> storage layer 0 and renders MVT.
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/VectorTileServer/tile/1/0/0.pbf");

        // The seeded geometry may or may not intersect this specific tile, so accept either
        // rendered bytes or an empty (204) tile; both are valid pipeline outcomes.
        response.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.NoContent);

        // Cache-Control (max-age from TileOptions.CacheMaxAge) is set on both OK and 204.
        response.Headers.Should().ContainKey("Cache-Control");
        response.Headers.CacheControl!.MaxAge.Should().BeGreaterThan(TimeSpan.Zero);

        if (response.StatusCode == HttpStatusCode.OK)
        {
            response.Content.Headers.ContentType?.MediaType.Should().Be("application/vnd.mapbox-vector-tile");
            var bytes = await response.Content.ReadAsByteArrayAsync();
            bytes.Should().NotBeEmpty();
        }
    }

    [IntegrationTest]
    [Operation(Operations.GetTile)]
    [Endpoint("GET /rest/services/{serviceId}/VectorTileServer/tile/{z}/{y}/{x}.pbf")]
    public async Task VectorTileServer_Tile_EmptyTile_ReturnsNoContent()
    {
        // A high-zoom tile far from the seeded extent yields no features -> 204 No Content.
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/VectorTileServer/tile/15/0/0.pbf");

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        response.Headers.Should().ContainKey("Cache-Control");
    }

    [IntegrationTest]
    [Operation(Operations.GetTile)]
    [Endpoint("GET /rest/services/{serviceId}/VectorTileServer/tile/{z}/{y}/{x}.pbf")]
    public async Task VectorTileServer_Tile_BadCoordinates_ReturnsBadRequest()
    {
        // x/y out of range for the zoom level (z=1 -> max index 1) -> 400 Bad Request.
        var badCoordinates = new[]
        {
            $"/rest/services/{WebAppFixture.TestServiceId}/VectorTileServer/tile/1/2/0.pbf", // y >= 2^z
            $"/rest/services/{WebAppFixture.TestServiceId}/VectorTileServer/tile/1/0/2.pbf"  // x >= 2^z
        };

        foreach (var url in badCoordinates)
        {
            var response = await _fixture.Client.GetAsync(url);
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest, $"{url} is out of range");
        }
    }

    [IntegrationTest]
    [Operation(Operations.GetTile)]
    [Endpoint("GET /rest/services/{serviceId}/VectorTileServer/tile/{z}/{y}/{x}.pbf")]
    public async Task VectorTileServer_Tile_OutOfRangeZoom_ReturnsBadRequest()
    {
        // Zoom above LimitsOptions.Tiles.MaxTileZoom -> 400 Bad Request.
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/VectorTileServer/tile/30/0/0.pbf");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Operation(Operations.GetTile)]
    [Endpoint("GET /rest/services/{serviceId}/VectorTileServer/tile/{z}/{y}/{x}.pbf")]
    public async Task VectorTileServer_Tile_UnknownService_ReturnsNotFound()
    {
        var response = await _fixture.Client.GetAsync(
            "/rest/services/does-not-exist/VectorTileServer/tile/1/0/0.pbf");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Operation(Operations.GetTileMetadata)]
    [Endpoint("GET /rest/services/{serviceId}/VectorTileServer/resources/styles")]
    public async Task VectorTileServer_DefaultStyles_IsStubbedNotImplemented()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/VectorTileServer/resources/styles");

        response.StatusCode.Should().Be(HttpStatusCode.NotImplemented);
    }

    [IntegrationTest]
    [Operation(Operations.GetTileMetadata)]
    [Endpoint("GET /rest/services/{serviceId}/VectorTileServer/resources/styles/{**resourcePath}")]
    public async Task VectorTileServer_StyleResource_IsStubbedNotImplemented()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/VectorTileServer/resources/styles/sprites/sprite.json");

        response.StatusCode.Should().Be(HttpStatusCode.NotImplemented);
    }

    [IntegrationTest]
    [Operation(Operations.GetTileMetadata)]
    [Endpoint("GET /rest/services/{serviceId}/VectorTileServer/tilemap/{z}/{y}/{x}/{dimension}/{dimension2}")]
    public async Task VectorTileServer_TileMap_IsStubbedNotImplemented()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/VectorTileServer/tilemap/2/0/0/4/4");

        response.StatusCode.Should().Be(HttpStatusCode.NotImplemented);
    }
}
