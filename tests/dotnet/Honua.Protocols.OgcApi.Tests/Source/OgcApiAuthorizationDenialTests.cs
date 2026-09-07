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

namespace Honua.Server.Tests.Features.Protocols.Ogc.Api;

/// <summary>
/// #4423: HTTP-level authorization denial proofs for the OGC API surfaces that had none —
/// Records (GA catalog), EDR and Coverages (Preview, security floor retained at full severity).
/// <para>
/// Before this file, <c>git grep -n -i -e '401' -e '403' -e 'Unauthorized' -e 'Forbidden'
/// -e 'tenant' -- tests/dotnet/Honua.Protocols.OgcApi.Tests/Source/Records/</c> returned nothing,
/// and neither EDR nor Coverages had a single denial test. Every test on all three surfaces ran
/// under the F1 development-authentication bypass, which returns an <c>admin</c> principal before
/// any credential is read, so the per-resource access gates
/// (<c>AccessPolicyHelpers.IsResourceAccessible</c> in Records and Coverages,
/// <c>LayerValidationHelpers.ValidateCollectionWithAccessV2Async</c> in EDR) were never entered.
/// </para>
/// <para>
/// Each test below runs with <c>HONUA_DEV_AUTH=false</c>, restricts a published resource to a
/// role, and asserts past the status code: the record id is absent from the catalog body, the
/// coverage response carries no sampled value, and — for Coverages — the mocked raster store
/// records no export query at all, so the denial provably precedes any data access.
/// </para>
/// </summary>
[Collection("Database.OgcApiData")]
public sealed class OgcApiAuthorizationDenialTests
{
    private const string EntitledRole = "catalog-reader";
    private const string Referer = "https://ogcapi-authorization-proof.example/";

    /// <summary>The resource restricted by these tests; layer 0 stays open as the control.</summary>
    private const int RestrictedLayerId = 1;

    private static readonly string RestrictedRecordId = $"layer:{RestrictedLayerId}";
    private static readonly string OpenRecordId = $"layer:{WebAppFixture.TestLayerId}";

    /// <summary>
    /// OGC API Records is GA and is the catalog surface. A record whose resource the caller may
    /// not access must not appear in <c>/items</c> — asserted on the record id in the body, not
    /// on a status, because Records has no denial status: it omits silently.
    /// </summary>
    [IntegrationTest]
    [Protocol(TestProtocols.OgcApiRecords)]
    [Operation(Operations.Query)]
    [Endpoint("GET /ogc/records/collections/{collectionId}/items")]
    public async Task Records_Items_RestrictedResource_IsAbsentForUnentitledCallers()
    {
        await using var fixture = CreateFixture();
        await fixture.InitializeAsync();
        Restrict(fixture);

        var entitled = await IssueAsync(fixture, "catalog-analyst", EntitledRole);
        var unentitled = await IssueAsync(fixture, "catalog-visitor", "unrelated-role");

        // Positive control: an entitled principal sees the restricted record, so its absence
        // below is the access policy and not a missing catalog row.
        var entitledIds = await RecordIdsAsync(fixture, entitled);
        entitledIds.Should().Contain(
            RestrictedRecordId,
            "the entitled principal must see the record the denied callers must not");

        foreach (var (who, token) in new[] { ("an anonymous caller", (string?)null), ("an unentitled principal", unentitled) })
        {
            using var response = await SendAsync(fixture, token, "/ogc/records/collections/honua-catalog/items?limit=100");
            var body = await response.Content.ReadAsStringAsync();
            response.StatusCode.Should().Be(HttpStatusCode.OK, body);

            var ids = ReadRecordIds(body);
            ids.Should().NotContain(
                RestrictedRecordId,
                "{0} must not receive the restricted record; body: {1}",
                who,
                body);
            body.Should().NotContain($"\"{RestrictedRecordId}\"");

            // The surface itself still answers, so the assertion above is not satisfied by an
            // empty or broken catalog.
            ids.Should().Contain(OpenRecordId, "{0} still sees the unrestricted record", who);
        }
    }

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

    private static WebAppFixture CreateFixture(Action<IWebHostBuilder>? configure = null)
        => new WebAppFixture().WithTestLicense(HonuaEdition.Pro).ConfigureWebHost(builder =>
        {
            // Displace the F1 development-authentication bypass, which would otherwise make every
            // caller an admin and the access gates under test unreachable.
            builder.UseSetting("HONUA_DEV_AUTH", "false");
            builder.UseSetting("HONUA_ADMIN_PASSWORD", WebAppFixture.SharedAdminPassword);
            configure?.Invoke(builder);
        });

    private static void Restrict(WebAppFixture fixture)
        => fixture.UpdateV2ResourceMetadata(
            RestrictedLayerId,
            accessPolicy: new AccessPolicy { AllowAnonymous = false, AllowedRoles = [EntitledRole] });

    private static async Task<string[]> RecordIdsAsync(WebAppFixture fixture, string? token)
    {
        using var response = await SendAsync(fixture, token, "/ogc/records/collections/honua-catalog/items?limit=100");
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        return ReadRecordIds(body);
    }

    private static string[] ReadRecordIds(string body)
    {
        using var document = JsonDocument.Parse(body);
        if (!document.RootElement.TryGetProperty("features", out var features)
            || features.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return features.EnumerateArray()
            .Select(feature => feature.TryGetProperty("id", out var id) ? id.GetString() ?? string.Empty : string.Empty)
            .ToArray();
    }

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
