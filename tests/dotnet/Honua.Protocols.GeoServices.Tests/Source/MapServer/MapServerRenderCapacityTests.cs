// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using FluentAssertions;
using Honua.Server.Features.Infrastructure.Rendering;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.MapServer;

[Collection("Database")]
[Protocol(TestProtocols.MapServer)]
public sealed class MapServerRenderCapacityTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new();

    public async Task InitializeAsync()
    {
        _fixture.ReplaceService(new RasterRenderCapacityLimiter(maxConcurrentRenders: 1, maxReservedBytes: 1));
        await _fixture.InitializeAsync();
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.Tile)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/tile/{z}/{y}/{x}")]
    public async Task Tile_WhenRasterCapacityUnavailable_ReturnsServiceUnavailable()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/tile/0/0/0");

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
    }

    [IntegrationTest]
    [Operation(Operations.Export)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/export")]
    public async Task Export_WhenRasterCapacityUnavailable_ReturnsServiceUnavailable()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/export?bbox=-180,-90,180,90&size=256,256&f=json");

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
    }

    [IntegrationTest]
    [Operation(Operations.Wms)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/WMS")]
    public async Task WmsGetMap_WhenRasterCapacityUnavailable_ReturnsServiceUnavailableXml()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/WMS?SERVICE=WMS&REQUEST=GetMap&VERSION=1.3.0&BBOX=-90,-180,90,180&WIDTH=256&HEIGHT=256&CRS=EPSG:4326&LAYERS={WebAppFixture.TestLayerId}&STYLES=&FORMAT=image/png");

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable, content);
        content.Should().Contain("NoApplicableCode");
        content.Should().Contain("Raster rendering capacity is currently exhausted");
    }
}
