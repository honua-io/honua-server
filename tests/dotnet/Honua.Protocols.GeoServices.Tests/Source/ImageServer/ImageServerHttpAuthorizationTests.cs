// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Security.Domain;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Extensions;
using Honua.TestKit.Infrastructure;
using Microsoft.AspNetCore.Hosting;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.ImageServer;

/// <summary>
/// #4423: HTTP-level authorization proof for the ImageServer read operations that return pixels
/// or attribute values.
/// <para>
/// The whole <c>/rest/services/{id}/ImageServer</c> group is <c>.AllowAnonymous()</c> by design —
/// "access is enforced by the handlers via the layer access policy" — so handler-internal policy
/// is the only control on ~45 operations. Before this file, that policy was proven for two
/// operations, both at unit level with a hand-built <c>DefaultHttpContext</c>, and
/// <c>git grep -q 'Unauthorized' -- Source/ImageServer</c> returned nothing: there was no 401 or
/// 403 test for any ImageServer operation through the HTTP stack, and every ImageServer test ran
/// under the F1 development-authentication bypass, which returns an <c>admin</c> principal before
/// any credential is read.
/// </para>
/// <para>
/// This test restricts the published raster resource to a role, then drives the six operations
/// that return imagery or pixel values — <c>exportImage</c>, <c>identify</c>, <c>getSamples</c>,
/// <c>tile</c>, WMTS <c>GetTile</c> and WMTS <c>GetFeatureInfo</c> — three ways: as an entitled
/// principal (positive control, which asserts the real pixel values the mosaic holds), as an
/// anonymous caller, and as an authenticated principal holding a different role. Each denial is
/// asserted past the status: the response must carry none of the pixel values, none of the raster
/// names, and no image payload.
/// </para>
/// </summary>
[Collection("Database.GeoServicesRaster")]
[Protocol(TestProtocols.ImageServer)]
public sealed class ImageServerHttpAuthorizationTests
{
    /// <summary>Role granted on the restricted raster resource.</summary>
    private const string EntitledRole = "imagery-reader";

    /// <summary>A role the resource does not grant. Held by the authenticated-denied principal.</summary>
    private const string UnrelatedRole = "cartography-reader";

    private const string Referer = "https://imageserver-authorization-proof.example/";

    private const int LayerId = WebAppFixture.TestLayerId;

    private static readonly string ImageServerBase = $"/rest/services/{LayerId}/ImageServer";

    /// <summary>
    /// The pixel values the Issue-522 mosaic holds. The entitled arm reads them back; every
    /// denied response is asserted to carry none of them.
    /// </summary>
    private static readonly string[] RasterNames = ["west", "east", "overlap-newest"];

    /// <summary>
    /// The seeded mosaic's east raster covers 3.5,1 and holds this value, so both the identify
    /// and getSamples positive controls have a real oracle rather than a shape assertion.
    /// </summary>
    private const double EastRasterValue = 40;

    /// <summary>Esri's <c>TokenRequired</c> error code, returned to an unauthenticated caller.</summary>
    private const int EsriTokenRequired = 499;

    /// <summary>
    /// getSamples takes its points in the Esri multipoint JSON form; the bare <c>x,y</c> form
    /// accepted by identify is not a valid geometry here, and a denial asserted on an invalid
    /// request would prove nothing about authorization.
    /// </summary>
    private static readonly string GetSamplesUrl =
        $"{ImageServerBase}/getSamples?f=json&geometryType=esriGeometryMultipoint&geometry="
        + Uri.EscapeDataString("""{"points":[[3.5,1]],"spatialReference":{"wkid":4326}}""");

    [IntegrationTest]
    [Operation(Operations.Export)]
    [Endpoint("GET /rest/services/{id}/ImageServer/exportImage")]
    [Endpoint("GET /rest/services/{id}/ImageServer/identify")]
    [Endpoint("GET /rest/services/{id}/ImageServer/getSamples")]
    [Endpoint("GET /rest/services/{id}/ImageServer/tile/{level}/{row}/{col}")]
    [Endpoint("GET /rest/services/{id}/ImageServer/WMTS")]
    public async Task RestrictedImagery_AnonymousAndUnentitledCallers_ReceiveNoPixels()
    {
        var fixture = new WebAppFixture()
            .ConfigureWebHost(builder =>
            {
                // Displace the F1 development-authentication bypass; without this every caller is
                // an admin and the handler policy under test is unreachable.
                builder.UseSetting("HONUA_DEV_AUTH", "false");
                builder.UseSetting("HONUA_ADMIN_PASSWORD", WebAppFixture.SharedAdminPassword);
            });

        try
        {
            await fixture.InitializeAsync();

            fixture.UpdateV2ResourceMetadata(
                LayerId,
                accessPolicy: new AccessPolicy { AllowAnonymous = false, AllowedRoles = [EntitledRole] });

            var entitled = await IssueAsync(fixture, "imagery-analyst", EntitledRole);
            var unentitled = await IssueAsync(fixture, "map-viewer", UnrelatedRole);

            // honua.raster_data is process-global; hold the seed advisory lock across the whole
            // matrix so a sibling collection cannot replace the rows mid-run.
            await RasterIntegrationTestData.RunWithIssue522MosaicAsync(fixture, async () =>
            {
                // ---- positive control: the imagery really is there, and really is readable ----
                await AssertEntitledReadsRealPixelsAsync(fixture, entitled);

                // ---- the denials -------------------------------------------------------------
                foreach (var (name, url) in PixelReturningOperations())
                {
                    await AssertDeniedAsync(fixture, token: null, name, url, "an anonymous caller");
                    await AssertDeniedAsync(fixture, unentitled, name, url, "a principal without the granted role");
                }
            });
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    /// <summary>The six pixel- or value-returning operations named by #4423.</summary>
    private static IEnumerable<(string Name, string Url)> PixelReturningOperations()
    {
        yield return ("exportImage", $"{ImageServerBase}/exportImage?bbox=0,0,4,2&size=64,32&format=png&f=image");
        yield return ("identify", $"{ImageServerBase}/identify?geometry=3.5,1&geometryType=esriGeometryPoint&f=json");
        yield return ("getSamples", GetSamplesUrl);
        yield return ("tile", $"{ImageServerBase}/tile/0/0/0");
        yield return (
            "WMTS GetTile",
            $"{ImageServerBase}/WMTS?SERVICE=WMTS&REQUEST=GetTile&VERSION=1.0.0&LAYER={LayerId}"
            + "&STYLE=default&FORMAT=image/png&TILEMATRIXSET=WebMercatorQuad&TILEMATRIX=0&TILEROW=0&TILECOL=0");
        yield return (
            "WMTS GetFeatureInfo",
            $"{ImageServerBase}/WMTS?SERVICE=WMTS&REQUEST=GetFeatureInfo&VERSION=1.0.0&LAYER={LayerId}"
            + "&STYLE=default&FORMAT=image/png&TILEMATRIXSET=WebMercatorQuad&TILEMATRIX=0&TILEROW=0&TILECOL=0"
            + "&I=128&J=128&INFOFORMAT=application/json");
    }

    /// <summary>
    /// The entitled principal reads the mosaic's actual pixel values. This is what makes the
    /// denial assertions meaningful: the same routes, on the same data, in the same run, do
    /// return imagery when the policy allows it.
    /// </summary>
    private static async Task AssertEntitledReadsRealPixelsAsync(WebAppFixture fixture, string token)
    {
        using var identify = await SendAsync(
            fixture, token, $"{ImageServerBase}/identify?geometry=3.5,1&geometryType=esriGeometryPoint&f=json");
        var identifyBody = await identify.Content.ReadAsStringAsync();
        identify.StatusCode.Should().Be(HttpStatusCode.OK, identifyBody);
        using (var document = JsonDocument.Parse(identifyBody))
        {
            document.RootElement.GetProperty("properties").GetProperty("Band_1").GetDouble()
                .Should().Be(EastRasterValue, "the east raster covers 3.5,1 and holds the value 40");
        }

        using var export = await SendAsync(
            fixture, token, $"{ImageServerBase}/exportImage?bbox=0,0,4,2&size=64,32&format=png&f=image");
        export.StatusCode.Should().Be(HttpStatusCode.OK, await export.Content.ReadAsStringAsync());
        export.Content.Headers.ContentType?.MediaType.Should().StartWith("image/");
        (await export.Content.ReadAsByteArrayAsync()).Should().NotBeEmpty();

        using var samples = await SendAsync(fixture, token, GetSamplesUrl);
        var samplesBody = await samples.Content.ReadAsStringAsync();
        samples.StatusCode.Should().Be(HttpStatusCode.OK, samplesBody);
        using (var document = JsonDocument.Parse(samplesBody))
        {
            var sampled = document.RootElement.GetProperty("samples");
            sampled.GetArrayLength().Should().Be(1, "one point was sampled; body: {0}", samplesBody);
            double.Parse(
                    sampled[0].GetProperty("value").GetString()!,
                    System.Globalization.CultureInfo.InvariantCulture)
                .Should().Be(
                    EastRasterValue,
                    "the entitled caller reads the east raster's actual pixel value; body: {0}",
                    samplesBody);
        }
    }

    /// <summary>
    /// Asserts a denial that goes past the status: no image bytes, no pixel value, no raster
    /// identity in the response.
    /// </summary>
    private static async Task AssertDeniedAsync(
        WebAppFixture fixture,
        string? token,
        string operation,
        string url,
        string who)
    {
        using var response = await SendAsync(fixture, token, url);
        var mediaType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
        var payload = await response.Content.ReadAsByteArrayAsync();

        mediaType.Should().NotStartWith(
            "image/",
            "{0} must not receive rendered imagery from {1}",
            who,
            operation);

        var body = SafeText(payload);

        // GeoServices answers with HTTP 200 and carries the outcome in the body envelope, so the
        // denial is asserted on the envelope's error code — 499 is Esri's TokenRequired, 401/403
        // the equivalents on the non-Esri shapes. A success-shaped body fails this assertion.
        if ((int)response.StatusCode == (int)HttpStatusCode.OK)
        {
            AssertGeoServicesDenialEnvelope(body, who, operation);
        }
        else
        {
            ((int)response.StatusCode).Should().BeOneOf(
                [(int)HttpStatusCode.Unauthorized, (int)HttpStatusCode.Forbidden, EsriTokenRequired],
                "{0} must be refused {1}; body: {2}",
                who,
                operation,
                body);
        }

        // The pixel values and raster identities the entitled arm just read back must be absent.
        body.Should().NotContain("Band_1", "{0} must learn no pixel value from {1}", who, operation);
        body.Should().NotContain("\"samples\"", "{0} must receive no sample array from {1}", who, operation);
        foreach (var rasterName in RasterNames)
        {
            body.Should().NotContain(
                $"\"{rasterName}\"",
                "{0} must not learn the identity of raster '{1}' from {2}",
                who,
                rasterName,
                operation);
        }
    }

    /// <summary>
    /// Asserts an HTTP-200 GeoServices response is the error envelope for a denial, and never a
    /// success-shaped payload.
    /// </summary>
    private static void AssertGeoServicesDenialEnvelope(string body, string who, string operation)
    {
        body.Should().NotBeNullOrWhiteSpace(
            "{0} must receive an explicit denial from {1}, not an empty 200", who, operation);

        using var document = JsonDocument.Parse(body);
        document.RootElement.TryGetProperty("error", out var error).Should().BeTrue(
            "{0} must receive the GeoServices error envelope from {1}; body: {2}", who, operation, body);
        error.GetProperty("code").GetInt32().Should().BeOneOf(
            [(int)HttpStatusCode.Unauthorized, (int)HttpStatusCode.Forbidden, EsriTokenRequired],
            "{0} must be refused {1} by an authorization decision; body: {2}", who, operation, body);
    }

    private static string SafeText(byte[] payload)
        => payload.Length == 0 ? string.Empty : System.Text.Encoding.UTF8.GetString(payload);

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

        // A dedicated client per request: the fixture's shared client carries an admin X-API-Key,
        // which would satisfy every request under test.
        using var client = fixture.CreateClient();
        return await client.SendAsync(request);
    }
}
