// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Classic.Wcs20;

[Collection("Database")]
public sealed class Wcs20AuthorizationProofTests : OgcClassicAuthorizationProofTestBase
{
    [IntegrationTest]
    [Protocol(TestProtocols.Wcs201)]
    [Operation(Operations.SecurityTesting)]
    [Endpoint("GET /rest/services/{id}/ImageServer/WCS")]
    [InterfaceOperation(TestProtocols.Wcs201, "DescribeCoverage")]
    public async Task Wcs_DescribeCoverage_WithoutReadRole_IsRefusedAndDescribesNothing()
    {
        var url =
            $"/rest/services/{WebAppFixture.TestLayerId}/ImageServer/WCS?SERVICE=WCS&REQUEST=DescribeCoverage" +
            "&VERSION=2.0.1&COVERAGEID=0";

        using var denied = await Outsider().GetAsync(url);
        var body = await AssertWcsRefusedAsync(denied);
        body.Should().NotContain("CoverageDescription").And.NotContain("RectifiedGrid");

        using var allowed = await Viewer().GetAsync(url);
        var description = await allowed.Content.ReadAsStringAsync();
        allowed.StatusCode.Should().Be(HttpStatusCode.OK, description);
        description.Should().Contain("CoverageDescription");
    }

    [IntegrationTest]
    [Protocol(TestProtocols.Wcs201)]
    [Operation(Operations.SecurityTesting)]
    [Endpoint("GET /rest/services/{id}/ImageServer/WCS")]
    [InterfaceOperation(TestProtocols.Wcs201, "GetCoverage")]
    public async Task Wcs_GetCoverage_WithoutReadRole_IsRefusedAndReturnsNoCoverageBytes()
    {
        // The seeded coverage is a 64x64 8BUI raster anchored at (-122.5, 37.84) with
        // a 0.00234375 x 0.0021875 degree cell, so it spans roughly
        // (-122.5 .. -122.35, 37.70 .. 37.84); the subset below is inside it.
        var url =
            $"/rest/services/{WebAppFixture.TestLayerId}/ImageServer/WCS?SERVICE=WCS&REQUEST=GetCoverage" +
            "&VERSION=2.0.1&COVERAGEID=0&FORMAT=image/png&SUBSET=Long(-122.45,-122.40)&SUBSET=Lat(37.75,37.80)";

        using var denied = await Outsider().GetAsync(url);
        await AssertWcsRefusedAsync(denied);

        using var allowed = await Viewer().GetAsync(url);
        await AssertImageAsync(allowed);
    }

    [IntegrationTest]
    [Protocol(TestProtocols.Wcs201)]
    [Operation(Operations.SecurityTesting)]
    [Endpoint("GET /ogc/wcs/{serviceId}")]
    [InterfaceOperation(TestProtocols.Wcs201, "GetCoverage")]
    public async Task Wcs_GetCoverage_DesktopPathWithoutReadRole_IsRefusedLikeTheServiceRoute()
    {
        // The desktop path (#4584) must enforce the service policy exactly as the
        // canonical service route does: same refusal, no coverage bytes, and the
        // same coverage for a principal that holds the read role.
        const string query =
            "?SERVICE=WCS&REQUEST=GetCoverage&VERSION=2.0.1&COVERAGEID=0&FORMAT=image/png" +
            "&SUBSET=Long(-122.45,-122.40)&SUBSET=Lat(37.75,37.80)";
        var desktopUrl = $"/ogc/wcs/{WebAppFixture.TestServiceId}{query}";
        var canonicalUrl = $"/ogc/services/{WebAppFixture.TestServiceId}/wcs{query}";

        using var canonicalDenied = await Outsider().GetAsync(canonicalUrl);
        using var denied = await Outsider().GetAsync(desktopUrl);
        var body = await AssertRefusedWithoutPayloadAsync(denied, HttpStatusCode.Forbidden);
        body.Should().Contain("ExceptionReport");
        denied.StatusCode.Should().Be(canonicalDenied.StatusCode);

        using var canonicalAllowed = await Viewer().GetAsync(canonicalUrl);
        using var allowed = await Viewer().GetAsync(desktopUrl);
        var image = await AssertImageAsync(allowed);
        image.Should().Equal(await AssertImageAsync(canonicalAllowed));
    }

    private static async Task<string> AssertWcsRefusedAsync(HttpResponseMessage response)
    {
        var body = await AssertRefusedWithoutPayloadAsync(response, HttpStatusCode.NotFound);
        body.Should().Contain("ExceptionReport").And.Contain("NoSuchCoverage");
        return body;
    }
}
