// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using FluentAssertions;
using Honua.Protocols.Ogc.Common;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Api.Tiles;

[Protocol(TestProtocols.OgcApiTiles)]
[Collection("Database.OgcApiTiles")]
public sealed class OgcTilesRasterTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new();

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.GetTile)]
    [Endpoint("GET /ogc/tiles/tiles/{tileMatrixSetId}/{tileMatrix}/{tileRow}/{tileCol}")]
    public async Task GetTile_WithPngFormat_ReturnsRasterImage()
    {
        var response = await _fixture.Client.GetAsync(
            "/ogc/tiles/tiles/WebMercatorQuad/0/0/0?collections=0&f=png");

        response.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.NoContent);

        if (response.StatusCode == HttpStatusCode.OK)
        {
            response.Content.Headers.ContentType?.MediaType.Should().Be(MediaTypes.Png);
            var content = await response.Content.ReadAsByteArrayAsync();
            content.Length.Should().BeGreaterThan(0);
        }
    }

    [IntegrationTest]
    [Operation(Operations.GetTile)]
    [Endpoint("GET /ogc/tiles/collections/{collectionId}/tiles/{tileMatrixSetId}/{tileMatrix}/{tileRow}/{tileCol}")]
    public async Task GetCollectionTile_WithPngFormat_ReturnsRasterImage()
    {
        var response = await _fixture.Client.GetAsync(
            "/ogc/tiles/collections/0/tiles/WebMercatorQuad/0/0/0?f=png");

        response.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.NoContent);

        if (response.StatusCode == HttpStatusCode.OK)
        {
            response.Content.Headers.ContentType?.MediaType.Should().Be(MediaTypes.Png);
            var content = await response.Content.ReadAsByteArrayAsync();
            content.Length.Should().BeGreaterThan(0);
        }
    }

    [IntegrationTest]
    [Operation(Operations.GetTile)]
    [Endpoint("GET /ogc/tiles/collections/{collectionId}/tiles/{tileMatrixSetId}/{tileMatrix}/{tileRow}/{tileCol}")]
    public async Task GetCollectionTile_WithAcceptPng_ReturnsRasterImage()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get,
            "/ogc/tiles/collections/0/tiles/WebMercatorQuad/0/0/0");
        request.Headers.Accept.Clear();
        request.Headers.Accept.ParseAdd("image/png");

        var response = await _fixture.Client.SendAsync(request);

        response.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.NoContent);

        if (response.StatusCode == HttpStatusCode.OK)
        {
            response.Content.Headers.ContentType?.MediaType.Should().Be(MediaTypes.Png);
        }
    }

    [IntegrationTest]
    [Operation(Operations.GetTile)]
    [Endpoint("GET /ogc/tiles/collections/{collectionId}/tiles/{tileMatrixSetId}/{tileMatrix}/{tileRow}/{tileCol}")]
    public async Task GetCollectionTile_WithPngPreferredOverVector_ReturnsRasterImage()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get,
            "/ogc/tiles/collections/0/tiles/WebMercatorQuad/0/0/0");
        request.Headers.Accept.Clear();
        request.Headers.Accept.ParseAdd("image/png;q=1.0, application/vnd.mapbox-vector-tile;q=0.1");

        var response = await _fixture.Client.SendAsync(request);

        response.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.NoContent);

        if (response.StatusCode == HttpStatusCode.OK)
        {
            response.Content.Headers.ContentType?.MediaType.Should().Be(MediaTypes.Png);
        }
    }

    [IntegrationTest]
    [Operation(Operations.GetTile)]
    [Endpoint("GET /ogc/tiles/collections/{collectionId}/tiles/{tileMatrixSetId}/{tileMatrix}/{tileRow}/{tileCol}")]
    public async Task GetCollectionTile_AfterCachedPngRequest_InvalidAcceptStillReturnsNotAcceptable()
    {
        using var pngRequest = new HttpRequestMessage(HttpMethod.Get,
            "/ogc/tiles/collections/0/tiles/WebMercatorQuad/0/0/0");
        pngRequest.Headers.Accept.Clear();
        pngRequest.Headers.Accept.ParseAdd(MediaTypes.Png);

        var pngResponse = await _fixture.Client.SendAsync(pngRequest);
        pngResponse.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.NoContent);

        using var invalidAcceptRequest = new HttpRequestMessage(HttpMethod.Get,
            "/ogc/tiles/collections/0/tiles/WebMercatorQuad/0/0/0");
        invalidAcceptRequest.Headers.Accept.Clear();
        invalidAcceptRequest.Headers.Accept.ParseAdd("text/plain");

        var invalidAcceptResponse = await _fixture.Client.SendAsync(invalidAcceptRequest);
        invalidAcceptResponse.StatusCode.Should().Be(HttpStatusCode.NotAcceptable);
    }

    [IntegrationTest]
    [Operation(Operations.GetTile)]
    [Endpoint("GET /ogc/tiles/tiles/{tileMatrixSetId}/{tileMatrix}/{tileRow}/{tileCol}")]
    public async Task GetTile_WithPngFormat_WorldCRS84Quad_ReturnsRasterImage()
    {
        var response = await _fixture.Client.GetAsync(
            "/ogc/tiles/tiles/WorldCRS84Quad/0/0/0?collections=0&f=png");

        response.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.NoContent);

        if (response.StatusCode == HttpStatusCode.OK)
        {
            response.Content.Headers.ContentType?.MediaType.Should().Be(MediaTypes.Png);
        }
    }

    [IntegrationTest]
    [Operation(Operations.GetTile)]
    [Endpoint("GET /ogc/tiles/tiles/{tileMatrixSetId}/{tileMatrix}/{tileRow}/{tileCol}")]
    public async Task GetTile_WithUnsupportedFormat_ReturnsBadRequest()
    {
        var response = await _fixture.Client.GetAsync(
            "/ogc/tiles/tiles/WebMercatorQuad/0/0/0?collections=0&f=geojson");

        response.StatusCode.Should().Be(HttpStatusCode.NotAcceptable);
    }
}
