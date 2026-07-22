// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Hosting;

namespace Honua.Server.Tests.Features.Identity;

/// <summary>
/// Entitlement-gate tests for the SAML and SCIM identity surfaces (#2978). Both previously
/// shipped ungated in every edition — catalog drift from ADR-0024, whose Identity
/// Governance tier places SAML and SCIM at Enterprise (<c>identity.saml</c> /
/// <c>identity.scim</c>), while Pro covers single-provider OIDC only. These tests prove
/// the gate in both directions — 402 with the standard
/// entitlement problem detail when unlicensed, and normal endpoint behavior once the
/// required edition is granted. The SAML/SCIM machinery itself is covered by
/// <see cref="SamlBridgeEndpointsTests"/> / <see cref="ScimProvisioningEndpointsTests"/>,
/// which run with the entitlement granted.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.Admin)]
[Operation(Operations.IdentityManagement)]
public sealed class IdentityEntitlementGateTests
{
    private const string ScimToken = "scim-gate-bearer-token";

    private static WebAppFixture CreateFixture(string? devGrantEdition)
        => new WebAppFixture()
            .UseSeed("tests/seed/server.yaml")
            .ConfigureWebHost(builder =>
            {
                builder.UseEnvironment("Test");
                builder.UseSetting("HONUA_DEV_AUTH", "false");
                builder.UseSetting("Saml:Enabled", "true");
                builder.UseSetting("Scim:BearerToken", ScimToken);
                if (devGrantEdition is not null)
                {
                    builder.UseSetting("Licensing:DevGrantEdition", devGrantEdition);
                }
            });

    [IntegrationTest]
    [Endpoint("GET /saml/metadata")]
    public async Task SamlMetadata_WithoutEntitlement_Returns402WithEntitlementDetail()
    {
        var fixture = CreateFixture(devGrantEdition: null);
        await fixture.InitializeAsync();
        try
        {
            using var client = fixture.CreateClient();
            using var response = await client.GetAsync("/saml/metadata");

            Assert.Equal(HttpStatusCode.PaymentRequired, response.StatusCode);
            var body = await response.Content.ReadAsStringAsync();
            Assert.Contains("identity.saml", body);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("GET /saml/metadata")]
    public async Task SamlMetadata_WithProEntitlement_StillReturns402()
    {
        // SAML is Enterprise (ADR-0024 Identity Governance), not Pro: the Pro "no SSO tax"
        // position covers single-provider OIDC only.
        var fixture = CreateFixture(devGrantEdition: "Pro");
        await fixture.InitializeAsync();
        try
        {
            using var client = fixture.CreateClient();
            using var response = await client.GetAsync("/saml/metadata");

            Assert.Equal(HttpStatusCode.PaymentRequired, response.StatusCode);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("GET /saml/metadata")]
    public async Task SamlMetadata_WithEnterpriseEntitlement_ReachesEndpoint()
    {
        var fixture = CreateFixture(devGrantEdition: "Enterprise");
        await fixture.InitializeAsync();
        try
        {
            using var client = fixture.CreateClient();
            using var response = await client.GetAsync("/saml/metadata");

            // The gate must not fire; the endpoint's own enabled/configured handling takes
            // over from here.
            Assert.NotEqual(HttpStatusCode.PaymentRequired, response.StatusCode);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("GET /scim/v2/Users")]
    public async Task ScimUsers_WithoutEntitlement_Returns402EvenWithValidBearer()
    {
        var fixture = CreateFixture(devGrantEdition: null);
        await fixture.InitializeAsync();
        try
        {
            using var client = fixture.CreateClient();
            using var request = new HttpRequestMessage(HttpMethod.Get, "/scim/v2/Users");
            request.Headers.Add("Authorization", $"Bearer {ScimToken}");
            using var response = await client.SendAsync(request);

            Assert.Equal(HttpStatusCode.PaymentRequired, response.StatusCode);
            var body = await response.Content.ReadAsStringAsync();
            Assert.Contains("identity.scim", body);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("GET /scim/v2/Users")]
    public async Task ScimUsers_WithProEntitlement_StillReturns402()
    {
        // SCIM is Enterprise, not Pro: a Pro license (which covers SAML/OIDC sign-in) must
        // not unlock directory provisioning.
        var fixture = CreateFixture(devGrantEdition: "Pro");
        await fixture.InitializeAsync();
        try
        {
            using var client = fixture.CreateClient();
            using var request = new HttpRequestMessage(HttpMethod.Get, "/scim/v2/Users");
            request.Headers.Add("Authorization", $"Bearer {ScimToken}");
            using var response = await client.SendAsync(request);

            Assert.Equal(HttpStatusCode.PaymentRequired, response.StatusCode);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("GET /scim/v2/ServiceProviderConfig")]
    public async Task ScimDiscovery_WithEnterpriseEntitlement_ReachesEndpoint()
    {
        var fixture = CreateFixture(devGrantEdition: "Enterprise");
        await fixture.InitializeAsync();
        try
        {
            using var client = fixture.CreateClient();
            using var request = new HttpRequestMessage(HttpMethod.Get, "/scim/v2/ServiceProviderConfig");
            request.Headers.Add("Authorization", $"Bearer {ScimToken}");
            using var response = await client.SendAsync(request);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }
}
