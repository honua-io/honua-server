// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text;
using FluentAssertions;
using Honua.Core.Features.Licensing.Domain;
using Honua.Core.Features.Security.Domain;
using Honua.TestKit;
using Honua.TestKit.Helpers;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Classic;

/// <summary>
/// Shared fixture and denial assertions for the OGC-classic authorization proofs
/// (#4388): WMS <c>GetMap</c> / <c>GetFeatureInfo</c>, WMTS <c>GetTile</c>, and WCS
/// <c>DescribeCoverage</c> / <c>GetCoverage</c>. Before these proofs the only
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
/// because these operations resolve through different resources: WMS and WMTS serve
/// the feature resource <c>res-layer-0</c> while WCS serves the raster resource
/// <c>res-image-test</c>. A service-level policy covers both, and
/// <c>AccessPolicyEvaluator</c> gives a service denial the same force as a layer
/// denial when the layer carries no policy of its own.
///
/// The proofs are split into one concrete class per protocol namespace
/// (<c>…Ogc.Classic.Wms</c>, <c>…Ogc.Classic.Wmts</c>, <c>…Ogc.Classic.Wcs20</c>) so
/// each proving test lands in the CI shard that already owns its protocol; the
/// capability-impact crosswalk rejects a proving test that no shard selects.
/// </remarks>
public abstract class OgcClassicAuthorizationProofTestBase : IAsyncLifetime
{
    protected const string ViewerRole = "map-viewer";
    protected const string OutsiderRole = "unrelated-role";

    /// <summary>Seed attribute values that must never appear in a denial body.</summary>
    protected static readonly string[] SeededAttributeValues =
        ["Test Feature", "Another Feature", "Fifth Feature", "A test feature for integration tests"];

    protected static readonly byte[] PngMagic = [0x89, 0x50, 0x4E, 0x47];

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

    protected HttpClient Viewer() => CreateClient(ViewerRole);

    protected HttpClient Outsider() => CreateClient(OutsiderRole);

    private HttpClient CreateClient(params string[] roles) => _fixture.CreateClient(client =>
    {
        client.DefaultRequestHeaders.Add(TestAuthHandler.UserHeader, "ogc-classic-proof-user");
        client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, string.Join(',', roles));
    });

    /// <summary>
    /// The shared half of every denial assertion: the refusal status, and a response
    /// that carries no imagery, tile, coverage bytes or feature attributes.
    /// </summary>
    protected static async Task<string> AssertRefusedWithoutPayloadAsync(
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

    protected static async Task<byte[]> AssertImageAsync(HttpResponseMessage response)
    {
        var bytes = await response.Content.ReadAsByteArrayAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, Encoding.UTF8.GetString(bytes.Take(512).ToArray()));
        response.Content.Headers.ContentType?.MediaType.Should().Be("image/png");
        bytes.Take(PngMagic.Length).Should().Equal(PngMagic);
        return bytes;
    }
}
