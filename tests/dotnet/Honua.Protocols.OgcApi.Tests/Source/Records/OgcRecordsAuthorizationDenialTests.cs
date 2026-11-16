// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Licensing.Domain;
using Honua.Core.Features.Security.Domain;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Helpers;
using Microsoft.AspNetCore.Hosting;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Api.Records;

[Collection("Database.OgcApiData")]
public sealed class OgcRecordsAuthorizationDenialTests
{
    private const string EntitledRole = "catalog-reader";
    private const string Referer = "https://ogcapi-authorization-proof.example/";

    private const int RestrictedLayerId = 1;

    private static readonly string RestrictedRecordId = $"layer:pub-layer-{RestrictedLayerId}";
    private static readonly string OpenRecordId = $"layer:pub-layer-{WebAppFixture.TestLayerId}";

    [IntegrationTest]
    [Protocol(TestProtocols.OgcApiRecords)]
    [Operation(Operations.Query)]
    [Endpoint("GET /ogc/records/collections/{collectionId}/items")]
    public async Task Records_Items_RestrictedResource_IsAbsentForUnentitledCallers()
    {
        await using var fixture = CreateFixture();
        await fixture.InitializeAsync();
        fixture.UpdateV2ResourceMetadata(
            RestrictedLayerId,
            accessPolicy: new AccessPolicy { AllowAnonymous = false, AllowedRoles = [EntitledRole] });

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

    private static WebAppFixture CreateFixture()
        => new WebAppFixture().WithTestLicense(HonuaEdition.Pro).ConfigureWebHost(builder =>
        {
            // Displace the F1 development-authentication bypass, which would otherwise make every
            // caller an admin and the access gate under test unreachable.
            builder.UseSetting("HONUA_DEV_AUTH", "false");
            builder.UseSetting("HONUA_ADMIN_PASSWORD", WebAppFixture.SharedAdminPassword);
        });

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
}
