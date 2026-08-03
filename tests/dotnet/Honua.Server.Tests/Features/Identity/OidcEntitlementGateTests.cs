// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
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
    [Endpoint("POST /api/v1/admin/oidc/providers/{id}/test")]
    public async Task TestProvider_WithTrailingSlash_IsNotBlockedByMultiProviderGate()
    {
        // The connectivity-test route creates nothing, so the Enterprise multi-provider gate must
        // never fire for it — including for the equally valid trailing-slash form, which routing
        // still matches. Identifying the create route by a "/test" path suffix classified
        // `…/providers/{id}/test/` as a create and 402'd a valid test request.
        var fixture = CreateFixture(devGrantEdition: "Pro");
        await fixture.InitializeAsync();
        try
        {
            using var client = fixture.CreateAdminClient();

            using var create = await client.PostAsJsonAsync(ProvidersRoute, CreateProviderPayload("only"));
            Assert.Equal(HttpStatusCode.Created, create.StatusCode);
            var providerId = await ReadProviderIdAsync(create);

            // Sanity check: a create now WOULD be blocked, so a 402 below could only come from the
            // create/test misclassification and not from an already-open gate.
            using var secondCreate = await client.PostAsJsonAsync(ProvidersRoute, CreateProviderPayload("second"));
            Assert.Equal(HttpStatusCode.PaymentRequired, secondCreate.StatusCode);

            foreach (var testRoute in new[]
                     {
                         $"{ProvidersRoute}/{providerId}/test",
                         $"{ProvidersRoute}/{providerId}/test/",
                     })
            {
                using var response = await client.PostAsync(testRoute, content: null);
                var body = await response.Content.ReadAsStringAsync();
                Assert.True(
                    response.StatusCode == HttpStatusCode.OK,
                    $"POST {testRoute} should reach the connectivity test, got {(int)response.StatusCode}: {body}");
            }
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/oidc/providers")]
    public async Task Providers_ConcurrentFirstCreatesAtPro_StillAdmitOnlyOneProvider()
    {
        // The provider-count check has to be atomic with the create: concurrent Pro requests could
        // otherwise all observe an empty store, all pass the preflight, and all be accepted with
        // distinct generated IDs — bypassing the Enterprise multi-provider entitlement entirely.
        var fixture = CreateFixture(devGrantEdition: "Pro");
        await fixture.InitializeAsync();
        try
        {
            using var client = fixture.CreateAdminClient();

            var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(index =>
                client.PostAsJsonAsync(ProvidersRoute, CreateProviderPayload($"racer-{index}"))));

            var created = responses.Count(response => response.StatusCode == HttpStatusCode.Created);
            var blocked = responses.Count(response => response.StatusCode == HttpStatusCode.PaymentRequired);
            foreach (var response in responses)
            {
                response.Dispose();
            }

            Assert.Equal(1, created);
            Assert.Equal(responses.Length - 1, blocked);

            using var listResponse = await client.GetAsync(ProvidersRoute);
            var listBody = await listResponse.Content.ReadAsStringAsync();
            using var document = JsonDocument.Parse(listBody);
            Assert.Equal(
                1,
                document.RootElement.GetProperty("data").GetArrayLength());
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    private static async Task<Guid> ReadProviderIdAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("data").GetProperty("providerId").GetGuid();
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
