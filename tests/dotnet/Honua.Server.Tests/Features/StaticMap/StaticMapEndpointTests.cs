// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using FluentAssertions;
using Honua.Core.Features.Licensing.Abstractions;
using Honua.Core.Features.Licensing.Domain;
using Honua.TestKit.Helpers;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using SkiaSharp;

namespace Honua.Server.Tests.Features.StaticMap;

[Collection("Database")]
[Protocol(TestProtocols.StaticMap)]
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
        var fixture = new WebAppFixture()
            .ReplaceService<ILicenseEntitlementService>(new TestLicenseEntitlementService(HonuaEdition.Pro));
        await fixture.InitializeAsync();

        try
        {
            // Pro entitlement allows 150 DPI.
            var response = await fixture.Client.GetAsync(
                $"/static/{WebAppFixture.TestServiceId}/-122.4194,37.7749,12/400x300.png?dpi=150");

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            response.Content.Headers.ContentType?.MediaType.Should().Be("image/png");
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    // --- Dimension entitlement band (honua-server#2945): 1280 (Community max) is the
    // entitlement boundary, 4096 (Pro max) is the absolute hard cap. Everything strictly
    // between the two requires the "staticmap.large-dimensions" entitlement.

    [IntegrationTest]
    [Operation(Operations.Render)]
    [Endpoint("GET /static/{serviceId}/{center}/{dimensions}.{format}")]
    public async Task CenterZoom_CommunityEdition_DimensionAtCommunityMax_ReturnsImage()
    {
        // 1280x1280 is exactly the Community cap: allowed without any entitlement.
        var response = await _fixture.Client.GetAsync(
            $"/static/{WebAppFixture.TestServiceId}/-122.4194,37.7749,12/1280x1280.png");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("image/png");
    }

    [IntegrationTest]
    [Operation(Operations.Render)]
    [Endpoint("GET /static/{serviceId}/{center}/{dimensions}.{format}")]
    public async Task CenterZoom_CommunityEdition_DimensionAboveCommunityMax_ReturnsPaymentRequired()
    {
        // 2000 is within the Pro hard cap (4096) but above the Community entitlement
        // boundary (1280): blocked for a Community-tier caller.
        var response = await _fixture.Client.GetAsync(
            $"/static/{WebAppFixture.TestServiceId}/-122.4194,37.7749,12/2000x2000.png");

        response.StatusCode.Should().Be(HttpStatusCode.PaymentRequired);
    }

    [IntegrationTest]
    [Operation(Operations.Render)]
    [Endpoint("GET /static/{serviceId}/{center}/{dimensions}.{format}")]
    public async Task CenterZoom_ProEdition_DimensionWithinEntitlementBand_ReturnsImage()
    {
        var fixture = new WebAppFixture().WithTestLicense(HonuaEdition.Pro);
        await fixture.InitializeAsync();

        try
        {
            var response = await fixture.Client.GetAsync(
                $"/static/{WebAppFixture.TestServiceId}/-122.4194,37.7749,12/2000x2000.png");

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            response.Content.Headers.ContentType?.MediaType.Should().Be("image/png");
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Operation(Operations.Render)]
    [Endpoint("GET /static/{serviceId}/{center}/{dimensions}.{format}")]
    public async Task CenterZoom_ProEdition_ExceedsHardCap_ReturnsBadRequest()
    {
        // The 4096 hard cap applies regardless of entitlement tier.
        var fixture = new WebAppFixture().WithTestLicense(HonuaEdition.Pro);
        await fixture.InitializeAsync();

        try
        {
            var response = await fixture.Client.GetAsync(
                $"/static/{WebAppFixture.TestServiceId}/-122.4194,37.7749,12/5000x5000.png");

            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    // --- Overlay-count entitlement band: >10 markers or >20 path vertices require
    // "staticmap.rich-overlays"; >100 markers / >500 vertices are always rejected.

    [IntegrationTest]
    [Operation(Operations.Render)]
    [Endpoint("GET /static/{serviceId}/{center}/{dimensions}.{format}")]
    public async Task CenterZoom_CommunityEdition_MarkersWithinCommunityLimit_ReturnsImage()
    {
        var markers = BuildMarkers(10);
        var response = await _fixture.Client.GetAsync(
            $"/static/{WebAppFixture.TestServiceId}/-122.4194,37.7749,12/400x300.png?markers={markers}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [IntegrationTest]
    [Operation(Operations.Render)]
    [Endpoint("GET /static/{serviceId}/{center}/{dimensions}.{format}")]
    public async Task CenterZoom_CommunityEdition_MarkersAboveCommunityLimit_ReturnsPaymentRequired()
    {
        var markers = BuildMarkers(11);
        var response = await _fixture.Client.GetAsync(
            $"/static/{WebAppFixture.TestServiceId}/-122.4194,37.7749,12/400x300.png?markers={markers}");

        response.StatusCode.Should().Be(HttpStatusCode.PaymentRequired);
    }

    [IntegrationTest]
    [Operation(Operations.Render)]
    [Endpoint("GET /static/{serviceId}/{center}/{dimensions}.{format}")]
    public async Task CenterZoom_ProEdition_MarkersAboveCommunityLimit_ReturnsImage()
    {
        var fixture = new WebAppFixture().WithTestLicense(HonuaEdition.Pro);
        await fixture.InitializeAsync();

        try
        {
            var markers = BuildMarkers(50);
            var response = await fixture.Client.GetAsync(
                $"/static/{WebAppFixture.TestServiceId}/-122.4194,37.7749,12/400x300.png?markers={markers}");

            response.StatusCode.Should().Be(HttpStatusCode.OK);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Operation(Operations.Render)]
    [Endpoint("GET /static/{serviceId}/{center}/{dimensions}.{format}")]
    public async Task CenterZoom_ProEdition_MarkersExceedProHardCap_ReturnsBadRequest()
    {
        var fixture = new WebAppFixture().WithTestLicense(HonuaEdition.Pro);
        await fixture.InitializeAsync();

        try
        {
            var markers = BuildMarkers(101);
            var response = await fixture.Client.GetAsync(
                $"/static/{WebAppFixture.TestServiceId}/-122.4194,37.7749,12/400x300.png?markers={markers}");

            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Operation(Operations.Render)]
    [Endpoint("GET /static/{serviceId}/{center}/{dimensions}.{format}")]
    public async Task CenterZoom_CommunityEdition_PathWithinCommunityLimit_ReturnsImage()
    {
        var path = BuildPathVertices(20);
        var response = await _fixture.Client.GetAsync(
            $"/static/{WebAppFixture.TestServiceId}/-122.4194,37.7749,12/400x300.png?path={path}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [IntegrationTest]
    [Operation(Operations.Render)]
    [Endpoint("GET /static/{serviceId}/{center}/{dimensions}.{format}")]
    public async Task CenterZoom_CommunityEdition_PathAboveCommunityLimit_ReturnsPaymentRequired()
    {
        var path = BuildPathVertices(21);
        var response = await _fixture.Client.GetAsync(
            $"/static/{WebAppFixture.TestServiceId}/-122.4194,37.7749,12/400x300.png?path={path}");

        response.StatusCode.Should().Be(HttpStatusCode.PaymentRequired);
    }

    [IntegrationTest]
    [Operation(Operations.Render)]
    [Endpoint("GET /static/{serviceId}/{center}/{dimensions}.{format}")]
    public async Task CenterZoom_ProEdition_PathAboveCommunityLimit_ReturnsImage()
    {
        var fixture = new WebAppFixture().WithTestLicense(HonuaEdition.Pro);
        await fixture.InitializeAsync();

        try
        {
            var path = BuildPathVertices(100);
            var response = await fixture.Client.GetAsync(
                $"/static/{WebAppFixture.TestServiceId}/-122.4194,37.7749,12/400x300.png?path={path}");

            response.StatusCode.Should().Be(HttpStatusCode.OK);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    // --- DPI entitlement band + actual output-resolution scaling ---

    [IntegrationTest]
    [Operation(Operations.Render)]
    [Endpoint("GET /static/{serviceId}/{center}/{dimensions}.{format}")]
    public async Task CenterZoom_CommunityEdition_HighDpi_ReturnsPaymentRequired()
    {
        var response = await _fixture.Client.GetAsync(
            $"/static/{WebAppFixture.TestServiceId}/-122.4194,37.7749,12/400x300.png?dpi=150");

        response.StatusCode.Should().Be(HttpStatusCode.PaymentRequired);
    }

    [IntegrationTest]
    [Operation(Operations.Render)]
    [Endpoint("GET /static/{serviceId}/{center}/{dimensions}.{format}")]
    public async Task CenterZoom_ProEditionHighDpi_ScalesOutputResolution()
    {
        // honua-server#2945: prove the dpi parameter actually changes the rendered
        // pixel dimensions (dpiFactor = dpi/72), not just that the request succeeds.
        var fixture = new WebAppFixture().WithTestLicense(HonuaEdition.Pro);
        await fixture.InitializeAsync();

        try
        {
            const int width = 144;
            const int height = 96;
            const int dpi = 150;

            var baselineResponse = await fixture.Client.GetAsync(
                $"/static/{WebAppFixture.TestServiceId}/-122.4194,37.7749,12/{width}x{height}.png");
            var highDpiResponse = await fixture.Client.GetAsync(
                $"/static/{WebAppFixture.TestServiceId}/-122.4194,37.7749,12/{width}x{height}.png?dpi={dpi}");

            baselineResponse.StatusCode.Should().Be(HttpStatusCode.OK);
            highDpiResponse.StatusCode.Should().Be(HttpStatusCode.OK);

            using var baselineBitmap = SKBitmap.Decode(await baselineResponse.Content.ReadAsByteArrayAsync());
            using var highDpiBitmap = SKBitmap.Decode(await highDpiResponse.Content.ReadAsByteArrayAsync());

            baselineBitmap.Width.Should().Be(width);
            baselineBitmap.Height.Should().Be(height);

            var dpiFactor = dpi / 72.0;
            var expectedWidth = (int)Math.Round(width * dpiFactor);
            var expectedHeight = (int)Math.Round(height * dpiFactor);

            highDpiBitmap.Width.Should().Be(expectedWidth);
            highDpiBitmap.Height.Should().Be(expectedHeight);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    private static string BuildMarkers(int count)
        => string.Join('|', Enumerable.Range(0, count)
            .Select(i => $"{-122.4 + (i * 0.001):F4},{37.7 + (i * 0.001):F4}"));

    private static string BuildPathVertices(int count)
        => string.Join('|', Enumerable.Range(0, count)
            .Select(i => $"{-122.4 + (i * 0.001):F4},{37.7 + (i * 0.001):F4}"));
}
