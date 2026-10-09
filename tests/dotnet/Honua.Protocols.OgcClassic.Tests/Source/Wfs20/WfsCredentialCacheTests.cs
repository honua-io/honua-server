// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Xml.Linq;
using FluentAssertions;
using Honua.Core.Features.Licensing.Domain;
using Honua.Core.Features.Security.Domain;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Helpers;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Classic.Wfs20;

/// <summary>Credential-filtered WFS documents must never persist in a client's HTTP cache.</summary>
[Collection("Database")]
[Protocol(TestProtocols.Wfs20)]
public sealed class WfsCredentialCacheTests
{
    private const string ReadProtectedType = "test_layer";
    private const string WriteType = "related_test_layer_1";

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
                .UseSetting("HONUA_ADMIN_PASSWORD", WebAppFixture.SharedAdminPassword));
        await fixture.InitializeAsync();
        fixture.UpdateV2ResourceMetadata(WebAppFixture.TestLayerId,
            accessPolicy: new AccessPolicy { AllowAnonymous = false, AllowedRoles = ["query"] });
        using var anonymous = fixture.CreateClient();
        using var invalid = fixture.CreateClient(client => client.DefaultRequestHeaders.Add("X-API-Key", "invalid"));
        using var authorized = fixture.CreateClient(client =>
            client.DefaultRequestHeaders.Add("X-API-Key", WebAppFixture.SharedAdminPassword));
        foreach (var client in new[] { anonymous, invalid, authorized })
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

}
