// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Json;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Hosting;

namespace Honua.Server.Tests.Features.Identity;

/// <summary>
/// Entitlement-gate tests for the OIDC provider admin surface (#2997), mirroring the SAML/SCIM
/// shape in <see cref="IdentityEntitlementGateTests"/> (#2978). Single-provider OIDC
/// configuration is Pro (<c>identity.oidc</c> — "no SSO tax for one provider"); configuring a
/// second provider is Enterprise (<c>identity.oidc-multi-provider</c>). The claims-mapping
/// entitlement (<c>identity.claims-mapping</c>) has no admin DTO surface and is covered by
/// <see cref="Honua.Server.Tests.Infrastructure.Authentication.OidcClaimsMappingEntitlementTests"/>.
/// The token-validation pipeline itself is deliberately not gated (ADR-0024 amendment).
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.Admin)]
[Operation(Operations.IdentityManagement)]
public sealed class OidcEntitlementGateTests
{
    private const string ProvidersRoute = "/api/v1/admin/oidc/providers";

    private static WebAppFixture CreateFixture(string? devGrantEdition)
        => new WebAppFixture()
            .UseSeed("tests/seed/server.yaml")
            .ConfigureWebHost(builder =>
            {
                builder.UseEnvironment("Test");
                if (devGrantEdition is not null)
                {
                    builder.UseSetting("Licensing:DevGrantEdition", devGrantEdition);
                }
            });

    private static object CreateProviderPayload(string name) => new
    {
        name,
        providerType = "Generic",
        authority = "https://idp.example.com",
        clientId = $"client-{name}",
        enabled = true,
    };

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/oidc/providers")]
    public async Task ListProviders_WithoutEntitlement_Returns402WithEntitlementDetail()
    {
        var fixture = CreateFixture(devGrantEdition: null);
        await fixture.InitializeAsync();
        try
        {
            using var client = fixture.CreateAdminClient();
            using var response = await client.GetAsync(ProvidersRoute);

            Assert.Equal(HttpStatusCode.PaymentRequired, response.StatusCode);
            var body = await response.Content.ReadAsStringAsync();
            Assert.Contains("identity.oidc", body);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/oidc/providers")]
    [Endpoint("POST /api/v1/admin/oidc/providers")]
    public async Task Providers_WithProEntitlement_AllowsSingleProviderButBlocksSecond()
    {
        var fixture = CreateFixture(devGrantEdition: "Pro");
        await fixture.InitializeAsync();
        try
        {
            using var client = fixture.CreateAdminClient();

            // Listing and the first provider are covered by the Pro identity.oidc entitlement.
            using var listResponse = await client.GetAsync(ProvidersRoute);
            Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);

            using var firstCreate = await client.PostAsJsonAsync(ProvidersRoute, CreateProviderPayload("first"));
            Assert.Equal(HttpStatusCode.Created, firstCreate.StatusCode);

            // A second provider requires the Enterprise identity.oidc-multi-provider
            // entitlement; the 402 must name that key (not identity.oidc, which Pro has).
            using var secondCreate = await client.PostAsJsonAsync(ProvidersRoute, CreateProviderPayload("second"));
            Assert.Equal(HttpStatusCode.PaymentRequired, secondCreate.StatusCode);
            var body = await secondCreate.Content.ReadAsStringAsync();
            Assert.Contains("identity.oidc-multi-provider", body);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/oidc/providers")]
    public async Task Providers_WithEnterpriseEntitlement_AllowsMultipleProviders()
    {
        var fixture = CreateFixture(devGrantEdition: "Enterprise");
        await fixture.InitializeAsync();
        try
        {
            using var client = fixture.CreateAdminClient();

            using var firstCreate = await client.PostAsJsonAsync(ProvidersRoute, CreateProviderPayload("first"));
            Assert.Equal(HttpStatusCode.Created, firstCreate.StatusCode);

            using var secondCreate = await client.PostAsJsonAsync(ProvidersRoute, CreateProviderPayload("second"));
            Assert.Equal(HttpStatusCode.Created, secondCreate.StatusCode);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }
}
