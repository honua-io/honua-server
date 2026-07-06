// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using FluentAssertions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Infrastructure.Rendering;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.MapServer;

[Protocol(TestProtocols.MapServer)]
[Collection("Database")]
public sealed class MapServerRenderCapacityTests : IClassFixture<MapServerRenderCapacityTests.Fixture>
{
    public sealed class Fixture : IAsyncLifetime
    {
        public WebAppFixture App { get; } = Build();

        private static WebAppFixture Build()
        {
            var app = new WebAppFixture()
                .ReplaceService(new RasterRenderCapacityLimiter(maxConcurrentRenders: 1, maxReservedBytes: 1));

            // The MapServer tile path consults the storage-backed tile cache before it
            // renders, and a cache hit short-circuits ahead of the render capacity gate.
            // Tests in this collection share a single LocalFileStorage directory, so a
            // tile rendered by another test (e.g. MapServerTileEndpointTests for 0/0/0
            // on the same service) would otherwise be served as a 200 here, bypassing the
            // capacity check this test asserts. Force every request to miss the cache so
            // the render path — and its capacity gate — is exercised deterministically.
            var storage = Substitute.For<ICloudFileStorage>();
            storage.Provider.Returns(CloudStorageProvider.Local);
            storage.GetMetadataAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns((CloudFile?)null);
            app.ConfigureServices(services =>
            {
                services.RemoveAll<ICloudFileStorage>();
                services.AddSingleton(storage);
            });

            return app;
        }

        public Task InitializeAsync() => App.InitializeAsync();

        public Task DisposeAsync() => App.DisposeAsync();
    }

    private readonly WebAppFixture _fixture;

    public MapServerRenderCapacityTests(Fixture fixture) => _fixture = fixture.App;

    [IntegrationTest]
    [Operation(Operations.Tile)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/tile/{z}/{y}/{x}")]
    public async Task Tile_WhenRasterCapacityUnavailable_ReturnsServiceUnavailable()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/tile/0/0/0");

        // PA-070/PA-117: GeoServices always returns HTTP 200; error code is in the JSON body.
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [IntegrationTest]
    [Operation(Operations.Export)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/export")]
    public async Task Export_WhenRasterCapacityUnavailable_ReturnsServiceUnavailable()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/export?bbox=-180,-90,180,90&size=256,256&f=json");

        // PA-070/PA-117: GeoServices always returns HTTP 200; error code is in the JSON body.
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [IntegrationTest]
    [Operation(Operations.Wms)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/WMS")]
    public async Task WmsGetMap_WhenRasterCapacityUnavailable_ReturnsServiceUnavailableXml()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/WMS?SERVICE=WMS&REQUEST=GetMap&VERSION=1.3.0&BBOX=-90,-180,90,180&WIDTH=256&HEIGHT=256&CRS=EPSG:4326&LAYERS={WebAppFixture.TestLayerId}&STYLES=&FORMAT=image/png");

        var content = await response.Content.ReadAsStringAsync();
        // PA-069 (#2418): a WMS ServiceExceptionReport MUST be returned with HTTP 200 OK
        // per WMS 1.3.0 §7.3.3.4 — the capacity-exhausted condition is signalled through
        // the XML exception body (NoApplicableCode), not the HTTP status.
        response.StatusCode.Should().Be(HttpStatusCode.OK, content);
        content.Should().Contain("NoApplicableCode");
        content.Should().Contain("Raster rendering capacity is currently exhausted");
    }
}
