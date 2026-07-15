// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using SkiaSharp;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Classic.Wms;

/// <summary>
/// Endpoint coverage for WMS GetLegendGraphic (#2855).
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.Wms13)]
public sealed class WmsGetLegendGraphicTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new();

    public async Task InitializeAsync() => await _fixture.InitializeAsync();

    public Task DisposeAsync() => _fixture.DisposeAsync();

    private static string LegendUrl(string query) =>
        $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/WMS?SERVICE=WMS&REQUEST=GetLegendGraphic&{query}";

    [IntegrationTest]
    [Operation(Operations.Wms)]
    [InterfaceOperation(TestProtocols.Wms13, "GetLegendGraphic")]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/WMS")]
    public async Task Wms_GetLegendGraphic_ReturnsPngLegend()
    {
        var response = await _fixture.Client.GetAsync(
            LegendUrl($"VERSION=1.3.0&LAYER={WebAppFixture.TestLayerId}&STYLE=default&FORMAT=image/png"));

        var content = await response.Content.ReadAsByteArrayAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, System.Text.Encoding.UTF8.GetString(content));
        response.Content.Headers.ContentType?.MediaType.Should().Be("image/png");

        using var bitmap = SKBitmap.Decode(content);
        bitmap.Should().NotBeNull();
        bitmap.Width.Should().BeGreaterThan(0);
        bitmap.Height.Should().BeGreaterThan(0);
    }

    [IntegrationTest]
    [Operation(Operations.Wms)]
    [InterfaceOperation(TestProtocols.Wms13, "GetLegendGraphic")]
    [Endpoint("GET /ogc/services/{serviceId}/wms")]
    public async Task Wms_GetLegendGraphic_OnOgcRoute_ReturnsPngLegend()
    {
        var response = await _fixture.Client.GetAsync(
            $"/ogc/services/{WebAppFixture.TestServiceId}/wms?SERVICE=WMS&REQUEST=GetLegendGraphic"
            + $"&VERSION=1.3.0&LAYER={WebAppFixture.TestLayerId}&FORMAT=image/png");

        var content = await response.Content.ReadAsByteArrayAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, System.Text.Encoding.UTF8.GetString(content));
        response.Content.Headers.ContentType?.MediaType.Should().Be("image/png");
    }

    [IntegrationTest]
    [Protocol(TestProtocols.Wms111)]
    [Operation(Operations.Wms)]
    [InterfaceOperation(TestProtocols.Wms111, "GetLegendGraphic")]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/WMS")]
    public async Task Wms111_GetLegendGraphic_ReturnsPngLegend()
    {
        var response = await _fixture.Client.GetAsync(
            LegendUrl($"VERSION=1.1.1&LAYER={WebAppFixture.TestLayerId}&FORMAT=image/png"));

        var content = await response.Content.ReadAsByteArrayAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, System.Text.Encoding.UTF8.GetString(content));
        response.Content.Headers.ContentType?.MediaType.Should().Be("image/png");
    }

    [IntegrationTest]
    [Operation(Operations.Wms)]
    [InterfaceOperation(TestProtocols.Wms13, "GetLegendGraphic")]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/WMS")]
    public async Task Wms_GetLegendGraphic_WidthAndHeightSizeTheSwatch()
    {
        var small = await _fixture.Client.GetAsync(
            LegendUrl($"VERSION=1.3.0&LAYER={WebAppFixture.TestLayerId}&FORMAT=image/png&WIDTH=20&HEIGHT=20"));
        var large = await _fixture.Client.GetAsync(
            LegendUrl($"VERSION=1.3.0&LAYER={WebAppFixture.TestLayerId}&FORMAT=image/png&WIDTH=64&HEIGHT=64"));

        small.StatusCode.Should().Be(HttpStatusCode.OK);
        large.StatusCode.Should().Be(HttpStatusCode.OK);

        using var smallBitmap = SKBitmap.Decode(await small.Content.ReadAsByteArrayAsync());
        using var largeBitmap = SKBitmap.Decode(await large.Content.ReadAsByteArrayAsync());

        largeBitmap.Height.Should().BeGreaterThan(smallBitmap.Height);
        largeBitmap.Width.Should().BeGreaterThan(smallBitmap.Width);
    }

    [IntegrationTest]
    [Operation(Operations.Wms)]
    [InterfaceOperation(TestProtocols.Wms13, "GetLegendGraphic")]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/WMS")]
    public async Task Wms_GetLegendGraphic_WithScale_ReturnsPngLegend()
    {
        var response = await _fixture.Client.GetAsync(
            LegendUrl($"VERSION=1.3.0&LAYER={WebAppFixture.TestLayerId}&FORMAT=image/png&SCALE=50000"));

        var content = await response.Content.ReadAsByteArrayAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, System.Text.Encoding.UTF8.GetString(content));
        response.Content.Headers.ContentType?.MediaType.Should().Be("image/png");
    }

    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/WMS")]
    public async Task Wms_GetLegendGraphic_MissingLayer_ReturnsServiceException()
    {
        var response = await _fixture.Client.GetAsync(LegendUrl("VERSION=1.3.0&FORMAT=image/png"));

        var content = await response.Content.ReadAsStringAsync();
        // WMS 1.3.0 section 7.3.3.4: a ServiceExceptionReport is returned with HTTP 200;
        // the failure is carried entirely by the XML body.
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        content.Should().Contain("ServiceExceptionReport");
        content.Should().Contain("MissingParameterValue");
    }

    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/WMS")]
    public async Task Wms_GetLegendGraphic_UnknownLayer_ReturnsLayerNotDefined()
    {
        var response = await _fixture.Client.GetAsync(
            LegendUrl("VERSION=1.3.0&LAYER=does-not-exist&FORMAT=image/png"));

        var content = await response.Content.ReadAsStringAsync();
        // WMS 1.3.0 section 7.3.3.4: a ServiceExceptionReport is returned with HTTP 200;
        // the failure is carried entirely by the XML body.
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        content.Should().Contain("ServiceExceptionReport");
        content.Should().Contain("LayerNotDefined");
    }

    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/WMS")]
    public async Task Wms_GetLegendGraphic_UnsupportedFormat_ReturnsInvalidFormat()
    {
        var response = await _fixture.Client.GetAsync(
            LegendUrl($"VERSION=1.3.0&LAYER={WebAppFixture.TestLayerId}&FORMAT=image/svg%2Bxml"));

        var content = await response.Content.ReadAsStringAsync();
        // WMS 1.3.0 section 7.3.3.4: a ServiceExceptionReport is returned with HTTP 200;
        // the failure is carried entirely by the XML body.
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        content.Should().Contain("ServiceExceptionReport");
        content.Should().Contain("InvalidFormat");
    }

    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/WMS")]
    public async Task Wms_GetLegendGraphic_UnknownStyle_ReturnsStyleNotDefined()
    {
        var response = await _fixture.Client.GetAsync(
            LegendUrl($"VERSION=1.3.0&LAYER={WebAppFixture.TestLayerId}&STYLE=nope&FORMAT=image/png"));

        var content = await response.Content.ReadAsStringAsync();
        // WMS 1.3.0 section 7.3.3.4: a ServiceExceptionReport is returned with HTTP 200;
        // the failure is carried entirely by the XML body.
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        content.Should().Contain("ServiceExceptionReport");
        content.Should().Contain("StyleNotDefined");
    }

    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/WMS")]
    public async Task Wms_GetLegendGraphic_InvalidScale_ReturnsServiceException()
    {
        var response = await _fixture.Client.GetAsync(
            LegendUrl($"VERSION=1.3.0&LAYER={WebAppFixture.TestLayerId}&FORMAT=image/png&SCALE=0"));

        var content = await response.Content.ReadAsStringAsync();
        // WMS 1.3.0 section 7.3.3.4: a ServiceExceptionReport is returned with HTTP 200;
        // the failure is carried entirely by the XML body.
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        content.Should().Contain("ServiceExceptionReport");
        content.Should().Contain("InvalidParameterValue");
    }

    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/WMS")]
    public async Task Wms_GetLegendGraphic_OversizedWidth_ReturnsInvalidDimensionValue()
    {
        var response = await _fixture.Client.GetAsync(
            LegendUrl($"VERSION=1.3.0&LAYER={WebAppFixture.TestLayerId}&FORMAT=image/png&WIDTH=9999"));

        var content = await response.Content.ReadAsStringAsync();
        // WMS 1.3.0 section 7.3.3.4: a ServiceExceptionReport is returned with HTTP 200;
        // the failure is carried entirely by the XML body.
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        content.Should().Contain("ServiceExceptionReport");
        content.Should().Contain("InvalidDimensionValue");
    }

    [IntegrationTest]
    [Operation(Operations.Wms)]
    [InterfaceOperation(TestProtocols.Wms13, "GetCapabilities")]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/WMS")]
    public async Task Wms_GetCapabilities_AdvertisesLegendUrlForLegendCapableLayer()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/WMS?SERVICE=WMS&REQUEST=GetCapabilities");

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, content);
        content.Should().Contain("<LegendURL>");
        content.Should().Contain("REQUEST=GetLegendGraphic");

        // The advertised URL must actually serve a legend — capability honesty (#2803).
        var start = content.IndexOf("REQUEST=GetLegendGraphic", StringComparison.Ordinal);
        start.Should().BeGreaterThan(0);
        var hrefStart = content.LastIndexOf("xlink:href=\"", start, StringComparison.Ordinal) + "xlink:href=\"".Length;
        var hrefEnd = content.IndexOf('"', hrefStart);
        var href = System.Net.WebUtility.HtmlDecode(content[hrefStart..hrefEnd]);

        var legendResponse = await _fixture.Client.GetAsync(href);
        legendResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        legendResponse.Content.Headers.ContentType?.MediaType.Should().Be("image/png");
    }
}
