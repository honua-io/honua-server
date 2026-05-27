// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Json;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using FluentAssertions.Execution;
using FluentAssertions;
using Honua.Server.Features.Admin.Models;
using Honua.Server.Features.Infrastructure.Authentication;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.AspNetCore.Hosting;
using Microsoft.IdentityModel.Tokens;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Features.Admin;

/// <summary>
/// Integration tests for the anonymous admin auth bootstrap and backend-assisted OIDC endpoints.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.Admin)]
[Operation(Operations.Configuration)]
public sealed class AdminAuthEndpointsTests : IAsyncLifetime
{
    private const string TestTenantId = "11111111-1111-1111-1111-111111111111";
    private const string TestClientId = "22222222-2222-2222-2222-222222222222";
    private const string TestSigningKey = "test-key-at-least-32-characters-long-for-testing";
    private const string TestOidcIssuer = "https://auth.example.com";
    private const string TestOidcAudience = "generic-client-id";
    private static readonly RsaSecurityKey _oidcSigningKey = CreateOidcSigningKey();
    private static readonly SigningCredentials _oidcSigningCredentials = new(_oidcSigningKey, SecurityAlgorithms.RsaSha256);

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
    public async Task GetAuthConfig_AfterAdminTrustProfileCreate_ReturnsActiveTrustStoreHints()
    {
        var mtlsFixture = CreateBaseFixture()
            .ConfigureWebHost(builder =>
            {
                builder.UseSetting("Authentication:ClientCertificates:Mode", "Optional");
                builder.UseSetting("Authentication:ClientCertificates:EnvironmentId", "prod");
            });

        try
        {
            await mtlsFixture.InitializeAsync();
            using var adminClient = mtlsFixture.CreateClient(client =>
                client.DefaultRequestHeaders.Add("X-API-Key", "test-admin-key"));
            var anonymousClient = mtlsFixture.CreateClient();

            var createResponse = await adminClient.PostAsJsonAsync(
                "/api/v1/admin/security/client-certificates/profiles",
                new UpsertClientCertificateTrustProfileRequest
                {
                    ProfileId = "runtime-prod-native",
                    EnvironmentId = "prod",
                    DisplayName = "Runtime production operators",
                    AcceptedIssuerSubjects = ["CN=Runtime Trust Issuer"],
                    RequireChainTrust = true,
                    ExpirationWarningThresholdDays = 12
                });

            createResponse.StatusCode.Should().Be(HttpStatusCode.Created);

            var response = await anonymousClient.GetAsync("/api/v1/admin/auth/config");
            var content = await response.Content.ReadFromJsonAsync(AdminAuthJsonContext.Default.AdminAuthConfigResponse);

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            content.Should().NotBeNull();
            content!.ClientCertificates.Should().NotBeNull();
            content.ClientCertificates!.Mode.Should().Be("Optional");
            // Trust profiles canonicalize subjects/thumbprints (uppercase, no spaces) at storage
            // time so matching is deterministic; the bootstrap hint mirrors the stored form.
            content.ClientCertificates.AcceptedIssuerSubjects.Should().Contain("CN=RUNTIMETRUSTISSUER");
            content.ClientCertificates.ExpirationWarningThresholdDays.Should().Be(12);
        }
        finally
        {
            await mtlsFixture.DisposeAsync();
        }
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
    [Endpoint("POST /api/v1/admin/auth/logout")]
    public async Task Logout_IsAnonymous_Returns200()
    {
        var response = await _client.PostAsJsonAsync("/api/v1/admin/auth/logout", new object());

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/auth/providers/{providerKey}/authorize-url")]
    public async Task CreateAuthorizeUrl_NoOidcConfigured_ReturnsNotFound()
    {
        var response = await _client.PostAsJsonAsync(
            "/api/v1/admin/auth/providers/azuread/authorize-url",
            new { });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/auth/providers/{providerKey}/authorize-url")]
    public async Task CreateAuthorizeUrl_SetsHttpOnlyPendingSessionCookie()
    {
        using var stubFactory = new StubHttpClientFactory(
            "https://auth.example.com/.well-known/openid-configuration",
            new StubOidcEndpoints(
                "https://auth.example.com/authorize",
                "https://auth.example.com/token",
                "https://auth.example.com/logout"));

        var oidcFixture = CreateGenericOidcFixture(stubFactory);

        try
        {
            await oidcFixture.InitializeAsync();
            var oidcClient = oidcFixture.CreateClient();

            var response = await oidcClient.PostAsJsonAsync(
                "/api/v1/admin/auth/providers/oidc/authorize-url",
                new { });

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            response.Headers.TryGetValues("Set-Cookie", out var setCookieValues).Should().BeTrue();
            setCookieValues.Should().NotBeNull();
            var loginCookies = setCookieValues!
                .Where(v => v.Contains("honua_admin_login=", StringComparison.Ordinal))
                .ToArray();

            loginCookies.Should().ContainSingle();
            loginCookies.Should().OnlyContain(v => v.Contains("HttpOnly", StringComparison.OrdinalIgnoreCase));
            loginCookies.Should().OnlyContain(v => v.Contains("SameSite=Strict", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            await oidcFixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/auth/providers/{providerKey}/authorize-url")]
    public async Task CreateAuthorizeUrl_InProductionWithoutConfiguredPublicBaseUrl_ReturnsServiceUnavailable()
    {
        using var stubFactory = new StubHttpClientFactory(
            "https://auth.example.com/.well-known/openid-configuration",
            new StubOidcEndpoints(
                "https://auth.example.com/authorize",
                "https://auth.example.com/token",
                "https://auth.example.com/logout"));

        var oidcFixture = CreateGenericOidcFixture(stubFactory)
            .ConfigureWebHost(builder =>
            {
                builder.UseEnvironment("Production");
                builder.UseSetting("HONUA_ADMIN_PASSWORD", "Production-Test-Password1!");
            });

        var initialized = false;
        try
        {
            await oidcFixture.InitializeAsync();
            initialized = true;
            var oidcClient = oidcFixture.CreateClient();

            var response = await oidcClient.PostAsJsonAsync(
                "/api/v1/admin/auth/providers/oidc/authorize-url",
                new { });

            response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
            var body = await response.Content.ReadAsStringAsync();
            body.Should().Contain("Public:BaseUrl");
        }
        finally
        {
            if (initialized)
            {
                await oidcFixture.DisposeAsync();
            }
        }
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/auth/providers/{providerKey}/logout-url")]
    public async Task GetLogoutUrl_InProductionWithoutConfiguredPublicBaseUrl_ReturnsServiceUnavailable()
    {
        using var stubFactory = new StubHttpClientFactory(
            "https://auth.example.com/.well-known/openid-configuration",
            new StubOidcEndpoints(
                "https://auth.example.com/authorize",
                "https://auth.example.com/token",
                "https://auth.example.com/logout"));

        var oidcFixture = CreateGenericOidcFixture(stubFactory)
            .ConfigureWebHost(builder =>
            {
                builder.UseEnvironment("Production");
                builder.UseSetting("HONUA_ADMIN_PASSWORD", "Production-Test-Password1!");
            });

        var initialized = false;
        try
        {
            await oidcFixture.InitializeAsync();
            initialized = true;
            var oidcClient = oidcFixture.CreateClient();

            var response = await oidcClient.GetAsync("/api/v1/admin/auth/providers/oidc/logout-url?idTokenHint=id-token");

            response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
            var body = await response.Content.ReadAsStringAsync();
            body.Should().Contain("Public:BaseUrl");
        }
        finally
        {
            if (initialized)
            {
                await oidcFixture.DisposeAsync();
            }
        }
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/auth/logout")]
    public async Task Logout_InProductionWithoutConfiguredPublicBaseUrl_ReturnsServiceUnavailableAndClearsSession()
    {
        using var stubFactory = new StubHttpClientFactory(
            "https://auth.example.com/.well-known/openid-configuration",
            new StubOidcEndpoints(
                "https://auth.example.com/authorize",
                "https://auth.example.com/token",
                "https://auth.example.com/logout"));

        var oidcFixture = CreateGenericOidcFixture(stubFactory)
            .ConfigureWebHost(builder =>
            {
                builder.UseEnvironment("Production");
                builder.UseSetting("HONUA_ADMIN_PASSWORD", "Production-Test-Password1!");
            });

        var initialized = false;
        try
        {
            await oidcFixture.InitializeAsync();
            initialized = true;

            string sessionId;
            await using (var scope = oidcFixture.Services.CreateAsyncScope())
            {
                var sessionStore = scope.ServiceProvider.GetRequiredService<AdminAuthSessionStore>();
                sessionId = await sessionStore.CreateAuthenticatedSessionAsync(
                    "oidc",
                    "access-token",
                    "id-token",
                    [new AdminAuthSessionClaim { Type = ClaimTypes.NameIdentifier, Value = "user-123" }],
                    DateTimeOffset.UtcNow.AddMinutes(5),
                    CancellationToken.None);
            }

            var oidcClient = oidcFixture.CreateClient(client =>
                client.DefaultRequestHeaders.Add("Cookie", $"{AdminAuthSessionStore.AuthSessionCookieName}={sessionId}"));

            var response = await oidcClient.PostAsJsonAsync("/api/v1/admin/auth/logout", new { });

            response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
            var body = await response.Content.ReadAsStringAsync();
            body.Should().Contain("Public:BaseUrl");

            await using var verificationScope = oidcFixture.Services.CreateAsyncScope();
            var verificationStore = verificationScope.ServiceProvider.GetRequiredService<AdminAuthSessionStore>();
            var persistedSession = await verificationStore.GetAuthenticatedSessionAsync(sessionId, CancellationToken.None);
            persistedSession.Should().BeNull();
        }
        finally
        {
            if (initialized)
            {
                await oidcFixture.DisposeAsync();
            }
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
                state = "state-123"
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
    [Endpoint("POST /api/v1/admin/auth/providers/{providerKey}/token")]
    public async Task RequestToken_WithoutPkceCookies_ReturnsBadRequest()
    {
        using var stubFactory = new StubHttpClientFactory(
            "https://auth.example.com/.well-known/openid-configuration",
            new StubOidcEndpoints(
                "https://auth.example.com/authorize",
                "https://auth.example.com/token",
                "https://auth.example.com/logout"));

        var oidcFixture = CreateGenericOidcFixture(stubFactory);

        try
        {
            await oidcFixture.InitializeAsync();
            var oidcClient = oidcFixture.CreateClient();

            var response = await oidcClient.PostAsJsonAsync(
                "/api/v1/admin/auth/providers/oidc/token",
                new
                {
                    grantType = "authorization_code",
                    code = "code-123",
                    state = "state-123"
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
    public async Task RequestToken_WithMismatchedPkceState_ReturnsBadRequest()
    {
        using var stubFactory = new StubHttpClientFactory(
            "https://auth.example.com/.well-known/openid-configuration",
            new StubOidcEndpoints(
                "https://auth.example.com/authorize",
                "https://auth.example.com/token",
                "https://auth.example.com/logout"));

        var oidcFixture = CreateGenericOidcFixture(stubFactory);

        try
        {
            await oidcFixture.InitializeAsync();
            var oidcClient = oidcFixture.CreateClient();

            var authorizeResponse = await oidcClient.PostAsJsonAsync(
                "/api/v1/admin/auth/providers/oidc/authorize-url",
                new { });
            authorizeResponse.StatusCode.Should().Be(HttpStatusCode.OK);

            var response = await oidcClient.PostAsJsonAsync(
                "/api/v1/admin/auth/providers/oidc/token",
                new
                {
                    grantType = "authorization_code",
                    code = "code-123",
                    state = "wrong-state"
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

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/auth/providers/{providerKey}/authorize-url")]
    public async Task CreateAuthorizeUrl_UsesConfiguredPublicBaseUrlForRedirectUri()
    {
        using var stubFactory = new StubHttpClientFactory(
            "https://auth.example.com/.well-known/openid-configuration",
            new StubOidcEndpoints(
                "https://auth.example.com/authorize",
                "https://auth.example.com/token",
                "https://auth.example.com/logout"));

        var oidcFixture = CreateGenericOidcFixture(stubFactory)
            .ConfigureWebHost(builder => builder.UseSetting("Public:BaseUrl", "https://public.example.com/honua"));

        try
        {
            await oidcFixture.InitializeAsync();
            var oidcClient = oidcFixture.CreateClient();

            var response = await oidcClient.PostAsJsonAsync(
                "/api/v1/admin/auth/providers/oidc/authorize-url",
                new
                {
                    state = "state-123",
                    codeChallenge = "challenge-123"
                });

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var content = await response.Content.ReadFromJsonAsync(AdminAuthJsonContext.Default.AdminAuthAuthorizeUrlResponse);
            content.Should().NotBeNull();

            var authorizeUri = new Uri(content!.AuthorizeUrl);
            var parameters = QueryHelpers.ParseQuery(authorizeUri.Query);
            parameters["redirect_uri"].ToString().Should().Be("https://public.example.com/honua/admin/auth/callback");
        }
        finally
        {
            await oidcFixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/auth/providers/{providerKey}/token")]
    public async Task RequestToken_UsesConfiguredPublicBaseUrlForRedirectUri()
    {
        using var stubFactory = new StubHttpClientFactory(
            "https://auth.example.com/.well-known/openid-configuration",
            new StubOidcEndpoints(
                "https://auth.example.com/authorize",
                "https://auth.example.com/token",
                "https://auth.example.com/logout"));

        var oidcFixture = CreateGenericOidcFixture(stubFactory)
            .ConfigureWebHost(builder => builder.UseSetting("Public:BaseUrl", "https://public.example.com/honua"));

        try
        {
            await oidcFixture.InitializeAsync();
            var oidcClient = oidcFixture.CreateClient();

            var authorizeResponse = await oidcClient.PostAsJsonAsync(
                "/api/v1/admin/auth/providers/oidc/authorize-url",
                new { });
            authorizeResponse.StatusCode.Should().Be(HttpStatusCode.OK);
            var authorizeContent = await authorizeResponse.Content.ReadFromJsonAsync(AdminAuthJsonContext.Default.AdminAuthAuthorizeUrlResponse);
            authorizeContent.Should().NotBeNull();
            var authorizeUri = new Uri(authorizeContent!.AuthorizeUrl);
            var authorizeParameters = QueryHelpers.ParseQuery(authorizeUri.Query);

            var response = await oidcClient.PostAsJsonAsync(
                "/api/v1/admin/auth/providers/oidc/token",
                new
                {
                    grantType = "authorization_code",
                    code = "code-123",
                    state = authorizeParameters["state"].ToString()
                });

            response.StatusCode.Should().Be(HttpStatusCode.NoContent);
            stubFactory.LastTokenFormValues.Should().ContainKey("redirect_uri");
            stubFactory.LastTokenFormValues["redirect_uri"].Should().Be("https://public.example.com/honua/admin/auth/callback");
            stubFactory.LastTokenFormValues.Should().ContainKey("code_verifier");

            var raw = await response.Content.ReadAsStringAsync();
            raw.Should().BeEmpty();
            raw.Should().NotContain("access_token", because: "the hosted admin UI now uses a server-managed session");
            raw.Should().NotContain("refresh_token", because: "refresh tokens must remain server-side for the hosted admin UI");

            response.Headers.TryGetValues("Set-Cookie", out var setCookieValues).Should().BeTrue();
            setCookieValues.Should().NotBeNull();
            setCookieValues.Should().Contain(v => v.Contains("honua_admin_session=", StringComparison.Ordinal));
        }
        finally
        {
            await oidcFixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/auth/providers/{providerKey}/token")]
    public async Task RequestToken_RefreshGrant_ReturnsBadRequest()
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
                    grantType = "refresh_token"
                });

            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }
        finally
        {
            await oidcFixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/auth/session")]
    public async Task GetSession_AfterTokenExchange_ReturnsProjectedClaimsWithoutTokens()
    {
        using var stubFactory = new StubHttpClientFactory(
            "https://auth.example.com/.well-known/openid-configuration",
            new StubOidcEndpoints(
                "https://auth.example.com/authorize",
                "https://auth.example.com/token",
                "https://auth.example.com/logout"));

        var oidcFixture = CreateGenericOidcFixture(stubFactory);

        try
        {
            await oidcFixture.InitializeAsync();
            var oidcClient = oidcFixture.CreateClient();

            var authorizeResponse = await oidcClient.PostAsJsonAsync(
                "/api/v1/admin/auth/providers/oidc/authorize-url",
                new { });
            var authorizeContent = await authorizeResponse.Content.ReadFromJsonAsync(AdminAuthJsonContext.Default.AdminAuthAuthorizeUrlResponse);
            var authorizeUri = new Uri(authorizeContent!.AuthorizeUrl);
            var authorizeParameters = QueryHelpers.ParseQuery(authorizeUri.Query);

            var tokenResponse = await oidcClient.PostAsJsonAsync(
                "/api/v1/admin/auth/providers/oidc/token",
                new
                {
                    grantType = "authorization_code",
                    code = "code-123",
                    state = authorizeParameters["state"].ToString()
                });

            tokenResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);

            var sessionResponse = await oidcClient.GetAsync("/api/v1/admin/auth/session");
            sessionResponse.StatusCode.Should().Be(HttpStatusCode.OK);

            var session = await sessionResponse.Content.ReadFromJsonAsync(AdminAuthJsonContext.Default.AdminAuthSessionResponse);
            session.Should().NotBeNull();
            session!.IsAuthenticated.Should().BeTrue();
            session.ProviderKey.Should().Be("oidc");
            session.Claims.Should().Contain(c => c.Type == "name" && c.Value == "Test Admin");
            session.Claims.Should().Contain(c => c.Type == ClaimTypes.Role && c.Value == "admin");

            var raw = await sessionResponse.Content.ReadAsStringAsync();
            raw.Should().NotContain("access_token");
            raw.Should().NotContain("refresh_token");
            raw.Should().NotContain("id_token");
        }
        finally
        {
            await oidcFixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/auth/session")]
    public async Task GetSession_WithOpaqueAccessTokenAndJwtIdToken_ReturnsProjectedClaims()
    {
        using var stubFactory = new StubHttpClientFactory(
            "https://auth.example.com/.well-known/openid-configuration",
            new StubOidcEndpoints(
                "https://auth.example.com/authorize",
                "https://auth.example.com/token",
                "https://auth.example.com/logout"),
            accessToken: "opaque-access-token",
            idToken: CreateIdToken("Opaque Token Admin", "admin"));

        var oidcFixture = CreateGenericOidcFixture(stubFactory);

        try
        {
            await oidcFixture.InitializeAsync();
            var oidcClient = oidcFixture.CreateClient();

            var authorizeResponse = await oidcClient.PostAsJsonAsync(
                "/api/v1/admin/auth/providers/oidc/authorize-url",
                new { });
            var authorizeContent = await authorizeResponse.Content.ReadFromJsonAsync(AdminAuthJsonContext.Default.AdminAuthAuthorizeUrlResponse);
            var authorizeUri = new Uri(authorizeContent!.AuthorizeUrl);
            var authorizeParameters = QueryHelpers.ParseQuery(authorizeUri.Query);

            var tokenResponse = await oidcClient.PostAsJsonAsync(
                "/api/v1/admin/auth/providers/oidc/token",
                new
                {
                    grantType = "authorization_code",
                    code = "code-123",
                    state = authorizeParameters["state"].ToString()
                });

            tokenResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);

            var sessionResponse = await oidcClient.GetAsync("/api/v1/admin/auth/session");
            sessionResponse.StatusCode.Should().Be(HttpStatusCode.OK);

            var session = await sessionResponse.Content.ReadFromJsonAsync(AdminAuthJsonContext.Default.AdminAuthSessionResponse);
            session.Should().NotBeNull();
            session!.IsAuthenticated.Should().BeTrue();
            session.Claims.Should().Contain(c => c.Type == "name" && c.Value == "Opaque Token Admin");
            session.Claims.Should().Contain(c => c.Type == ClaimTypes.Role && c.Value == "admin");
        }
        finally
        {
            await oidcFixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/auth/providers/{providerKey}/token")]
    public async Task RequestToken_WithForgedIdToken_ReturnsServiceUnavailable()
    {
        using var forgedKey = RSA.Create(2048);
        var forgedSigningKey = new RsaSecurityKey(forgedKey) { KeyId = "forged-key" };
        var forgedCredentials = new SigningCredentials(forgedSigningKey, SecurityAlgorithms.RsaSha256);
        var forgedToken = new JwtSecurityToken(
            issuer: TestOidcIssuer,
            audience: TestOidcAudience,
            claims:
            [
                new Claim("sub", "attacker"),
                new Claim("name", "Forged Admin"),
                new Claim("roles", "admin")
            ],
            expires: DateTime.UtcNow.AddHours(1),
            signingCredentials: forgedCredentials);

        using var stubFactory = new StubHttpClientFactory(
            "https://auth.example.com/.well-known/openid-configuration",
            new StubOidcEndpoints(
                "https://auth.example.com/authorize",
                "https://auth.example.com/token",
                "https://auth.example.com/logout"),
            idToken: new JwtSecurityTokenHandler().WriteToken(forgedToken));

        var oidcFixture = CreateGenericOidcFixture(stubFactory);

        try
        {
            await oidcFixture.InitializeAsync();
            var oidcClient = oidcFixture.CreateClient();

            var authorizeResponse = await oidcClient.PostAsJsonAsync(
                "/api/v1/admin/auth/providers/oidc/authorize-url",
                new { });
            var authorizeContent = await authorizeResponse.Content.ReadFromJsonAsync(AdminAuthJsonContext.Default.AdminAuthAuthorizeUrlResponse);
            var authorizeUri = new Uri(authorizeContent!.AuthorizeUrl);
            var authorizeParameters = QueryHelpers.ParseQuery(authorizeUri.Query);

            var tokenResponse = await oidcClient.PostAsJsonAsync(
                "/api/v1/admin/auth/providers/oidc/token",
                new
                {
                    grantType = "authorization_code",
                    code = "code-123",
                    state = authorizeParameters["state"].ToString()
                });

            tokenResponse.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);

            var sessionResponse = await oidcClient.GetAsync("/api/v1/admin/auth/session");
            var session = await sessionResponse.Content.ReadFromJsonAsync(AdminAuthJsonContext.Default.AdminAuthSessionResponse);
            session.Should().NotBeNull();
            session!.IsAuthenticated.Should().BeFalse();
        }
        finally
        {
            await oidcFixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/auth/providers/{providerKey}/logout-url")]
    public async Task GetLogoutUrl_WithUnknownProvider_DoesNotClearAuthenticatedSession()
    {
        using var stubFactory = new StubHttpClientFactory(
            "https://auth.example.com/.well-known/openid-configuration",
            new StubOidcEndpoints(
                "https://auth.example.com/authorize",
                "https://auth.example.com/token",
                "https://auth.example.com/logout"));

        var oidcFixture = CreateGenericOidcFixture(stubFactory);

        try
        {
            await oidcFixture.InitializeAsync();
            var oidcClient = oidcFixture.CreateClient();

            var authorizeResponse = await oidcClient.PostAsJsonAsync(
                "/api/v1/admin/auth/providers/oidc/authorize-url",
                new { });
            var authorizeContent = await authorizeResponse.Content.ReadFromJsonAsync(AdminAuthJsonContext.Default.AdminAuthAuthorizeUrlResponse);
            var authorizeUri = new Uri(authorizeContent!.AuthorizeUrl);
            var authorizeParameters = QueryHelpers.ParseQuery(authorizeUri.Query);

            var tokenResponse = await oidcClient.PostAsJsonAsync(
                "/api/v1/admin/auth/providers/oidc/token",
                new
                {
                    grantType = "authorization_code",
                    code = "code-123",
                    state = authorizeParameters["state"].ToString()
                });

            tokenResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);

            var logoutResponse = await oidcClient.GetAsync("/api/v1/admin/auth/providers/unknown/logout-url");
            logoutResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);

            var sessionResponse = await oidcClient.GetAsync("/api/v1/admin/auth/session");
            sessionResponse.StatusCode.Should().Be(HttpStatusCode.OK);

            var session = await sessionResponse.Content.ReadFromJsonAsync(AdminAuthJsonContext.Default.AdminAuthSessionResponse);
            session.Should().NotBeNull();
            session!.IsAuthenticated.Should().BeTrue();
        }
        finally
        {
            await oidcFixture.DisposeAsync();
        }
    }

    [Endpoint("GET /api/v1/admin/auth/providers/{providerKey}/logout-url")]
    public async Task GetLogoutUrl_UsesConfiguredPublicBaseUrlForPostLogoutRedirectUri()
    {
        using var stubFactory = new StubHttpClientFactory(
            "https://auth.example.com/.well-known/openid-configuration",
            new StubOidcEndpoints(
                "https://auth.example.com/authorize",
                "https://auth.example.com/token",
                "https://auth.example.com/logout"));

        var oidcFixture = CreateGenericOidcFixture(stubFactory)
            .ConfigureWebHost(builder => builder.UseSetting("Public:BaseUrl", "https://public.example.com/honua"));

        try
        {
            await oidcFixture.InitializeAsync();
            var oidcClient = oidcFixture.CreateClient();

            var response = await oidcClient.GetAsync("/api/v1/admin/auth/providers/oidc/logout-url?idTokenHint=id-token");

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var content = await response.Content.ReadFromJsonAsync(AdminAuthJsonContext.Default.AdminAuthLogoutUrlResponse);
            content.Should().NotBeNull();

            var logoutUri = new Uri(content!.LogoutUrl!);
            var parameters = QueryHelpers.ParseQuery(logoutUri.Query);
            parameters["post_logout_redirect_uri"].ToString().Should().Be("https://public.example.com/honua/admin");
            parameters["id_token_hint"].ToString().Should().Be("id-token");
        }
        finally
        {
            await oidcFixture.DisposeAsync();
        }
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
                builder.UseSetting("Oidc:TokenValidation:SymmetricSigningKey", TestSigningKey);
            });
    }

    private static WebAppFixture CreateGenericOidcFixture(IHttpClientFactory httpClientFactory)
    {
        return CreateBaseFixture()
            .ConfigureWebHost(builder =>
            {
                builder.UseSetting("Oidc:Enabled", "true");
                builder.UseSetting("Oidc:RequireHttps", "true");
                builder.UseSetting("Oidc:Generic:Enabled", "true");
                builder.UseSetting("Oidc:Generic:Authority", "https://auth.example.com");
                builder.UseSetting("Oidc:Generic:ClientId", "generic-client-id");
                builder.UseSetting("Oidc:Generic:ClientSecret", "generic-secret-value-minimum-length");
                builder.UseSetting("Oidc:Generic:DisplayName", "External Provider");
                builder.UseSetting("Oidc:TokenValidation:ValidateIssuer", "false");
                builder.UseSetting("Oidc:TokenValidation:ValidateAudience", "false");
                builder.UseSetting("Oidc:TokenValidation:ValidateIssuerSigningKey", "false");
                builder.UseSetting("Oidc:TokenValidation:SymmetricSigningKey", TestSigningKey);
            })
            .ConfigureServices(services =>
            {
                services.AddSingleton(httpClientFactory);
            });
    }

    private sealed record StubOidcEndpoints(
        string AuthorizationEndpoint,
        string TokenEndpoint,
        string LogoutEndpoint,
        string? JwksEndpoint = null);

    private sealed class StubHttpClientFactory : IHttpClientFactory, IDisposable
    {
        private readonly HttpClient _client;
        private readonly string _expectedDiscoveryUrl;
        private readonly StubOidcEndpoints _endpoints;
        private readonly string _accessToken;
        private readonly string? _idToken;
        private readonly string _jwksEndpoint;

        public StubHttpClientFactory(
            string expectedDiscoveryUrl,
            StubOidcEndpoints endpoints,
            string? accessToken = null,
            string? idToken = null)
        {
            _expectedDiscoveryUrl = expectedDiscoveryUrl;
            _endpoints = endpoints;
            _jwksEndpoint = endpoints.JwksEndpoint
                ?? new Uri(new Uri(expectedDiscoveryUrl), "/keys").ToString();
            _accessToken = accessToken ?? CreateAccessToken();
            _idToken = idToken ?? CreateIdToken("Test Admin", "admin");
            _client = new HttpClient(new StubHttpMessageHandler(this))
            {
                BaseAddress = new Uri("https://stub.local")
            };
        }

        public Dictionary<string, string> LastTokenFormValues { get; } = new(StringComparer.Ordinal);

        public HttpClient CreateClient(string name) => _client;

        public void Dispose()
        {
            _client.Dispose();
        }

        private sealed class StubHttpMessageHandler(StubHttpClientFactory owner) : HttpMessageHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                request.RequestUri.Should().NotBeNull();

                if (request.Method == HttpMethod.Get &&
                    string.Equals(request.RequestUri!.ToString(), owner._expectedDiscoveryUrl, StringComparison.Ordinal))
                {
                    var discoveryJson = $$"""
                        {
                          "issuer": "{{TestOidcIssuer}}",
                          "authorization_endpoint": "{{owner._endpoints.AuthorizationEndpoint}}",
                          "token_endpoint": "{{owner._endpoints.TokenEndpoint}}",
                          "jwks_uri": "{{owner._jwksEndpoint}}",
                          "end_session_endpoint": "{{owner._endpoints.LogoutEndpoint}}"
                        }
                        """;

                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(discoveryJson, Encoding.UTF8, "application/json")
                    };
                }

                if (request.Method == HttpMethod.Get &&
                    string.Equals(request.RequestUri!.ToString(), owner._jwksEndpoint, StringComparison.Ordinal))
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(CreateJwksDocument(), Encoding.UTF8, "application/json")
                    };
                }

                if (request.Method == HttpMethod.Post &&
                    string.Equals(request.RequestUri!.ToString(), owner._endpoints.TokenEndpoint, StringComparison.Ordinal))
                {
                    var formBody = await request.Content!.ReadAsStringAsync(cancellationToken);
                    owner.LastTokenFormValues.Clear();
                    foreach (var pair in QueryHelpers.ParseQuery(formBody))
                    {
                        owner.LastTokenFormValues[pair.Key] = pair.Value.ToString();
                    }

                    var idTokenJson = owner._idToken is null ? "null" : $"\"{owner._idToken}\"";
                    var tokenJson = $$"""
                        {
                          "access_token": "{{owner._accessToken}}",
                          "id_token": {{idTokenJson}},
                          "refresh_token": "refresh-token",
                          "token_type": "Bearer",
                          "expires_in": 3600,
                          "scope": "openid profile"
                        }
                        """;

                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(tokenJson, Encoding.UTF8, "application/json")
                    };
                }

                using var scope = new AssertionScope();
                request.RequestUri!.ToString().Should().BeOneOf(owner._expectedDiscoveryUrl, owner._jwksEndpoint, owner._endpoints.TokenEndpoint);
                request.Method.Should().BeOneOf(HttpMethod.Get, HttpMethod.Post);
                return new HttpResponseMessage(HttpStatusCode.InternalServerError);
            }
        }
    }

    private static string CreateAccessToken()
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(TestSigningKey));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(
            issuer: "https://auth.example.com",
            audience: "generic-client-id",
            claims:
            [
                new Claim("sub", "admin-user"),
                new Claim("name", "Test Admin"),
                new Claim("roles", "admin"),
                new Claim(ClaimTypes.Role, "admin")
            ],
            expires: DateTime.UtcNow.AddHours(1),
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private static string CreateIdToken(string name, params string[] roles)
    {
        var claims = new List<Claim>
        {
            new("sub", "admin-user"),
            new("name", name)
        };

        claims.AddRange(roles.Select(static role => new Claim("roles", role)));

        var token = new JwtSecurityToken(
            issuer: TestOidcIssuer,
            audience: TestOidcAudience,
            claims: claims,
            expires: DateTime.UtcNow.AddHours(1),
            signingCredentials: _oidcSigningCredentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private static RsaSecurityKey CreateOidcSigningKey()
    {
        var rsa = RSA.Create(2048);
        return new RsaSecurityKey(rsa)
        {
            KeyId = "test-admin-auth-jwk"
        };
    }

    private static string CreateJwksDocument()
    {
        var parameters = _oidcSigningKey.Rsa!.ExportParameters(false);
        var modulus = Base64UrlEncoder.Encode(parameters.Modulus);
        var exponent = Base64UrlEncoder.Encode(parameters.Exponent);

        return $$"""
            {
              "keys": [
                {
                  "kty": "RSA",
                  "use": "sig",
                  "alg": "RS256",
                  "kid": "{{_oidcSigningKey.KeyId}}",
                  "n": "{{modulus}}",
                  "e": "{{exponent}}"
                }
              ]
            }
            """;
    }
}
