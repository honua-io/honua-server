// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Classic.Wcs20;

/// <summary>
/// Authorization proofs for the WCS operations that describe and return coverage
/// bytes (#4388): <c>DescribeCoverage</c> and <c>GetCoverage</c>. See
/// <see cref="OgcClassicAuthorizationProofTestBase"/> for the fixture and for the
/// shared denial floor these cases assert.
/// </summary>
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

    /// <summary>
    /// WCS refuses differently from WMS and WMTS, and this asserts what it actually
    /// does rather than what the other two do. The layer-scoped
    /// <c>/rest/services/{id:int}/ImageServer/WCS</c> route resolves the coverage with
    /// <c>failOnAccessDenied: false</c>, so a coverage the caller may not read is
    /// reported as <c>NoSuchCoverage</c> — withheld and absent are deliberately
    /// indistinguishable, the same posture the WFS read path takes. The security floor
    /// asserted below is identical either way: no description, no coverage bytes, no
    /// attribute data. Reporting these as AccessDenied instead is a separate,
    /// presentational question and is not settled here.
    /// </summary>
    private static async Task<string> AssertWcsRefusedAsync(HttpResponseMessage response)
    {
        var body = await AssertRefusedWithoutPayloadAsync(response, HttpStatusCode.NotFound);
        body.Should().Contain("ExceptionReport").And.Contain("NoSuchCoverage");
        return body;
    }
}
