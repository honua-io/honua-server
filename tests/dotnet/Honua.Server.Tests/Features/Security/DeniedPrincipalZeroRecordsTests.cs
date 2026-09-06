// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Licensing.Domain;
using Honua.Core.Features.Security.Abstractions;
using Honua.Core.Features.Security.Domain;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Npgsql;
using DomainAccessDecision = Honua.Core.Features.Security.Domain.AccessDecision;

namespace Honua.Server.Tests.Features.Security;

/// <summary>
/// #4386: proves that a denied principal receives <b>zero records</b>, not merely a
/// status code.
/// <para>
/// Every pre-existing RBAC denial assertion in this repository stopped at
/// <c>response.StatusCode.Should().Be(...)</c>, and the fixture that proved
/// authorization (<c>ServiceRbacTestFixture</c> over <c>TestWebApplicationFactory</c>)
/// substitutes an in-memory feature store, so no test proved a denied principal was
/// refused rows that actually exist in PostGIS.
/// </para>
/// <para>
/// These tests run against a real PostGIS-backed host with the development
/// authentication bypass switched off and a real portal credential that holds
/// <see cref="AllowedRole"/> and never <see cref="DeniedRole"/>. Both the allowed and
/// the denied layer hold real, non-empty rows carrying distinguishable markers, so a
/// disclosure is observable in the response body rather than inferred from a status.
/// </para>
/// <para>
/// The <c>authorizationDefectInjected</c> arm is the negative control demanded by the
/// last acceptance criterion: it replaces <see cref="IAccessPolicyEvaluator"/> with an
/// always-allow implementation — a deliberately bypassed authorization decision — and
/// asserts the denied read then <b>does</b> return the restricted rows and their
/// marker. That proves the zero-record assertions have teeth: they are not satisfied
/// by a route that returns nothing for unrelated reasons (wrong path, missing layer,
/// unseeded table), because the same route on the same data returns the rows the
/// moment the authorization decision is removed.
/// </para>
/// </summary>
[Collection("Database")]
[Operation(Operations.Query)]
public sealed class DeniedPrincipalZeroRecordsTests
{
    /// <summary>Role held by the credential under test; gates layer 0 only.</summary>
    private const string AllowedRole = "alpha-reader";

    /// <summary>Role gating layer 1. The credential under test never holds it.</summary>
    private const string DeniedRole = "beta-reader";

    private const int AllowedLayerId = 0;
    private const int DeniedLayerId = 1;

    private const long AllowedObjectId = 94101;
    private const long AllowedSecondObjectId = 94102;
    private const long DeniedObjectId = 94201;
    private const long DeniedSecondObjectId = 94202;

    /// <summary>Marker written into the readable layer's rows.</summary>
    private const string AllowedMarker = "alpha-readable-parcel-94101";

    private const string AllowedSecondMarker = "alpha-readable-parcel-94102";

    /// <summary>
    /// Marker written into the restricted layer's rows. Its absence from a denied
    /// response body is the disclosure assertion #4386 asks for.
    /// </summary>
    private const string DeniedMarker = "beta-restricted-parcel-94201";

    private const string DeniedSecondMarker = "beta-restricted-parcel-94202";

    private const string Referer = "https://denied-principal-zero-records.example/";

    public static TheoryData<string, bool> SurfaceMatrix()
    {
        var data = new TheoryData<string, bool>();
        foreach (var surface in new[] { FeatureServerSurface, OgcFeaturesSurface, ODataSurface })
        {
            data.Add(surface, false);
            data.Add(surface, true);
        }

        return data;
    }

    private const string FeatureServerSurface = "featureserver";
    private const string OgcFeaturesSurface = "ogc-features";
    private const string ODataSurface = "odata";

    /// <summary>
    /// A denied principal's read of a resource that genuinely holds rows returns no
    /// record from that resource, and its allowed read returns exactly the allowed
    /// ids with no row from the denied sibling.
    /// </summary>
    [IntegrationTheory]
    [MemberData(nameof(SurfaceMatrix))]
    [Protocol(TestProtocols.FeatureServer, TestProtocols.OgcApiFeatures, TestProtocols.ODataV4)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    [Endpoint("GET /ogc/features/collections/{collectionId}/items")]
    [Endpoint("GET /odata/Features({layerId})")]
    public async Task DeniedPrincipal_ReadingAResourceHoldingRealRows_ReceivesZeroRecords(
        string surface,
        bool authorizationDefectInjected)
    {
        await using var fixture = CreateFixture(authorizationDefectInjected);
        await fixture.InitializeAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        var ct = timeout.Token;

        ApplyAccessPolicies(fixture);
        await SeedRealRowsAsync(fixture, ct);

        var credential = await IssueAllowedCredentialAsync(fixture, ct);

        // ---- denied resource --------------------------------------------------
        using var deniedResponse = await ReadAsync(fixture, surface, DeniedLayerId, credential, ct);
        var deniedBody = await deniedResponse.Content.ReadAsStringAsync(ct);

        if (authorizationDefectInjected)
        {
            // Negative control: with the authorization decision removed the very same
            // request returns the restricted rows. The zero-record assertions below
            // therefore measure the authorization decision, not an empty route.
            deniedResponse.StatusCode.Should().Be(
                HttpStatusCode.OK,
                "a bypassed authorization decision must expose the restricted layer: {0}",
                deniedBody);
            var disclosed = ReadRecordIds(surface, deniedBody);
            disclosed.Should().BeEquivalentTo(
                new[] { DeniedObjectId, DeniedSecondObjectId },
                "the restricted layer really does hold rows on this route");
            deniedBody.Should().Contain(DeniedMarker);
            return;
        }

        deniedResponse.StatusCode.Should().Be(
            ExpectedDenialStatus(surface),
            "the credential does not hold {0}: {1}",
            DeniedRole,
            deniedBody);

        // The assertion #4386 was filed for: zero records, not merely a status.
        ReadRecordIds(surface, deniedBody).Should().BeEmpty(
            "a denied principal must receive no record from the denied resource, body was: {0}",
            deniedBody);
        deniedBody.Should().NotContain(DeniedMarker);
        deniedBody.Should().NotContain(DeniedSecondMarker);
        deniedBody.Should().NotContain(DeniedObjectId.ToString(System.Globalization.CultureInfo.InvariantCulture));

        // ---- allowed resource -------------------------------------------------
        using var allowedResponse = await ReadAsync(fixture, surface, AllowedLayerId, credential, ct);
        var allowedBody = await allowedResponse.Content.ReadAsStringAsync(ct);
        allowedResponse.StatusCode.Should().Be(HttpStatusCode.OK, allowedBody);

        ReadRecordIds(surface, allowedBody).Should().BeEquivalentTo(
            new[] { AllowedObjectId, AllowedSecondObjectId },
            "the allowed principal receives exactly the allowed ids");
        allowedBody.Should().Contain(AllowedMarker);
        allowedBody.Should().Contain(AllowedSecondMarker);

        // Rows from the denied sibling resource are absent from the allowed read.
        allowedBody.Should().NotContain(DeniedMarker);
        allowedBody.Should().NotContain(DeniedSecondMarker);
    }

    private static WebAppFixture CreateFixture(bool authorizationDefectInjected)
    {
        var fixture = new WebAppFixture()
            .WithTestLicense(HonuaEdition.Pro)
            .ConfigureWebHost(builder =>
            {
                builder.UseSetting("HONUA_DEV_AUTH", "false");
                builder.UseSetting("HONUA_ADMIN_PASSWORD", WebAppFixture.SharedAdminPassword);
            });

        return authorizationDefectInjected
            ? fixture.ReplaceService<IAccessPolicyEvaluator>(new BypassedAccessPolicyEvaluator())
            : fixture;
    }

    private static void ApplyAccessPolicies(WebAppFixture fixture)
    {
        fixture.UpdateV2ResourceMetadata(
            AllowedLayerId,
            accessPolicy: new AccessPolicy { AllowAnonymous = false, AllowedRoles = [AllowedRole] });
        fixture.UpdateV2ResourceMetadata(
            DeniedLayerId,
            accessPolicy: new AccessPolicy { AllowAnonymous = false, AllowedRoles = [DeniedRole] });
    }

    private static async Task SeedRealRowsAsync(WebAppFixture fixture, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(fixture.Postgres.ConnectionString);
        await connection.OpenAsync(ct);
        using var identifiers = new NpgsqlCommandBuilder();
        var schema = identifiers.QuoteIdentifier(fixture.CurrentSchema!);

        await using var seed = new NpgsqlCommand($$"""
            INSERT INTO {{schema}}.features(objectid, layer_id, geometry, attributes) VALUES
              ({{AllowedObjectId}}, {{AllowedLayerId}}, ST_SetSRID(ST_MakePoint(-122.41, 37.77), 4326), '{"name":"{{AllowedMarker}}"}'),
              ({{AllowedSecondObjectId}}, {{AllowedLayerId}}, ST_SetSRID(ST_MakePoint(-122.42, 37.78), 4326), '{"name":"{{AllowedSecondMarker}}"}'),
              ({{DeniedObjectId}}, {{DeniedLayerId}}, ST_SetSRID(ST_MakePoint(-122.43, 37.79), 4326), '{"name":"{{DeniedMarker}}"}'),
              ({{DeniedSecondObjectId}}, {{DeniedLayerId}}, ST_SetSRID(ST_MakePoint(-122.44, 37.80), 4326), '{"name":"{{DeniedSecondMarker}}"}');
            """, connection);
        await seed.ExecuteNonQueryAsync(ct);

        // The denied resource must genuinely hold rows, otherwise "zero records" is
        // vacuous. Read them straight out of PostGIS before any HTTP call.
        await using var verify = new NpgsqlCommand(
            $"SELECT count(*) FROM {schema}.features WHERE layer_id = {DeniedLayerId} AND objectid IN ({DeniedObjectId}, {DeniedSecondObjectId});",
            connection);
        var stored = Convert.ToInt64(await verify.ExecuteScalarAsync(ct), System.Globalization.CultureInfo.InvariantCulture);
        stored.Should().Be(2, "the denied resource must hold real, non-empty rows");
    }

    private static async Task<PortalTokenIssuance> IssueAllowedCredentialAsync(
        WebAppFixture fixture,
        CancellationToken ct)
        => await fixture.GetService<IPortalTokenIssuer>().IssueAsync(
            new PortalTokenIssueRequest(
                "zero-records-proof",
                "Zero-records proof",
                TenantId: null,
                Roles: [AllowedRole],
                PortalTokenClientType.Referer,
                Referer,
                DateTimeOffset.UtcNow.AddMinutes(5)),
            ct);

    private static async Task<HttpResponseMessage> ReadAsync(
        WebAppFixture fixture,
        string surface,
        int layerId,
        PortalTokenIssuance credential,
        CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, RouteFor(surface, layerId));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential.Token);
        request.Headers.Referrer = new Uri(Referer);
        return await fixture.Client.SendAsync(request, ct);
    }

    private static string RouteFor(string surface, int layerId) => surface switch
    {
        FeatureServerSurface =>
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{layerId}/query?f=json&where=1%3D1&outFields=*&returnGeometry=false",
        OgcFeaturesSurface => $"/ogc/features/collections/{layerId}/items?limit=50",
        ODataSurface => $"/odata/Features({layerId})",
        _ => throw new ArgumentOutOfRangeException(nameof(surface), surface, "Unknown surface."),
    };

    private static HttpStatusCode ExpectedDenialStatus(string surface) => surface switch
    {
        // GeoServices maps a denial onto its own error envelope carried by a 200 body
        // (asserted separately by the zero-record and marker checks).
        FeatureServerSurface => HttpStatusCode.Forbidden,
        OgcFeaturesSurface => HttpStatusCode.Forbidden,
        ODataSurface => HttpStatusCode.Forbidden,
        _ => throw new ArgumentOutOfRangeException(nameof(surface), surface, "Unknown surface."),
    };

    /// <summary>
    /// Extracts the record identifiers a response body actually carries. A body that
    /// is not a success-shaped payload carries no records, which is exactly what the
    /// denial case asserts.
    /// </summary>
    private static IReadOnlyList<long> ReadRecordIds(string surface, string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return [];
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            return [];
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return [];
            }

            return surface switch
            {
                FeatureServerSurface => ReadFeatureServerIds(root),
                OgcFeaturesSurface => ReadOgcFeatureIds(root),
                ODataSurface => ReadODataIds(root),
                _ => throw new ArgumentOutOfRangeException(nameof(surface), surface, "Unknown surface."),
            };
        }
    }

    private static IReadOnlyList<long> ReadFeatureServerIds(JsonElement root)
    {
        if (!root.TryGetProperty("features", out var features) || features.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var ids = new List<long>();
        foreach (var feature in features.EnumerateArray())
        {
            if (feature.TryGetProperty("attributes", out var attributes)
                && TryReadId(attributes, out var id))
            {
                ids.Add(id);
            }
        }

        return ids;
    }

    private static IReadOnlyList<long> ReadOgcFeatureIds(JsonElement root)
    {
        if (!root.TryGetProperty("features", out var features) || features.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var ids = new List<long>();
        foreach (var feature in features.EnumerateArray())
        {
            if (feature.TryGetProperty("id", out var id) && TryReadNumeric(id, out var value))
            {
                ids.Add(value);
                continue;
            }

            if (feature.TryGetProperty("properties", out var properties) && TryReadId(properties, out var fromProperties))
            {
                ids.Add(fromProperties);
            }
        }

        return ids;
    }

    private static IReadOnlyList<long> ReadODataIds(JsonElement root)
    {
        if (!root.TryGetProperty("value", out var value) || value.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var ids = new List<long>();
        foreach (var entry in value.EnumerateArray())
        {
            if (TryReadId(entry, out var id))
            {
                ids.Add(id);
            }
        }

        return ids;
    }

    private static bool TryReadId(JsonElement container, out long id)
    {
        foreach (var property in container.EnumerateObject())
        {
            if (string.Equals(property.Name, "objectid", StringComparison.OrdinalIgnoreCase)
                && TryReadNumeric(property.Value, out id))
            {
                return true;
            }
        }

        id = 0;
        return false;
    }

    private static bool TryReadNumeric(JsonElement element, out long value)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Number when element.TryGetInt64(out value):
                return true;
            case JsonValueKind.String when long.TryParse(
                element.GetString(),
                System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture,
                out value):
                return true;
            default:
                value = 0;
                return false;
        }
    }

    /// <summary>
    /// The deliberately introduced defect: authorization always allows. Injected only
    /// by the negative-control arm, so the zero-record assertions are challenged by a
    /// host that genuinely discloses the restricted rows.
    /// </summary>
    private sealed class BypassedAccessPolicyEvaluator : IAccessPolicyEvaluator
    {
        public Task<DomainAccessDecision> EvaluateAsync(ClaimsPrincipal principal, string resource, string action)
            => Task.FromResult(DomainAccessDecision.Allowed());

        public DomainAccessDecision Evaluate(ClaimsPrincipal principal, string resource, string action)
            => DomainAccessDecision.Allowed();

        public Task<DomainAccessDecision> EvaluateAsync(
            ClaimsPrincipal principal,
            AccessPolicy? layerPolicy,
            AccessPolicy? servicePolicy,
            object? scope = null)
            => Task.FromResult(DomainAccessDecision.Allowed());

        public DomainAccessDecision Evaluate(
            ClaimsPrincipal principal,
            AccessPolicy? layerPolicy,
            AccessPolicy? servicePolicy,
            object? scope = null)
            => DomainAccessDecision.Allowed();
    }
}
