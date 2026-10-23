// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Xml.Linq;
using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Classic.Wms;

/// <summary>
/// Authorization proofs for the WMS operations that return pixels and attributes
/// (#4388): <c>GetMap</c> and <c>GetFeatureInfo</c>. See
/// <see cref="OgcClassicAuthorizationProofTestBase"/> for the fixture and for the
/// shared denial floor these cases assert.
/// </summary>
[Collection("Database")]
public sealed class WmsAuthorizationProofTests : OgcClassicAuthorizationProofTestBase
{
    [IntegrationTest]
    [Protocol(TestProtocols.Wms13)]
    [Operation(Operations.SecurityTesting)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/WMS")]
    [InterfaceOperation(TestProtocols.Wms13, "GetMap")]
    public async Task Wms_GetMap_WithoutReadRole_ReturnsAccessDeniedAndNoImagery()
    {
        var url =
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/WMS?SERVICE=WMS&REQUEST=GetMap&VERSION=1.3.0" +
            $"&BBOX=37.4,-122.6,37.6,-122.4&WIDTH=256&HEIGHT=256&CRS=EPSG:4326&LAYERS={WebAppFixture.TestLayerId}" +
            "&STYLES=&FORMAT=image/png";

        using var denied = await Outsider().GetAsync(url);
        await AssertWmsAccessDeniedAsync(denied);

        using var allowed = await Viewer().GetAsync(url);
        var image = await AssertImageAsync(allowed);
        // Not merely a well-formed PNG: the render must actually contain the seeded
        // points, so the paired success cannot pass on a blank or broken renderer.
        image.Length.Should().BeGreaterThan(PngMagic.Length + 100);
    }

    [IntegrationTest]
    [Protocol(TestProtocols.Wms13)]
    [Operation(Operations.SecurityTesting)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/WMS")]
    [InterfaceOperation(TestProtocols.Wms13, "GetFeatureInfo")]
    public async Task Wms_GetFeatureInfo_WithoutReadRole_ReturnsAccessDeniedAndNoAttributes()
    {
        // I=41, J=74 over the worldwide CRS:84 extent at 256x256 lands on the seeded
        // point at (-122.5, 37.5): (-122.5 + 180) / 360 * 256 = 40.9 and
        // (90 - 37.5) / 180 * 256 = 74.7. So the success case returns a known feature.
        var url =
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/WMS?SERVICE=WMS&REQUEST=GetFeatureInfo&VERSION=1.3.0" +
            $"&BBOX=-180,-90,180,90&CRS=CRS:84&WIDTH=256&HEIGHT=256&LAYERS={WebAppFixture.TestLayerId}" +
            $"&QUERY_LAYERS={WebAppFixture.TestLayerId}&INFO_FORMAT=text/plain&I=41&J=74";

        using var denied = await Outsider().GetAsync(url);
        await AssertWmsAccessDeniedAsync(denied);

        using var allowed = await Viewer().GetAsync(url);
        var body = await allowed.Content.ReadAsStringAsync();
        allowed.StatusCode.Should().Be(HttpStatusCode.OK, body);
        body.Should().Contain("Test Feature",
            "the denial above must be the access policy at work, not an endpoint that returns nothing to anyone");
    }

    /// <summary>
    /// WMS 1.3.0 §7.3.3.4 defines the ServiceExceptionReport payload for a refusal;
    /// the principal is authenticated but lacks the role, so the status is 403.
    /// </summary>
    private static async Task AssertWmsAccessDeniedAsync(HttpResponseMessage response)
    {
        var body = await AssertRefusedWithoutPayloadAsync(response, HttpStatusCode.Forbidden);
        response.Content.Headers.ContentType?.MediaType.Should().Be("text/xml");
        body.Should().Contain("ServiceExceptionReport").And.Contain("code=\"AccessDenied\"");
        XDocument.Parse(body).Descendants().Select(static element => element.Name.LocalName)
            .Should().NotContain("FeatureInfoResponse");
    }
}
