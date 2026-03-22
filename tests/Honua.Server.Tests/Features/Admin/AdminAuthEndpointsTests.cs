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
/// Integration tests for the admin auth bootstrap endpoint.
/// </summary>
[Collection("Database")]
[Protocol(Protocols.Admin)]
[Operation(Operations.Configuration)]
public sealed class AdminAuthEndpointsTests : IAsyncLifetime
{
    // Valid test GUIDs for OIDC provider configuration
    private const string TestTenantId = "11111111-1111-1111-1111-111111111111";
    private const string TestClientId = "22222222-2222-2222-2222-222222222222";
    private const string AzureClientId = "33333333-3333-3333-3333-333333333333";

    private readonly WebAppFixture _fixture;
    private HttpClient _client = null!;

    public AdminAuthEndpointsTests()
    {
        _fixture = new WebAppFixture()
            .UseSeed("tests/seed/server.yaml")
            .ConfigureWebHost(builder =>
            {
                builder.UseEnvironment("Test");
                builder.UseSetting("HONUA_DEV_AUTH", "false");
                builder.UseSetting("HONUA_ADMIN_PASSWORD", "test-admin-key");
            });
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
        // No authentication headers sent
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
    public async Task GetAuthConfig_NoOidcConfigured_ApiKeyFallbackEnabled()
    {
        var response = await _client.GetAsync("/api/v1/admin/auth/config");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadFromJsonAsync(AdminAuthJsonContext.Default.AdminAuthConfigResponse);

        content.Should().NotBeNull();
        content!.ApiKeyFallbackEnabled.Should().BeTrue();
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/auth/config")]
    public async Task GetAuthConfig_NoSecretsExposed_ResponseHasNoSecretFields()
    {
        var response = await _client.GetAsync("/api/v1/admin/auth/config");
        var raw = await response.Content.ReadAsStringAsync();

        // Ensure no secret-like fields leak into the response
        raw.Should().NotContain("clientSecret", because: "client secrets must never be exposed to the browser");
        raw.Should().NotContain("client_secret");
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/auth/config")]
    public async Task GetAuthConfig_WithAzureAdConfigured_ReturnsSingleProvider()
    {
        var oidcFixture = new WebAppFixture()
            .UseSeed("tests/seed/server.yaml")
            .ConfigureWebHost(builder =>
            {
                builder.UseEnvironment("Test");
                builder.UseSetting("HONUA_DEV_AUTH", "false");
                builder.UseSetting("HONUA_ADMIN_PASSWORD", "test-admin-key");
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

        try
        {
            await oidcFixture.InitializeAsync();
            var oidcClient = oidcFixture.CreateClient();

            var response = await oidcClient.GetAsync("/api/v1/admin/auth/config");
            response.StatusCode.Should().Be(HttpStatusCode.OK);

            var content = await response.Content.ReadFromJsonAsync(AdminAuthJsonContext.Default.AdminAuthConfigResponse);
            content.Should().NotBeNull();
            content!.OidcEnabled.Should().BeTrue();
            content.Providers.Should().HaveCount(1);
            content.Providers[0].Key.Should().Be("azuread");
            content.Providers[0].DisplayName.Should().Be("Microsoft Entra ID");
            content.Providers[0].ClientId.Should().Be(TestClientId);
            content.Providers[0].Authority.Should().Contain(TestTenantId);
            content.Providers[0].SupportsLogout.Should().BeTrue();
            content.ApiKeyFallbackEnabled.Should().BeFalse();
        }
        finally
        {
            await oidcFixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/auth/config")]
    public async Task GetAuthConfig_WithMultipleProviders_ReturnsAllProviders()
    {
        var oidcFixture = new WebAppFixture()
            .UseSeed("tests/seed/server.yaml")
            .ConfigureWebHost(builder =>
            {
                builder.UseEnvironment("Test");
                builder.UseSetting("HONUA_DEV_AUTH", "false");
                builder.UseSetting("HONUA_ADMIN_PASSWORD", "test-admin-key");
                builder.UseSetting("Oidc:Enabled", "true");
                builder.UseSetting("Oidc:RequireHttps", "true");
                builder.UseSetting("Oidc:TokenValidation:ValidateIssuer", "false");
                builder.UseSetting("Oidc:TokenValidation:ValidateAudience", "false");
                builder.UseSetting("Oidc:TokenValidation:ValidateIssuerSigningKey", "false");
                builder.UseSetting("Oidc:TokenValidation:SymmetricSigningKey", "test-key-at-least-32-characters-long-for-testing");
                // Azure AD
                builder.UseSetting("Oidc:AzureAd:Enabled", "true");
                builder.UseSetting("Oidc:AzureAd:TenantId", TestTenantId);
                builder.UseSetting("Oidc:AzureAd:ClientId", AzureClientId);
                builder.UseSetting("Oidc:AzureAd:ClientSecret", "azure-secret-value-minimum-length");
                // Generic OIDC (e.g. Okta or Auth0)
                builder.UseSetting("Oidc:Generic:Enabled", "true");
                builder.UseSetting("Oidc:Generic:Authority", "https://auth.example.com");
                builder.UseSetting("Oidc:Generic:ClientId", "generic-client-id");
                builder.UseSetting("Oidc:Generic:DisplayName", "Okta");
            });

        try
        {
            await oidcFixture.InitializeAsync();
            var oidcClient = oidcFixture.CreateClient();

            var response = await oidcClient.GetAsync("/api/v1/admin/auth/config");
            response.StatusCode.Should().Be(HttpStatusCode.OK);

            var content = await response.Content.ReadFromJsonAsync(AdminAuthJsonContext.Default.AdminAuthConfigResponse);
            content.Should().NotBeNull();
            content!.OidcEnabled.Should().BeTrue();
            content.Providers.Should().HaveCount(2);
            content.Providers.Should().Contain(p => p.Key == "azuread");
            content.Providers.Should().Contain(p => p.Key == "oidc");
        }
        finally
        {
            await oidcFixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/auth/config")]
    public async Task GetAuthConfig_OktaProvider_ReturnsCorrectAuthority()
    {
        var oidcFixture = new WebAppFixture()
            .UseSeed("tests/seed/server.yaml")
            .ConfigureWebHost(builder =>
            {
                builder.UseEnvironment("Test");
                builder.UseSetting("HONUA_DEV_AUTH", "false");
                builder.UseSetting("HONUA_ADMIN_PASSWORD", "test-admin-key");
                builder.UseSetting("Oidc:Enabled", "true");
                builder.UseSetting("Oidc:RequireHttps", "true");
                builder.UseSetting("Oidc:TokenValidation:ValidateIssuer", "false");
                builder.UseSetting("Oidc:TokenValidation:ValidateAudience", "false");
                builder.UseSetting("Oidc:TokenValidation:ValidateIssuerSigningKey", "false");
                builder.UseSetting("Oidc:TokenValidation:SymmetricSigningKey", "test-key-at-least-32-characters-long-for-testing");
                builder.UseSetting("Oidc:Okta:Enabled", "true");
                builder.UseSetting("Oidc:Okta:OrgUrl", "dev-12345.okta.com");
                builder.UseSetting("Oidc:Okta:ClientId", "okta-client-id");
            });

        try
        {
            await oidcFixture.InitializeAsync();
            var oidcClient = oidcFixture.CreateClient();

            var response = await oidcClient.GetAsync("/api/v1/admin/auth/config");
            response.StatusCode.Should().Be(HttpStatusCode.OK);

            var content = await response.Content.ReadFromJsonAsync(AdminAuthJsonContext.Default.AdminAuthConfigResponse);
            content.Should().NotBeNull();
            content!.OidcEnabled.Should().BeTrue();
            content.Providers.Should().HaveCount(1);
            content.Providers[0].Key.Should().Be("okta");
            content.Providers[0].DisplayName.Should().Be("Okta");
            content.Providers[0].Authority.Should().Be("https://dev-12345.okta.com/oauth2/default");
            content.Providers[0].ClientId.Should().Be("okta-client-id");
            content.Providers[0].SupportsLogout.Should().BeTrue();
        }
        finally
        {
            await oidcFixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/auth/config")]
    public async Task GetAuthConfig_Auth0Provider_ReturnsCorrectAuthority()
    {
        var oidcFixture = new WebAppFixture()
            .UseSeed("tests/seed/server.yaml")
            .ConfigureWebHost(builder =>
            {
                builder.UseEnvironment("Test");
                builder.UseSetting("HONUA_DEV_AUTH", "false");
                builder.UseSetting("HONUA_ADMIN_PASSWORD", "test-admin-key");
                builder.UseSetting("Oidc:Enabled", "true");
                builder.UseSetting("Oidc:RequireHttps", "true");
                builder.UseSetting("Oidc:TokenValidation:ValidateIssuer", "false");
                builder.UseSetting("Oidc:TokenValidation:ValidateAudience", "false");
                builder.UseSetting("Oidc:TokenValidation:ValidateIssuerSigningKey", "false");
                builder.UseSetting("Oidc:TokenValidation:SymmetricSigningKey", "test-key-at-least-32-characters-long-for-testing");
                builder.UseSetting("Oidc:Auth0:Enabled", "true");
                builder.UseSetting("Oidc:Auth0:Domain", "myapp.us.auth0.com");
                builder.UseSetting("Oidc:Auth0:ClientId", "auth0-client-id");
            });

        try
        {
            await oidcFixture.InitializeAsync();
            var oidcClient = oidcFixture.CreateClient();

            var response = await oidcClient.GetAsync("/api/v1/admin/auth/config");
            response.StatusCode.Should().Be(HttpStatusCode.OK);

            var content = await response.Content.ReadFromJsonAsync(AdminAuthJsonContext.Default.AdminAuthConfigResponse);
            content.Should().NotBeNull();
            content!.OidcEnabled.Should().BeTrue();
            content.Providers.Should().HaveCount(1);
            content.Providers[0].Key.Should().Be("auth0");
            content.Providers[0].DisplayName.Should().Be("Auth0");
            content.Providers[0].Authority.Should().Be("https://myapp.us.auth0.com/");
            content.Providers[0].ClientId.Should().Be("auth0-client-id");
        }
        finally
        {
            await oidcFixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/auth/config")]
    public async Task GetAuthConfig_GoogleProvider_SupportsLogoutIsFalse()
    {
        var oidcFixture = new WebAppFixture()
            .UseSeed("tests/seed/server.yaml")
            .ConfigureWebHost(builder =>
            {
                builder.UseEnvironment("Test");
                builder.UseSetting("HONUA_DEV_AUTH", "false");
                builder.UseSetting("HONUA_ADMIN_PASSWORD", "test-admin-key");
                builder.UseSetting("Oidc:Enabled", "true");
                builder.UseSetting("Oidc:RequireHttps", "true");
                builder.UseSetting("Oidc:TokenValidation:ValidateIssuer", "false");
                builder.UseSetting("Oidc:TokenValidation:ValidateAudience", "false");
                builder.UseSetting("Oidc:TokenValidation:ValidateIssuerSigningKey", "false");
                builder.UseSetting("Oidc:TokenValidation:SymmetricSigningKey", "test-key-at-least-32-characters-long-for-testing");
                builder.UseSetting("Oidc:Google:Enabled", "true");
                builder.UseSetting("Oidc:Google:ClientId", "google-client-id.apps.googleusercontent.com");
                builder.UseSetting("Oidc:Google:ClientSecret", "google-secret-value-test");
            });

        try
        {
            await oidcFixture.InitializeAsync();
            var oidcClient = oidcFixture.CreateClient();

            var response = await oidcClient.GetAsync("/api/v1/admin/auth/config");
            var content = await response.Content.ReadFromJsonAsync(AdminAuthJsonContext.Default.AdminAuthConfigResponse);

            content.Should().NotBeNull();
            content!.Providers.Should().HaveCount(1);
            content.Providers[0].Key.Should().Be("google");
            // Google does not support RP-initiated logout
            content.Providers[0].SupportsLogout.Should().BeFalse();
        }
        finally
        {
            await oidcFixture.DisposeAsync();
        }
    }
}
