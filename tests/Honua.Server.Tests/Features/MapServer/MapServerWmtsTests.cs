// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.MapServer;

[Collection("Database")]
[Protocol(Protocols.MapServer)]
public sealed class MapServerWmtsTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new();

    public async Task InitializeAsync() => await _fixture.InitializeAsync();

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.Wmts)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/WMTS")]
    public async Task Wmts_GetCapabilities_ReturnsXml()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/WMTS?SERVICE=WMTS&REQUEST=GetCapabilities");

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, content);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/xml");
        content.Should().Contain("<Capabilities");
        content.Should().Contain("WebMercatorQuad");
        content.Should().Contain("<TileMatrixSet>");
        content.Should().Contain("<ows:ServiceType>OGC WMTS</ows:ServiceType>");
    }

    [IntegrationTest]
    [Operation(Operations.Wmts)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/WMTS")]
    public async Task Wmts_GetCapabilities_DefaultRequest_ReturnsXml()
    {
        // When no REQUEST is specified, should default to GetCapabilities
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/WMTS");

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, content);
        content.Should().Contain("<Capabilities");
    }

    [IntegrationTest]
    [Operation(Operations.Wmts)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/WMTS")]
    public async Task Wmts_GetTile_ReturnsPngImage()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/WMTS?SERVICE=WMTS&REQUEST=GetTile&TILEMATRIXSET=WebMercatorQuad&TILEMATRIX=0&TILEROW=0&TILECOL=0");

        var content = await response.Content.ReadAsByteArrayAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, $"Response body: {System.Text.Encoding.UTF8.GetString(content)}");
        response.Content.Headers.ContentType?.MediaType.Should().Be("image/png");
        content.Length.Should().BeGreaterThan(0);
    }

    [IntegrationTest]
    [Operation(Operations.Wmts)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/WMTS")]
    public async Task Wmts_GetTile_InvalidTileMatrixSet_ReturnsBadRequest()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/WMTS?SERVICE=WMTS&REQUEST=GetTile&TILEMATRIXSET=InvalidSet&TILEMATRIX=0&TILEROW=0&TILECOL=0");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Operation(Operations.Wmts)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/WMTS")]
    public async Task Wmts_GetTile_MissingParams_ReturnsBadRequest()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/WMTS?SERVICE=WMTS&REQUEST=GetTile");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Operation(Operations.Wmts)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/WMTS")]
    public async Task Wmts_InvalidServiceId_ReturnsBadRequest()
    {
        var response = await _fixture.Client.GetAsync(
            "/rest/services/%20/MapServer/WMTS?SERVICE=WMTS&REQUEST=GetCapabilities");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Operation(Operations.Wmts)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/WMTS")]
    public async Task Wmts_InvalidService_ReturnsBadRequest()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/WMTS?SERVICE=WFS&REQUEST=GetCapabilities");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
