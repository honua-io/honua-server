// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.FeatureServer;

[Collection("Database")]
[Protocol(TestProtocols.FeatureServer)]
public sealed class FeatureServerH3TileTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new();

    public async Task InitializeAsync() => await _fixture.InitializeAsync();

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.GetTile)]
    [Endpoint("GET /tiles/{layerId}/h3/{z}/{x}/{y}.mvt")]
    public async Task H3Tile_ValidCoords_ReturnsOkOrCapabilityError()
    {
        var response = await _fixture.Client.GetAsync(
            $"/tiles/{WebAppFixture.TestLayerId}/h3/5/16/11.mvt");

        // Accept OK/NoContent (h3-pg available), 501 (missing), or 503 (transient)
        response.StatusCode.Should().BeOneOf(
            HttpStatusCode.OK, HttpStatusCode.NoContent, HttpStatusCode.NotImplemented, HttpStatusCode.ServiceUnavailable);

        if (response.StatusCode == HttpStatusCode.OK)
        {
            response.Content.Headers.ContentType?.MediaType.Should().Be("application/vnd.mapbox-vector-tile");
        }
    }

    [IntegrationTest]
    [Operation(Operations.GetTile)]
    [Endpoint("GET /tiles/{layerId}/h3/{z}/{x}/{y}.mvt")]
    public async Task H3Tile_WithExplicitResolution_ReturnsOkOrCapabilityError()
    {
        var response = await _fixture.Client.GetAsync(
            $"/tiles/{WebAppFixture.TestLayerId}/h3/5/16/11.mvt?resolution=5");

        response.StatusCode.Should().BeOneOf(
            HttpStatusCode.OK, HttpStatusCode.NoContent, HttpStatusCode.NotImplemented, HttpStatusCode.ServiceUnavailable);
    }

    [IntegrationTest]
    [Operation(Operations.GetTile)]
    [Endpoint("GET /tiles/{layerId}/h3/{z}/{x}/{y}.mvt")]
    public async Task H3Tile_InvalidResolution_ReturnsBadRequest()
    {
        var response = await _fixture.Client.GetAsync(
            $"/tiles/{WebAppFixture.TestLayerId}/h3/5/16/11.mvt?resolution=99");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Operation(Operations.GetTile)]
    [Endpoint("GET /tiles/{layerId}/h3/{z}/{x}/{y}.mvt")]
    public async Task H3Tile_InvalidCoords_ReturnsBadRequest()
    {
        var response = await _fixture.Client.GetAsync(
            $"/tiles/{WebAppFixture.TestLayerId}/h3/5/999/999.mvt");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
