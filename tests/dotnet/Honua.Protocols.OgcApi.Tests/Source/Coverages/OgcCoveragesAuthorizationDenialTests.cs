// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Headers;
using FluentAssertions;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Licensing.Domain;
using Honua.Core.Features.Raster.Domain;
using Honua.Core.Features.Security.Domain;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Helpers;
using Microsoft.AspNetCore.Hosting;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Api.Coverages;

/// <summary>
/// #4423: HTTP-level authorization denial proof for OGC API Coverages (Preview; the security
/// floor is retained at full severity).
/// <para>
/// Before this file Coverages had no denial test at all: every Coverages test ran under the F1
/// development-authentication bypass, which returns an <c>admin</c> principal before any
/// credential is read, so the per-resource access gate
/// (<c>AccessPolicyHelpers.IsResourceAccessible</c>) was never entered.
/// </para>
/// <para>
/// The test below runs with <c>HONUA_DEV_AUTH=false</c>, restricts the published resource to a
/// role, and asserts past the status code — on the mocked raster store, which must record no
/// export query at all, so the denial provably precedes any data access. The Records and EDR
/// halves of the same proof live in <c>Ogc.Api.Records.OgcRecordsAuthorizationDenialTests</c> and
/// <c>Ogc.Api.Edr.EdrAuthorizationDenialTests</c>; the three are split by surface so each lands in
/// the CI shard that owns its namespace.
/// </para>
/// </summary>
[Collection("Database.OgcApiData")]
public sealed class OgcCoveragesAuthorizationDenialTests
{
    private const string EntitledRole = "catalog-reader";
    private const string Referer = "https://ogcapi-authorization-proof.example/";

    /// <summary>
    /// Coverages <c>/coverage</c> returns raster bytes. The denial is asserted both on the
    /// response and on the raster store: a denied request must never reach an export query.
    /// </summary>
    [IntegrationTest]
    [Protocol(TestProtocols.OgcApiCoverages)]
    [Operation(Operations.Export)]
    [Endpoint("GET /ogc/coverages/collections/{collectionId}/coverage")]
    public async Task Coverages_Coverage_RestrictedCollection_NeverReachesTheRasterStore()
    {
        var exportQueries = new List<RasterQuery>();
        var raster = CoverageDepthRasterStore.CreateRasterInfo(901, width: 64, height: 64, pixelSize: 0.003125);
        var rasterStore = CoverageDepthRasterStore.Create(raster, exportQueries, emptyDataForSingleBand: null);

        await using var fixture = CreateFixture().ReplaceService(rasterStore);
        await fixture.InitializeAsync();
        fixture.UpdateV2ResourceMetadata(
            WebAppFixture.TestLayerId,
            accessPolicy: new AccessPolicy { AllowAnonymous = false, AllowedRoles = [EntitledRole] });

        var entitled = await IssueAsync(fixture, "coverage-analyst", EntitledRole);
        var unentitled = await IssueAsync(fixture, "coverage-visitor", "unrelated-role");

        const string coverage = "/ogc/coverages/collections/0/coverage";

        foreach (var (who, token) in new[] { ("an anonymous caller", (string?)null), ("an unentitled principal", unentitled) })
        {
            using var response = await SendAsync(fixture, token, coverage);
            var body = await response.Content.ReadAsByteArrayAsync();

            response.StatusCode.Should().NotBe(
                HttpStatusCode.OK,
                "{0} must not read the restricted coverage",
                who);
            (response.Content.Headers.ContentType?.MediaType ?? string.Empty)
                .Should().NotStartWith("image/", "{0} must receive no raster payload", who);

            exportQueries.Should().BeEmpty(
                "the denial must precede any data access: {0} caused {1} raster export query(ies)",
                who,
                exportQueries.Count);

            body.Length.Should().BeLessThan(
                4096,
                "a denial body is an error document, not a coverage");
        }

        // Positive control: the entitled principal does reach the raster store, so the
        // BeEmpty assertions above measure the authorization decision.
        using var allowed = await SendAsync(fixture, entitled, coverage);
        allowed.StatusCode.Should().Be(HttpStatusCode.OK, await allowed.Content.ReadAsStringAsync());
        exportQueries.Should().NotBeEmpty("the entitled read reaches the raster export path");
    }

    private static WebAppFixture CreateFixture()
        => new WebAppFixture().WithTestLicense(HonuaEdition.Pro).ConfigureWebHost(builder =>
        {
            // Displace the F1 development-authentication bypass, which would otherwise make every
            // caller an admin and the access gate under test unreachable.
            builder.UseSetting("HONUA_DEV_AUTH", "false");
            builder.UseSetting("HONUA_ADMIN_PASSWORD", WebAppFixture.SharedAdminPassword);
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
}
