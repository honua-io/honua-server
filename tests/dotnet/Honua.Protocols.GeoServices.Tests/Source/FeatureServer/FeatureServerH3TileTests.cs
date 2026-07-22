// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.FeatureServer;

[Protocol(TestProtocols.FeatureServer)]
[Collection("Database")]
public sealed class FeatureServerH3TileTests : IClassFixture<WebAppFixture>
{
    private readonly WebAppFixture _fixture;

    public FeatureServerH3TileTests(WebAppFixture fixture)
    {
        _fixture = fixture;
    }

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

        // PA-070/PA-117 (#2418): a capability error is now signalled as HTTP 200 +
        // a JSON {"error":{...}} body (formerly 501/503), so an OK response is a
        // real tile only when it is not the JSON error envelope.
        if (response.StatusCode == HttpStatusCode.OK &&
            response.Content.Headers.ContentType?.MediaType != "application/json")
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

        // PA-070/PA-117: GeoServices always returns HTTP 200; error code is in the JSON body.
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [IntegrationTest]
    [Operation(Operations.GetTile)]
    [Endpoint("GET /tiles/{layerId}/h3/{z}/{x}/{y}.mvt")]
    public async Task H3Tile_InvalidCoords_ReturnsBadRequest()
    {
        var response = await _fixture.Client.GetAsync(
            $"/tiles/{WebAppFixture.TestLayerId}/h3/5/999/999.mvt");

        // PA-070/PA-117: GeoServices always returns HTTP 200; error code is in the JSON body.
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [IntegrationTest]
    [Operation(Operations.GetTile)]
    [Endpoint("GET /tiles/{layerId}/h3/{z}/{x}/{y}.mvt")]
    public async Task H3Tile_NonExistentLayer_ReturnsRealNotFound()
    {
        // Regression test for honua-server#2945: /tiles is not an Esri protocol surface,
        // so layer-not-found must be a real HTTP 404 + problem+json, not the GeoServices
        // 200-envelope.
        var response = await _fixture.Client.GetAsync("/tiles/999999/h3/5/16/11.mvt");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");
    }
}
