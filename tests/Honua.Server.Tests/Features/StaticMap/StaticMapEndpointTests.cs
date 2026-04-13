// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.StaticMap;

[Collection("Database")]
[Protocol(Protocols.StaticMap)]
public sealed class StaticMapEndpointTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new();

    public async Task InitializeAsync() => await _fixture.InitializeAsync();

    public Task DisposeAsync() => _fixture.DisposeAsync();

    // --- Center+Zoom endpoint ---

    [IntegrationTest]
    [Operation(Operations.Render)]
    [Endpoint("GET /static/{serviceId}/{center}/{dimensions}.{format}")]
    public async Task CenterZoom_Png_ReturnsImage()
    {
        var response = await _fixture.Client.GetAsync(
            $"/static/{WebAppFixture.TestServiceId}/-122.4194,37.7749,12/600x400.png");

        var content = await response.Content.ReadAsByteArrayAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, $"body length={content.Length}");
        response.Content.Headers.ContentType?.MediaType.Should().Be("image/png");
        content.Length.Should().BeGreaterThan(0);
    }

    [IntegrationTest]
    [Operation(Operations.Render)]
    [Endpoint("GET /static/{serviceId}/{center}/{dimensions}.{format}")]
    public async Task CenterZoom_Jpeg_ReturnsJpeg()
    {
        var response = await _fixture.Client.GetAsync(
            $"/static/{WebAppFixture.TestServiceId}/-122.4194,37.7749,12/600x400.jpeg");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("image/jpeg");
    }

    [IntegrationTest]
    [Operation(Operations.Render)]
    [Endpoint("GET /static/{serviceId}/{center}/{dimensions}.{format}")]
    public async Task CenterZoom_Webp_ReturnsWebp()
    {
        var response = await _fixture.Client.GetAsync(
            $"/static/{WebAppFixture.TestServiceId}/-122.4194,37.7749,12/600x400.webp");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("image/webp");
    }

    [IntegrationTest]
    [Operation(Operations.Render)]
    [Endpoint("GET /static/{serviceId}/{center}/{dimensions}.{format}")]
    public async Task CenterZoom_InvalidCenter_ReturnsBadRequest()
    {
        var response = await _fixture.Client.GetAsync(
            $"/static/{WebAppFixture.TestServiceId}/invalid/600x400.png");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Operation(Operations.Render)]
    [Endpoint("GET /static/{serviceId}/{center}/{dimensions}.{format}")]
    public async Task CenterZoom_InvalidDimensions_ReturnsBadRequest()
    {
        var response = await _fixture.Client.GetAsync(
            $"/static/{WebAppFixture.TestServiceId}/-122.4194,37.7749,12/badxsize.png");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // --- Bbox endpoint ---

    [IntegrationTest]
    [Operation(Operations.Render)]
    [Endpoint("GET /static/{serviceId}/bbox/{bbox}/{dimensions}.{format}")]
    public async Task Bbox_Png_ReturnsImage()
    {
        var response = await _fixture.Client.GetAsync(
            $"/static/{WebAppFixture.TestServiceId}/bbox/-122.5,37.7,-122.3,37.9/800x600.png");

        var content = await response.Content.ReadAsByteArrayAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, $"body length={content.Length}");
        response.Content.Headers.ContentType?.MediaType.Should().Be("image/png");
        content.Length.Should().BeGreaterThan(0);
    }

    [IntegrationTest]
    [Operation(Operations.Render)]
    [Endpoint("GET /static/{serviceId}/bbox/{bbox}/{dimensions}.{format}")]
    public async Task Bbox_DatelineCrossing_ReturnsImage()
    {
        var response = await _fixture.Client.GetAsync(
            $"/static/{WebAppFixture.TestServiceId}/bbox/170,-10,-170,10/800x600.png");

        var content = await response.Content.ReadAsByteArrayAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, $"body length={content.Length}");
        response.Content.Headers.ContentType?.MediaType.Should().Be("image/png");
        content.Length.Should().BeGreaterThan(0);
    }

    [IntegrationTest]
    [Operation(Operations.Render)]
    [Endpoint("GET /static/{serviceId}/bbox/{bbox}/{dimensions}.{format}")]
    public async Task Bbox_InvalidBbox_ReturnsBadRequest()
    {
        var response = await _fixture.Client.GetAsync(
            $"/static/{WebAppFixture.TestServiceId}/bbox/invalid/800x600.png");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Operation(Operations.Render)]
    [Endpoint("GET /static/{serviceId}/bbox/{bbox}/{dimensions}.{format}")]
    public async Task Bbox_InvertedLatitudeRange_ReturnsBadRequest()
    {
        // ymin > ymax
        var response = await _fixture.Client.GetAsync(
            $"/static/{WebAppFixture.TestServiceId}/bbox/-122.5,37.9,-122.3,37.7/800x600.png");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Operation(Operations.Render)]
    [Endpoint("GET /static/{serviceId}/bbox/{bbox}/{dimensions}.{format}")]
    public async Task Bbox_OutOfRangeCoordinates_ReturnsBadRequest()
    {
        // lon < -180
        var response = await _fixture.Client.GetAsync(
            $"/static/{WebAppFixture.TestServiceId}/bbox/-200,37.7,-100,37.9/800x600.png");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Operation(Operations.Render)]
    [Endpoint("GET /static/{serviceId}/bbox/{bbox}/{dimensions}.{format}")]
    public async Task Bbox_LatitudeOutOfRange_ReturnsBadRequest()
    {
        // lat > 90
        var response = await _fixture.Client.GetAsync(
            $"/static/{WebAppFixture.TestServiceId}/bbox/-122.5,85,-122.3,95/800x600.png");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // --- DPI validation ---

    [IntegrationTest]
    [Operation(Operations.Render)]
    [Endpoint("GET /static/{serviceId}/{center}/{dimensions}.{format}")]
    public async Task CenterZoom_WithDpi72_ReturnsImage()
    {
        var response = await _fixture.Client.GetAsync(
            $"/static/{WebAppFixture.TestServiceId}/-122.4194,37.7749,12/400x300.png?dpi=72");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("image/png");
    }

    [IntegrationTest]
    [Operation(Operations.Render)]
    [Endpoint("GET /static/{serviceId}/{center}/{dimensions}.{format}")]
    public async Task CenterZoom_UnsupportedDpiFallsToDefault_ReturnsImage()
    {
        // Unsupported DPI falls back to 72
        var response = await _fixture.Client.GetAsync(
            $"/static/{WebAppFixture.TestServiceId}/-122.4194,37.7749,12/400x300.png?dpi=99");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // --- ETag / Caching ---

    [IntegrationTest]
    [Operation(Operations.Render)]
    [Endpoint("GET /static/{serviceId}/{center}/{dimensions}.{format}")]
    public async Task CenterZoom_ReturnsETag()
    {
        var response = await _fixture.Client.GetAsync(
            $"/static/{WebAppFixture.TestServiceId}/-122.4194,37.7749,12/400x300.png");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.ETag.Should().NotBeNull();
        response.Headers.ETag!.Tag.Should().NotBeNullOrWhiteSpace();
    }

    [IntegrationTest]
    [Operation(Operations.Render)]
    [Endpoint("GET /static/{serviceId}/{center}/{dimensions}.{format}")]
    public async Task CenterZoom_IfNoneMatch_Returns304WhenUnmodified()
    {
        // First request to get ETag
        var firstResponse = await _fixture.Client.GetAsync(
            $"/static/{WebAppFixture.TestServiceId}/-122.4194,37.7749,12/400x300.png");
        firstResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var etag = firstResponse.Headers.ETag;
        etag.Should().NotBeNull();

        // Second request with If-None-Match
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"/static/{WebAppFixture.TestServiceId}/-122.4194,37.7749,12/400x300.png");
        request.Headers.IfNoneMatch.Add(etag!);

        var secondResponse = await _fixture.Client.SendAsync(request);
        secondResponse.StatusCode.Should().Be(HttpStatusCode.NotModified);
    }

    // --- Layers parameter ---

    [IntegrationTest]
    [Operation(Operations.Render)]
    [Endpoint("GET /static/{serviceId}/{center}/{dimensions}.{format}")]
    public async Task CenterZoom_WithLayers_ReturnsImage()
    {
        var response = await _fixture.Client.GetAsync(
            $"/static/{WebAppFixture.TestServiceId}/-122.4194,37.7749,12/400x300.png?layers=0");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("image/png");
    }

    [IntegrationTest]
    [Operation(Operations.Render)]
    [Endpoint("GET /static/{serviceId}/{center}/{dimensions}.{format}")]
    public async Task CenterZoom_WithInvalidRequestedLayer_ReturnsBadRequest()
    {
        var response = await _fixture.Client.GetAsync(
            $"/static/{WebAppFixture.TestServiceId}/-122.4194,37.7749,12/400x300.png?layers={WebAppFixture.TestLayerId},999999");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Operation(Operations.Render)]
    [Endpoint("GET /static/{serviceId}/{center}/{dimensions}.{format}")]
    public async Task CenterZoom_WithInvalidLayers_ReturnsBadRequest()
    {
        var response = await _fixture.Client.GetAsync(
            $"/static/{WebAppFixture.TestServiceId}/-122.4194,37.7749,12/400x300.png?layers=abc");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // --- Markers parameter ---

    [IntegrationTest]
    [Operation(Operations.Render)]
    [Endpoint("GET /static/{serviceId}/{center}/{dimensions}.{format}")]
    public async Task CenterZoom_WithMarkers_ReturnsImage()
    {
        var response = await _fixture.Client.GetAsync(
            $"/static/{WebAppFixture.TestServiceId}/-122.4194,37.7749,12/400x300.png?markers=-122.42,37.78,red|-122.40,37.77,blue");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("image/png");
    }

    // --- Path parameter ---

    [IntegrationTest]
    [Operation(Operations.Render)]
    [Endpoint("GET /static/{serviceId}/{center}/{dimensions}.{format}")]
    public async Task CenterZoom_WithPath_ReturnsImage()
    {
        var response = await _fixture.Client.GetAsync(
            $"/static/{WebAppFixture.TestServiceId}/-122.4194,37.7749,12/400x300.png?path=-122.42,37.78|-122.40,37.77|-122.41,37.76");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("image/png");
    }

    // --- Filter parameter ---

    [IntegrationTest]
    [Operation(Operations.Render)]
    [Endpoint("GET /static/{serviceId}/bbox/{bbox}/{dimensions}.{format}")]
    public async Task Bbox_WithFilter_ReturnsImage()
    {
        var response = await _fixture.Client.GetAsync(
            $"/static/{WebAppFixture.TestServiceId}/bbox/-122.5,37.7,-122.3,37.9/400x300.png?filter=1%3D1");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("image/png");
    }

    // --- Service validation ---

    [IntegrationTest]
    [Operation(Operations.Render)]
    [Endpoint("GET /static/{serviceId}/{center}/{dimensions}.{format}")]
    public async Task CenterZoom_InvalidService_ReturnsNotFound()
    {
        var response = await _fixture.Client.GetAsync(
            "/static/nonexistent/-122.4194,37.7749,12/400x300.png");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // --- Dimension limits (Community edition defaults) ---

    [IntegrationTest]
    [Operation(Operations.Render)]
    [Endpoint("GET /static/{serviceId}/{center}/{dimensions}.{format}")]
    public async Task CenterZoom_ExceedsProMaxDimension_ReturnsBadRequest()
    {
        // Pro limit is 4096x4096; 5000 exceeds it
        var response = await _fixture.Client.GetAsync(
            $"/static/{WebAppFixture.TestServiceId}/-122.4194,37.7749,12/5000x5000.png");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Operation(Operations.Render)]
    [Endpoint("GET /static/{serviceId}/{center}/{dimensions}.{format}")]
    public async Task CenterZoom_ProEditionAllowsHighDpi_ReturnsImage()
    {
        // Pro edition allows 150 DPI
        var response = await _fixture.Client.GetAsync(
            $"/static/{WebAppFixture.TestServiceId}/-122.4194,37.7749,12/400x300.png?dpi=150");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("image/png");
    }
}
