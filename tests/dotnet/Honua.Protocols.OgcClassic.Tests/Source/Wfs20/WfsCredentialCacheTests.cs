// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Headers;
using System.Xml.Linq;
using FluentAssertions;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Licensing.Domain;
using Honua.Core.Features.Security.Domain;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Extensions;
using Honua.TestKit.Helpers;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Classic.Wfs20;

/// <summary>Credential-filtered WFS documents must never persist in a client's HTTP cache.</summary>
[Collection("Database")]
[Protocol(TestProtocols.Wfs20)]
public sealed class WfsCredentialCacheTests
{
    private const string ReadProtectedType = "test_layer";
    private const string WriteType = "related_test_layer_1";
    private const string Referer = "https://wfs-credential-cache.example/";

    [IntegrationTheory]
    [InlineData("1.0.0")]
    [InlineData("1.1.0")]
    [InlineData("2.0.0")]
    [Operation(Operations.SecurityTesting)]
    [Endpoint("GET /wfs")]
    [InterfaceOperation(TestProtocols.Wfs20, "GetCapabilities")]
    [InterfaceOperation(TestProtocols.Wfs20, "DescribeFeatureType")]
    [InterfaceOperation(TestProtocols.Wfs20, "GetFeature")]
    public async Task CredentialFilteredResponses_PreventClientStorageForEveryIdentity(string version)
    {
        await using var fixture = new WebAppFixture().WithTestLicense(HonuaEdition.Pro)
            .ConfigureWebHost(builder => builder
                .UseSetting("HONUA_DEV_AUTH", "false")
                .UseSetting("HONUA_DEV_AUTH_ALLOW_BYPASS", "false")
                .UseSetting("HONUA_ADMIN_PASSWORD", WebAppFixture.SharedAdminPassword));
        await fixture.InitializeAsync();
        fixture.UpdateV2ResourceMetadata(WebAppFixture.TestLayerId,
            accessPolicy: new AccessPolicy { AllowAnonymous = false, AllowedRoles = ["query"] });
        using var anonymous = fixture.CreateClient();
        using var invalid = fixture.CreateClient(client => client.DefaultRequestHeaders.Add("X-API-Key", "invalid"));
        using var adminWithoutQueryRole = fixture.CreateClient(client =>
            client.DefaultRequestHeaders.Add("X-API-Key", WebAppFixture.SharedAdminPassword));
        using var authorized = await CreateQueryClientAsync(fixture);
        foreach (var client in new[] { anonymous, invalid, adminWithoutQueryRole, authorized })
        {
            foreach (var operation in new[] { "GetCapabilities", "DescribeFeatureType", "GetFeature" })
            {
                using var response = await client.GetAsync(
                    $"/wfs?SERVICE=WFS&REQUEST={operation}&VERSION={version}&TYPENAME={WriteType}");
                var body = await response.Content.ReadAsStringAsync();
                response.StatusCode.Should().Be(HttpStatusCode.OK, body);
                response.Headers.CacheControl.Should().NotBeNull();
                response.Headers.CacheControl!.NoStore.Should().BeTrue(
                    "a cached anonymous response must not conceal newly authorized layers");
                if (operation == "GetCapabilities")
                {
                    var names = XDocument.Parse(body).Descendants()
                        .Where(element => element.Name.LocalName == "FeatureType")
                        .Select(element => element.Elements().Single(child => child.Name.LocalName == "Name").Value);
                    names.Should().Contain($"honua:{WriteType}");
                    if (client == authorized)
                    {
                        names.Should().Contain($"honua:{ReadProtectedType}");
                    }
                    else
                    {
                        names.Should().NotContain($"honua:{ReadProtectedType}");
                    }
                }
            }
        }
    }

    [IntegrationTheory]
    [InlineData("1.0.0")]
    [InlineData("1.1.0")]
    [InlineData("2.0.0")]
    [Operation(Operations.SecurityTesting)]
    [Endpoint("GET /wfs")]
    [InterfaceOperation(TestProtocols.Wfs20, "GetFeature")]
    public async Task ProtectedGetFeature_ChallengesMissingAndInvalidCredentialsWithoutChangingPublicOrUnknownTypes(
        string version)
    {
        await using var fixture = new WebAppFixture().WithTestLicense(HonuaEdition.Pro)
            .ConfigureWebHost(builder => builder
                .UseSetting("HONUA_DEV_AUTH", "false")
                .UseSetting("HONUA_DEV_AUTH_ALLOW_BYPASS", "false")
                .UseSetting("HONUA_ADMIN_PASSWORD", WebAppFixture.SharedAdminPassword));
        await fixture.InitializeAsync();
        fixture.UpdateV2ResourceMetadata(WebAppFixture.TestLayerId,
            accessPolicy: new AccessPolicy { AllowAnonymous = false, AllowedRoles = ["query"] });
        using var anonymous = fixture.CreateClient();
        using var invalid = fixture.CreateClient(client => client.DefaultRequestHeaders.Add("X-API-Key", "invalid"));
        using var adminWithoutQueryRole = fixture.CreateClient(client =>
            client.DefaultRequestHeaders.Add("X-API-Key", WebAppFixture.SharedAdminPassword));
        using var authorized = await CreateQueryClientAsync(fixture);

        string FeatureUrl(string type) =>
            $"/wfs?SERVICE=WFS&REQUEST=GetFeature&VERSION={version}&TYPENAME={type}";

        foreach (var client in new[] { anonymous, invalid })
        {
            using var denied = await client.GetAsync(FeatureUrl(ReadProtectedType));
            var deniedBody = await denied.Content.ReadAsStringAsync();
            denied.StatusCode.Should().Be(HttpStatusCode.Unauthorized, deniedBody);
            denied.Headers.WwwAuthenticate.Should().NotBeEmpty();
            deniedBody.Should().Contain("AccessDenied").And.NotContain("Test Feature");
            XDocument.Parse(deniedBody).Descendants()
                .Where(element => element.Name.LocalName is "member" or "featureMember" or "Point")
                .Should().BeEmpty("a denial must contain no protected geometry or rows");

            using var unknown = await client.GetAsync(FeatureUrl("truly_unknown_type"));
            var unknownBody = await unknown.Content.ReadAsStringAsync();
            unknown.StatusCode.Should().Be(HttpStatusCode.BadRequest, unknownBody);
            unknownBody.Should().Contain("InvalidParameterValue").And.NotContain("AccessDenied");
            unknown.Headers.WwwAuthenticate.Should().BeEmpty();

            using var publicRead = await client.GetAsync(FeatureUrl(WriteType));
            var publicBody = await publicRead.Content.ReadAsStringAsync();
            publicRead.StatusCode.Should().Be(HttpStatusCode.OK, publicBody);
            XDocument.Parse(publicBody).Descendants()
                .Count(element => element.Name.LocalName is "member" or "featureMember")
                .Should().Be(4);
        }

        using var excludedRead = await adminWithoutQueryRole.GetAsync(FeatureUrl(ReadProtectedType));
        var excludedBody = await excludedRead.Content.ReadAsStringAsync();
        excludedRead.StatusCode.Should().Be(HttpStatusCode.BadRequest, excludedBody);
        excludedBody.Should().Contain("InvalidParameterValue").And.NotContain("Test Feature");
        XDocument.Parse(excludedBody).Descendants()
            .Where(element => element.Name.LocalName is "member" or "featureMember" or "Point")
            .Should().BeEmpty("an admin key outside the query role must not expose protected features");

        using var protectedRead = await authorized.GetAsync(FeatureUrl(ReadProtectedType));
        var protectedBody = await protectedRead.Content.ReadAsStringAsync();
        protectedRead.StatusCode.Should().Be(HttpStatusCode.OK, protectedBody);
        XDocument.Parse(protectedBody).Descendants()
            .Count(element => element.Name.LocalName is "member" or "featureMember")
            .Should().Be(5);
    }

    private static async Task<HttpClient> CreateQueryClientAsync(WebAppFixture fixture)
    {
        var token = await fixture.GetService<IPortalTokenIssuer>().IssueAsync(
            new PortalTokenIssueRequest(
                "wfs-query-reader",
                "wfs-query-reader",
                TenantId: null,
                Roles: ["query"],
                PortalTokenClientType.Referer,
                Referer,
                DateTimeOffset.UtcNow.AddMinutes(30)),
            CancellationToken.None);
        return fixture.CreateClient(client =>
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
            client.DefaultRequestHeaders.Referrer = new Uri(Referer);
        });
    }
}
