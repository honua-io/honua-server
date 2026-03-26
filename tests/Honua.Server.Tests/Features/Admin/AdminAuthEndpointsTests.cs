// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Honua.Server.Features.Admin.Models;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Hosting;

namespace Honua.Server.Tests.Features.Admin;

/// <summary>
/// Integration tests for the anonymous admin auth bootstrap and backend-assisted OIDC endpoints.
/// </summary>
[Collection("Database")]
[Protocol(Protocols.Admin)]
[Operation(Operations.Configuration)]
public sealed class AdminAuthEndpointsTests : IAsyncLifetime
{
    private const string TestTenantId = "11111111-1111-1111-1111-111111111111";
    private const string TestClientId = "22222222-2222-2222-2222-222222222222";

    private readonly WebAppFixture _fixture;
    private HttpClient _client = null!;

    public AdminAuthEndpointsTests()
    {
        _fixture = CreateBaseFixture();
    }

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        _client = _fixture.CreateClient();
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/auth/config")]
    public async Task GetAuthConfig_IsAnonymous_Returns200()
    {
        var response = await _client.GetAsync("/api/v1/admin/auth/config");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/auth/config")]
    public async Task GetAuthConfig_NoOidcConfigured_ReturnsEmptyProviders()
    {
        var response = await _client.GetAsync("/api/v1/admin/auth/config");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadFromJsonAsync(AdminAuthJsonContext.Default.AdminAuthConfigResponse);

        content.Should().NotBeNull();
        content!.OidcEnabled.Should().BeFalse();
        content.Providers.Should().BeEmpty();
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/auth/config")]
    public async Task GetAuthConfig_WithAzureAdConfigured_ReturnsSelectionMetadataOnly()
    {
        var oidcFixture = CreateAzureAdFixture();

        try
        {
            await oidcFixture.InitializeAsync();
            var oidcClient = oidcFixture.CreateClient();

            var response = await oidcClient.GetAsync("/api/v1/admin/auth/config");
            var raw = await response.Content.ReadAsStringAsync();
            var content = await response.Content.ReadFromJsonAsync(AdminAuthJsonContext.Default.AdminAuthConfigResponse);

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            content.Should().NotBeNull();
            content!.OidcEnabled.Should().BeTrue();
            content.Providers.Should().HaveCount(1);
            content.Providers[0].Key.Should().Be("azuread");
            content.Providers[0].DisplayName.Should().Be("Microsoft Entra ID");

            raw.Should().NotContain("authority", because: "anonymous bootstrap should not expose provider issuer details");
            raw.Should().NotContain("clientId", because: "anonymous bootstrap should not expose provider client IDs");
            raw.Should().NotContain(TestTenantId, because: "anonymous bootstrap should not expose tenant identifiers");
            raw.Should().NotContain(TestClientId, because: "anonymous bootstrap should not expose provider client IDs");
            raw.Should().NotContain("apiKeyFallbackEnabled", because: "fallback mode is derived from OIDC provider presence");
        }
        finally
        {
            await oidcFixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/auth/config")]
    public async Task GetAuthConfig_WithMultipleProviders_ReturnsProviderKeysAndNames()
    {
        var oidcFixture = CreateBaseFixture()
            .ConfigureWebHost(builder =>
            {
                builder.UseSetting("Oidc:Enabled", "true");
                builder.UseSetting("Oidc:RequireHttps", "true");
                builder.UseSetting("Oidc:TokenValidation:ValidateIssuer", "false");
                builder.UseSetting("Oidc:TokenValidation:ValidateAudience", "false");
                builder.UseSetting("Oidc:TokenValidation:ValidateIssuerSigningKey", "false");
                builder.UseSetting("Oidc:TokenValidation:SymmetricSigningKey", "test-key-at-least-32-characters-long-for-testing");
                builder.UseSetting("Oidc:AzureAd:Enabled", "true");
                builder.UseSetting("Oidc:AzureAd:TenantId", TestTenantId);
                builder.UseSetting("Oidc:AzureAd:ClientId", TestClientId);
                builder.UseSetting("Oidc:AzureAd:ClientSecret", "azure-secret-value-minimum-length");
                builder.UseSetting("Oidc:Generic:Enabled", "true");
                builder.UseSetting("Oidc:Generic:Authority", "https://auth.example.com");
                builder.UseSetting("Oidc:Generic:ClientId", "generic-client-id");
                builder.UseSetting("Oidc:Generic:DisplayName", "External Provider");
            });

        try
        {
            await oidcFixture.InitializeAsync();
            var oidcClient = oidcFixture.CreateClient();

            var response = await oidcClient.GetAsync("/api/v1/admin/auth/config");
            var content = await response.Content.ReadFromJsonAsync(AdminAuthJsonContext.Default.AdminAuthConfigResponse);

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            content.Should().NotBeNull();
            content!.Providers.Should().HaveCount(2);
            content.Providers.Should().Contain(p => p.Key == "azuread" && p.DisplayName == "Microsoft Entra ID");
            content.Providers.Should().Contain(p => p.Key == "oidc" && p.DisplayName == "External Provider");
        }
        finally
        {
            await oidcFixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/auth/providers/{providerKey}/authorize-url")]
    public async Task CreateAuthorizeUrl_NoOidcConfigured_ReturnsNotFound()
    {
        var response = await _client.PostAsJsonAsync(
            "/api/v1/admin/auth/providers/azuread/authorize-url",
            new
            {
                state = "state-123",
                codeChallenge = "challenge-123"
            });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/auth/providers/{providerKey}/authorize-url")]
    public async Task CreateAuthorizeUrl_WithMissingPkceValues_ReturnsBadRequest()
    {
        var oidcFixture = CreateAzureAdFixture();

        try
        {
            await oidcFixture.InitializeAsync();
            var oidcClient = oidcFixture.CreateClient();

            var response = await oidcClient.PostAsJsonAsync(
                "/api/v1/admin/auth/providers/azuread/authorize-url",
                new
                {
                    state = "",
                    codeChallenge = ""
                });

            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }
        finally
        {
            await oidcFixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/auth/providers/{providerKey}/token")]
    public async Task RequestToken_NoOidcConfigured_ReturnsNotFound()
    {
        var response = await _client.PostAsJsonAsync(
            "/api/v1/admin/auth/providers/azuread/token",
            new
            {
                grantType = "authorization_code",
                code = "code-123",
                codeVerifier = "verifier-123"
            });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/auth/providers/{providerKey}/token")]
    public async Task RequestToken_WithInvalidGrantType_ReturnsBadRequest()
    {
        var oidcFixture = CreateAzureAdFixture();

        try
        {
            await oidcFixture.InitializeAsync();
            var oidcClient = oidcFixture.CreateClient();

            var response = await oidcClient.PostAsJsonAsync(
                "/api/v1/admin/auth/providers/azuread/token",
                new
                {
                    grantType = "unsupported"
                });

            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }
        finally
        {
            await oidcFixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/auth/providers/{providerKey}/logout-url")]
    public async Task GetLogoutUrl_NoOidcConfigured_ReturnsNotFound()
    {
        var response = await _client.GetAsync("/api/v1/admin/auth/providers/azuread/logout-url");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    private static WebAppFixture CreateBaseFixture()
    {
        return new WebAppFixture()
            .UseSeed("tests/seed/server.yaml")
            .ConfigureWebHost(builder =>
            {
                builder.UseEnvironment("Test");
                builder.UseSetting("HONUA_DEV_AUTH", "false");
                builder.UseSetting("HONUA_ADMIN_PASSWORD", "test-admin-key");
            });
    }

    private static WebAppFixture CreateAzureAdFixture()
    {
        return CreateBaseFixture()
            .ConfigureWebHost(builder =>
            {
                builder.UseSetting("Oidc:Enabled", "true");
                builder.UseSetting("Oidc:RequireHttps", "true");
                builder.UseSetting("Oidc:AzureAd:Enabled", "true");
                builder.UseSetting("Oidc:AzureAd:TenantId", TestTenantId);
                builder.UseSetting("Oidc:AzureAd:ClientId", TestClientId);
                builder.UseSetting("Oidc:AzureAd:ClientSecret", "test-secret-value-minimum-length");
                builder.UseSetting("Oidc:TokenValidation:ValidateIssuer", "false");
                builder.UseSetting("Oidc:TokenValidation:ValidateAudience", "false");
                builder.UseSetting("Oidc:TokenValidation:ValidateIssuerSigningKey", "false");
                builder.UseSetting("Oidc:TokenValidation:SymmetricSigningKey", "test-key-at-least-32-characters-long-for-testing");
            });
    }
}
