// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Licensing.Domain;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.Domain;
using Honua.Core.Features.Security.Domain;
using Honua.Server.Tests.Features.Protocols.Ogc.Api.Coverages;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Helpers;
using Microsoft.AspNetCore.Hosting;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Api.Edr;

/// <summary>
/// #4423: HTTP-level authorization denial proof for OGC API EDR (Preview; the security floor is
/// retained at full severity).
/// <para>
/// Before this file EDR had no denial test at all: every EDR test ran under the F1
/// development-authentication bypass, which returns an <c>admin</c> principal before any
/// credential is read, so the per-collection access gate
/// (<c>LayerValidationHelpers.ValidateCollectionWithAccessV2Async</c>) was never entered.
/// </para>
/// <para>
/// The test below runs with <c>HONUA_DEV_AUTH=false</c>, restricts the published collection to a
/// role, and asserts past the status code: the response carries no sampled value. The Records and
/// Coverages halves of the same proof live in
/// <c>Ogc.Api.Records.OgcRecordsAuthorizationDenialTests</c> and
/// <c>Ogc.Api.Coverages.OgcCoveragesAuthorizationDenialTests</c>; the three are split by surface
/// so each lands in the CI shard that owns its namespace.
/// </para>
/// </summary>
[Collection("Database.OgcApiData")]
public sealed class EdrAuthorizationDenialTests
{
    private const string EntitledRole = "catalog-reader";
    private const string Referer = "https://ogcapi-authorization-proof.example/";

    /// <summary>
    /// EDR <c>/position</c> and <c>/cube</c> return sampled pixel values. A caller without access
    /// to the collection's resource receives neither the values nor the collection.
    /// </summary>
    [IntegrationTest]
    [Protocol(TestProtocols.OgcApiCoverages)]
    [Operation(Operations.Query)]
    [Endpoint("GET /edr/collections/{collectionId}/position")]
    [Endpoint("GET /edr/collections/{collectionId}/cube")]
    public async Task Edr_PositionAndCube_RestrictedCollection_ReturnNoSampledValues()
    {
        var rasterStore = CreateEdrRasterStore();
        await using var fixture = CreateFixture(builder => builder.UseSetting(
            "Capabilities:Experimental:serve.ogc-api-edr:Enabled", "true"))
            .ReplaceService(rasterStore);
        await fixture.InitializeAsync();

        // EDR reads the raster published at the canonical test layer, so restrict that one here.
        fixture.UpdateV2ResourceMetadata(
            WebAppFixture.TestLayerId,
            accessPolicy: new AccessPolicy { AllowAnonymous = false, AllowedRoles = [EntitledRole] });

        var entitled = await IssueAsync(fixture, "edr-analyst", EntitledRole);
        var unentitled = await IssueAsync(fixture, "edr-visitor", "unrelated-role");

        const string position = "/edr/collections/0/position?coords=POINT(-122.4%2037.8)";
        const string cube = "/edr/collections/0/cube?bbox=-122.45,37.75,-122.35,37.85";

        // Positive control: the entitled principal reads a real sampled value.
        using (var allowed = await SendAsync(fixture, entitled, position))
        {
            var body = await allowed.Content.ReadAsStringAsync();
            allowed.StatusCode.Should().Be(HttpStatusCode.OK, body);
            using var document = JsonDocument.Parse(body);
            document.RootElement.GetProperty("ranges").EnumerateObject().Should().NotBeEmpty(
                "the entitled caller receives sampled values; body: {0}", body);
        }

        foreach (var url in new[] { position, cube })
        {
            foreach (var (who, token) in new[] { ("an anonymous caller", (string?)null), ("an unentitled principal", unentitled) })
            {
                using var response = await SendAsync(fixture, token, url);
                var body = await response.Content.ReadAsStringAsync();

                response.StatusCode.Should().NotBe(
                    HttpStatusCode.OK,
                    "{0} must not read {1}; body: {2}",
                    who,
                    url,
                    body);
                body.Should().NotContain("\"ranges\"", "{0} must receive no coverage ranges from {1}", who, url);
                body.Should().NotContain("CoverageJSON");
            }
        }

        // The collection is also absent from the unentitled listing.
        using var collections = await SendAsync(fixture, unentitled, "/edr/collections");
        (await collections.Content.ReadAsStringAsync()).Should().NotContain("\"id\":\"0\"");
    }

    private static WebAppFixture CreateFixture(Action<IWebHostBuilder> configure)
        => new WebAppFixture().WithTestLicense(HonuaEdition.Pro).ConfigureWebHost(builder =>
        {
            // Displace the F1 development-authentication bypass, which would otherwise make every
            // caller an admin and the access gate under test unreachable.
            builder.UseSetting("HONUA_DEV_AUTH", "false");
            builder.UseSetting("HONUA_ADMIN_PASSWORD", WebAppFixture.SharedAdminPassword);
            configure(builder);
        });

    private static async Task<string> IssueAsync(WebAppFixture fixture, string principalId, string role)
        => (await fixture.GetService<IPortalTokenIssuer>().IssueAsync(
            new PortalTokenIssueRequest(
                principalId,
                principalId,
                TenantId: null,
                Roles: [role],
                PortalTokenClientType.Referer,
                Referer,
                DateTimeOffset.UtcNow.AddMinutes(30)),
            CancellationToken.None)).Token;

    private static async Task<HttpResponseMessage> SendAsync(WebAppFixture fixture, string? token, string url)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.Referrer = new Uri(Referer);
        }

        // A dedicated client: the fixture's shared client carries an admin X-API-Key.
        using var client = fixture.CreateClient();
        return await client.SendAsync(request);
    }

    private static IRasterStore CreateEdrRasterStore()
    {
        var rasterStore = Substitute.For<IRasterStore>();
        var raster = CoverageDepthRasterStore.CreateRasterInfo(902, width: 64, height: 64, pixelSize: 0.003125);

        rasterStore.GetPrimaryRasterInfoAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<RasterInfo?>(null));
        rasterStore.GetPrimaryRasterInfoAsync(WebAppFixture.TestLayerId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<RasterInfo?>(raster));
        rasterStore.GetExtentAsync(
                WebAppFixture.TestLayerId,
                raster.Id,
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<RasterExtent?>(raster.Extent));
        rasterStore.IdentifyAsync(
                WebAppFixture.TestLayerId,
                raster.Id,
                Arg.Any<double>(),
                Arg.Any<double>(),
                Arg.Any<int?>(),
                Arg.Any<RasterIdentifyRendering?>(),
                Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult(new PixelValueResult
            {
                X = call.ArgAt<double>(2),
                Y = call.ArgAt<double>(3),
                Srid = 4326,
                HasData = true,
                BandValues = new Dictionary<int, object?> { [1] = 11d },
            }));

        return rasterStore;
    }
}
