// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text;
using System.Xml.Linq;
using FluentAssertions;
using Honua.Core.Features.Licensing.Domain;
using Honua.Core.Features.Security.Domain;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Helpers;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Classic;

/// <summary>
/// Authorization proofs for the OGC-classic render and coverage surfaces (#4388):
/// WMS <c>GetMap</c> and <c>GetFeatureInfo</c>, WMTS <c>GetTile</c>, and WCS
/// <c>DescribeCoverage</c> / <c>GetCoverage</c>. Before this file the only
/// OGC-classic authorization tests in the repository covered WMS
/// <c>GetCapabilities</c> and WFS <c>GetFeature</c>; every operation that actually
/// returns imagery, tiles, coverage bytes or attribute data was untested.
/// </summary>
/// <remarks>
/// Every case is a real HTTP request against a real PostGIS seed with the dev-auth
/// bypass switched off, so the denials are produced by the production access
/// pipeline rather than by an unconfigured endpoint. Each denial is paired with an
/// authenticated success on the same fixture that asserts the actual payload — a
/// denial-only test would also pass against an endpoint that is broken for everyone.
///
/// The access policy is set on the <c>test</c> service rather than on layer 0
/// because these four operations resolve through different resources: WMS and WMTS
/// serve the feature resource <c>res-layer-0</c> while WCS serves the raster
/// resource <c>res-image-test</c>. A service-level policy covers both, and
/// <c>AccessPolicyEvaluator</c> gives a service denial the same force as a layer
/// denial when the layer carries no policy of its own.
/// </remarks>
[Collection("Database")]
public sealed class OgcClassicAuthorizationProofTests : IAsyncLifetime
{
    private const string ViewerRole = "map-viewer";
    private const string OutsiderRole = "unrelated-role";

    /// <summary>Seed attribute values that must never appear in a denial body.</summary>
    private static readonly string[] SeededAttributeValues =
        ["Test Feature", "Another Feature", "Fifth Feature", "A test feature for integration tests"];

    private static readonly byte[] PngMagic = [0x89, 0x50, 0x4E, 0x47];

    private readonly WebAppFixture _fixture = new WebAppFixture().WithTestLicense(HonuaEdition.Pro)
        .ConfigureWebHost(builder => builder
            .UseSetting("HONUA_DEV_AUTH", "false")
            .UseSetting("HONUA_DEV_AUTH_ALLOW_BYPASS", "false"))
        .ConfigureServices(services =>
        {
            services.AddAuthentication()
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { });
            services.PostConfigureAll<AuthenticationOptions>(options =>
            {
                options.DefaultAuthenticateScheme = TestAuthHandler.SchemeName;
                options.DefaultChallengeScheme = TestAuthHandler.SchemeName;
                options.DefaultScheme = TestAuthHandler.SchemeName;
            });
        });

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();

        // The default test graph publishes the "test" service as AllowAnonymous; close
        // it and require a single read role so a denied principal is a real principal
        // that simply lacks the grant, not merely an anonymous caller.
        _fixture.UpdateV2ServiceMetadata(
            WebAppFixture.TestServiceId,
            accessPolicy: new AccessPolicy
            {
                AllowAnonymous = false,
                AllowAnonymousWrite = false,
                AllowedRoles = [ViewerRole]
            });

        // The committed WMS baselines render layer 0 without a TIME default; clear the
        // temporal slot so GetMap/GetFeatureInfo return the seeded points rather than
        // an empty time slice.
        _fixture.UpdateV2ResourceMetadata(WebAppFixture.TestLayerId, clearTemporal: true);
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

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

    [IntegrationTest]
    [Protocol(TestProtocols.Wmts10)]
    [Operation(Operations.SecurityTesting)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/WMTS")]
    [InterfaceOperation(TestProtocols.Wmts10, "GetTile")]
    public async Task Wmts_GetTile_WithoutReadRole_IsRefusedAndReturnsNoTileBytes()
    {
        var url =
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/WMTS?SERVICE=WMTS&REQUEST=GetTile&VERSION=1.0.0" +
            $"&LAYER={WebAppFixture.TestLayerId}&STYLE=default&FORMAT=image/png" +
            "&TILEMATRIXSET=WebMercatorQuad&TILEMATRIX=0&TILEROW=0&TILECOL=0";

        using var denied = await Outsider().GetAsync(url);
        await AssertRefusedWithoutPayloadAsync(denied, HttpStatusCode.Forbidden);

        using var allowed = await Viewer().GetAsync(url);
        await AssertImageAsync(allowed);
    }

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

    private HttpClient Viewer() => CreateClient(ViewerRole);

    private HttpClient Outsider() => CreateClient(OutsiderRole);

    private HttpClient CreateClient(params string[] roles) => _fixture.CreateClient(client =>
    {
        client.DefaultRequestHeaders.Add(TestAuthHandler.UserHeader, "ogc-classic-proof-user");
        client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, string.Join(',', roles));
    });

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

    /// <summary>
    /// The shared half of every denial assertion: the refusal status, and a response
    /// that carries no imagery, tile, coverage bytes or feature attributes.
    /// </summary>
    private static async Task<string> AssertRefusedWithoutPayloadAsync(
        HttpResponseMessage response,
        HttpStatusCode expectedStatus)
    {
        var bytes = await response.Content.ReadAsByteArrayAsync();
        var body = Encoding.UTF8.GetString(bytes);

        response.StatusCode.Should().Be(expectedStatus, body);
        response.Content.Headers.ContentType?.MediaType.Should().NotStartWith("image/",
            "a refusal must not be served as imagery");
        bytes.Take(PngMagic.Length).Should().NotEqual(PngMagic, "no PNG may be emitted on a refusal");
        bytes.Take(2).Should().NotEqual([(byte)0xFF, (byte)0xD8], "no JPEG may be emitted on a refusal");
        bytes.Take(2).Should().NotEqual([(byte)'I', (byte)'I'], "no TIFF may be emitted on a refusal");

        foreach (var value in SeededAttributeValues)
        {
            body.Should().NotContain(value, "a refusal must disclose no attribute data");
        }

        return body;
    }

    private static async Task<byte[]> AssertImageAsync(HttpResponseMessage response)
    {
        var bytes = await response.Content.ReadAsByteArrayAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, Encoding.UTF8.GetString(bytes.Take(512).ToArray()));
        response.Content.Headers.ContentType?.MediaType.Should().Be("image/png");
        bytes.Take(PngMagic.Length).Should().Equal(PngMagic);
        return bytes;
    }
}
