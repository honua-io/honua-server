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
public sealed class MapServerTileEndpointTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new();

    public async Task InitializeAsync() => await _fixture.InitializeAsync();

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.Tile)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/tile/{z}/{y}/{x}")]
    public async Task Tile_ValidCoordinates_ReturnsPngImage()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/tile/0/0/0");

        var content = await response.Content.ReadAsByteArrayAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, $"Response body: {System.Text.Encoding.UTF8.GetString(content)}");
        response.Content.Headers.ContentType?.MediaType.Should().Be("image/png");
        content.Length.Should().BeGreaterThan(0);
    }

    [IntegrationTest]
    [Operation(Operations.Tile)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/tile/{z}/{y}/{x}")]
    public async Task Tile_HighZoom_ReturnsPngImage()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/tile/5/10/15");

        var content = await response.Content.ReadAsByteArrayAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, $"Response body: {System.Text.Encoding.UTF8.GetString(content)}");
        response.Content.Headers.ContentType?.MediaType.Should().Be("image/png");
        content.Length.Should().BeGreaterThan(0);
    }

    [IntegrationTest]
    [Operation(Operations.Tile)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/tile/{z}/{y}/{x}")]
    public async Task Tile_InvalidCoordinates_ReturnsBadRequest()
    {
        // z=2 means max tile index is 3 (2^2 - 1), so x=10 is out of range
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/tile/2/10/10");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Operation(Operations.Tile)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/tile/{z}/{y}/{x}")]
    public async Task Tile_NegativeZoom_ReturnsBadRequest()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/tile/-1/0/0");

        response.StatusCode.Should().BeOneOf(HttpStatusCode.BadRequest, HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Operation(Operations.Tile)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/tile/{z}/{y}/{x}")]
    public async Task Tile_InvalidServiceId_ReturnsBadRequest()
    {
        var response = await _fixture.Client.GetAsync(
            "/rest/services/%20/MapServer/tile/0/0/0");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
