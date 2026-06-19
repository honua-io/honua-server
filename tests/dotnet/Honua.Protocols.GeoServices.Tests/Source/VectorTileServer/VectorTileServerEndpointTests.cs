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
    public async Task VectorTileServer_Tile_IsStubbedNotImplemented()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/VectorTileServer/tile/0/0/0.pbf");

        response.StatusCode.Should().Be(HttpStatusCode.NotImplemented);
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
