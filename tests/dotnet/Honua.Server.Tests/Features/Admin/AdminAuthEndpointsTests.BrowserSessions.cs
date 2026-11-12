// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using FluentAssertions;
using Honua.Infrastructure.Authentication;
using Honua.Server.Features.Admin.Models;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Features.Admin;

public sealed partial class AdminAuthEndpointsTests
{
    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/auth/password-token")]
    [Endpoint("GET /api/v1/admin/config")]
    [Endpoint("GET /api/v1/admin/auth/session")]
    public async Task PasswordToken_AllowedOrigin_IssuesUsableBearerWithoutCookies()
    {
        await using var fixture = CreatePasswordTokenFixture();
        await fixture.InitializeAsync();
        using var browser = fixture.CreateClient();
        browser.BaseAddress = new Uri("https://localhost");
        browser.DefaultRequestHeaders.Add("Origin", "https://console.example.com");
        using var response = await browser.PostAsJsonAsync("/api/v1/admin/auth/password-token", new { password = "test-admin-key" });
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.Contains("Set-Cookie").Should().BeFalse();
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
        var token = await response.Content.ReadFromJsonAsync(AdminAuthJsonContext.Default.AdminOperatorBearerResponse);
        token!.ExpiresIn.Should().BeInRange(1, 300);
        using var api = fixture.CreateClient();
        api.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token.AccessToken);
        using var authorized = await api.GetAsync("/api/v1/admin/config");
        authorized.StatusCode.Should().Be(HttpStatusCode.OK);
        using var identityResponse = await api.GetAsync("/api/v1/admin/auth/session");
        identityResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var identity = await identityResponse.Content.ReadFromJsonAsync(AdminAuthJsonContext.Default.AdminAuthSessionResponse);
        identity!.IsAuthenticated.Should().BeTrue();
        identityResponse.Headers.Contains("Set-Cookie").Should().BeFalse();
        identity.ProviderKey.Should().Be("operator-bearer");
        identity.Claims.Should().Contain(claim => claim.Type == ClaimTypes.NameIdentifier
            && claim.Value == ApiKeyAuthenticationHandler.BootstrapAdminActorId.ToString());
    }

    [IntegrationTheory]
    [InlineData("https://unlisted.example.com", "https://localhost", HttpStatusCode.BadRequest)]
    [InlineData("null", "https://localhost", HttpStatusCode.BadRequest)]
    [InlineData(null, "https://localhost", HttpStatusCode.BadRequest)]
    [InlineData("https://console.example.com", "http://localhost", HttpStatusCode.BadRequest)]
    [Endpoint("POST /api/v1/admin/auth/password-token")]
    public async Task PasswordToken_UntrustedRequest_IsRejected(string? origin, string server, HttpStatusCode expected)
    {
        await using var fixture = CreatePasswordTokenFixture();
        await fixture.InitializeAsync();
        using var browser = fixture.CreateClient();
        browser.BaseAddress = new Uri(server);
        if (origin is not null)
        {
            browser.DefaultRequestHeaders.Add("Origin", origin);
        }
        using var response = await browser.PostAsJsonAsync("/api/v1/admin/auth/password-token", new { password = "test-admin-key" });
        response.StatusCode.Should().Be(expected);
        response.Headers.Contains("Set-Cookie").Should().BeFalse();
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/auth/password-token")]
    public async Task PasswordToken_InvalidPassword_IsUnauthorized()
    {
        await using var fixture = CreatePasswordTokenFixture();
        await fixture.InitializeAsync();
        using var browser = fixture.CreateClient();
        browser.BaseAddress = new Uri("https://localhost");
        browser.DefaultRequestHeaders.Add("Origin", "https://console.example.com");
        using var response = await browser.PostAsJsonAsync("/api/v1/admin/auth/password-token", new { password = "invalid" });
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        response.Headers.Contains("Set-Cookie").Should().BeFalse();
    }

    private static WebAppFixture CreatePasswordTokenFixture() => CreateBaseFixture().ConfigureWebHost(builder =>
    {
        builder.UseSetting("Oidc:Enabled", "false");
        builder.UseSetting("Cors:AllowedOrigins:0", "https://console.example.com");
        builder.UseSetting("Authentication:OperatorBearer:Enabled", "true");
        builder.UseSetting("Authentication:OperatorBearer:SigningKey", TestOperatorBearerSigningKey);
        builder.UseSetting("Authentication:OperatorBearer:MaxLifetimeMinutes", "5");
    });

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/auth/session")]
    public async Task PasswordSignIn_WithoutCsrf_IsRejected()
    {
        _client.BaseAddress = new Uri("https://localhost");
        var response = await _client.PostAsJsonAsync("/api/v1/admin/auth/session", new { password = "test-admin-key" });
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/auth/session")]
    [Endpoint("GET /api/v1/admin/auth/csrf")]
    [Endpoint("GET /api/v1/admin/config")]
    public async Task PasswordSignIn_WithoutOidc_CreatesSecureSessionAndProtectsLogout()
    {
        _client.BaseAddress = new Uri("https://localhost");
        await SetCsrfHeaderAsync(_client);
        var signIn = await _client.PostAsJsonAsync("/api/v1/admin/auth/session", new { password = "test-admin-key" });
        signIn.StatusCode.Should().Be(HttpStatusCode.NoContent);
        var cookie = signIn.Headers.GetValues("Set-Cookie").Single(value => value.StartsWith("honua_admin_session=", StringComparison.Ordinal));
        cookie.Should().Contain("httponly").And.Contain("secure").And.Contain("samesite=strict");
        var session = await _client.GetFromJsonAsync<AdminAuthSessionResponse>("/api/v1/admin/auth/session");
        session!.IsAuthenticated.Should().BeTrue();
        session.ProviderKey.Should().Be("builtin");
        session.Claims.Should().ContainSingle(claim => claim.Type == "auth_type" && claim.Value == "admin");
        var admin = await _client.GetAsync("/api/v1/admin/config");
        admin.StatusCode.Should().Be(HttpStatusCode.OK);
        var sessionId = cookie.Split(';')[0].Split('=', 2)[1];
        var store = _fixture.Services.GetRequiredService<AdminAuthSessionStore>();
        (await store.GetInteractiveSessionForApprovalAsync(sessionId, CancellationToken.None)).Should().BeNull();
        _client.DefaultRequestHeaders.Remove("X-Honua-CSRF");
        var rejected = await _client.PostAsync("/api/v1/admin/auth/logout", null);
        rejected.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        await SetCsrfHeaderAsync(_client);
        var logout = await _client.PostAsync("/api/v1/admin/auth/logout", null);
        logout.StatusCode.Should().Be(HttpStatusCode.OK);
        var revoked = await _client.GetFromJsonAsync<AdminAuthSessionResponse>("/api/v1/admin/auth/session");
        revoked!.IsAuthenticated.Should().BeFalse();
        var stored = await _fixture.Services.GetRequiredService<AdminAuthSessionStore>()
            .GetAuthenticatedSessionAsync(sessionId, CancellationToken.None);
        stored.Should().BeNull();
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/auth/session")]
    [Endpoint("GET /api/v1/studio/package-families")]
    public async Task PasswordSignIn_WithoutOidc_CanAccessStudio()
    {
        _client.BaseAddress = new Uri("https://localhost");
        await SetCsrfHeaderAsync(_client);
        using var signIn = await _client.PostAsJsonAsync("/api/v1/admin/auth/session", new { password = "test-admin-key" });
        signIn.StatusCode.Should().Be(HttpStatusCode.NoContent);
        using var studio = await _client.GetAsync("/api/v1/studio/package-families");
        studio.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/auth/session")]
    [Endpoint("POST /api/v1/admin/auth/bearer")]
    [Endpoint("POST /mcp")]
    public async Task PasswordSignIn_WithoutOidc_IssuesBearerWithDurableMcpIdentity()
    {
        await using var fixture = CreateBaseFixture().ConfigureWebHost(builder =>
        {
            builder.UseSetting("Oidc:Enabled", "false");
            builder.UseSetting("Authentication:OperatorBearer:Enabled", "true");
            builder.UseSetting("Authentication:OperatorBearer:SigningKey", TestOperatorBearerSigningKey);
        });
        await fixture.InitializeAsync();
        using var browser = fixture.CreateClient();
        browser.BaseAddress = new Uri("https://localhost");
        await SetCsrfHeaderAsync(browser);
        using var signIn = await browser.PostAsJsonAsync("/api/v1/admin/auth/session", new { password = "test-admin-key" });
        signIn.StatusCode.Should().Be(HttpStatusCode.NoContent);
        await SetCsrfHeaderAsync(browser);
        using var bearer = await browser.PostAsync("/api/v1/admin/auth/bearer", null);
        bearer.StatusCode.Should().Be(HttpStatusCode.OK);
        var token = await bearer.Content.ReadFromJsonAsync(AdminAuthJsonContext.Default.AdminOperatorBearerResponse);
        using var mcp = fixture.CreateClient();
        mcp.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token!.AccessToken);
        using var initialized = await mcp.PostAsJsonAsync("/mcp", new
        {
            jsonrpc = "2.0",
            id = 1,
            method = "initialize",
            @params = new
            {
                protocolVersion = "2025-06-18",
                capabilities = new { },
                clientInfo = new { name = "browser-session-test", version = "1.0" }
            }
        });
        initialized.StatusCode.Should().Be(HttpStatusCode.OK);
        initialized.Headers.Contains("Mcp-Session-Id").Should().BeTrue();
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/auth/session")]
    public async Task PasswordSignIn_InvalidPassword_IsUnauthorized()
    {
        _client.BaseAddress = new Uri("https://localhost");
        await SetCsrfHeaderAsync(_client);
        var response = await _client.PostAsJsonAsync("/api/v1/admin/auth/session", new { password = "incorrect-password" });
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        response.Headers.TryGetValues("Set-Cookie", out var cookies).Should().BeFalse();
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/auth/session")]
    [Endpoint("GET /api/v1/admin/auth/csrf")]
    public async Task PasswordSignIn_RequiresHttps()
    {
        _client.BaseAddress = new Uri("http://localhost");
        var token = await _client.GetAsync("/api/v1/admin/auth/csrf");
        token.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var signIn = await _client.PostAsJsonAsync("/api/v1/admin/auth/session", new { password = "test-admin-key" });
        signIn.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/auth/session")]
    public async Task PasswordSignIn_WithDifferentOrigin_IsRejected()
    {
        _client.BaseAddress = new Uri("https://localhost");
        await SetCsrfHeaderAsync(_client);
        _client.DefaultRequestHeaders.Add("Origin", "https://localhost:8443");
        var response = await _client.PostAsJsonAsync("/api/v1/admin/auth/session", new { password = "test-admin-key" });
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/auth/session")]
    public async Task PasswordSignIn_WithInvalidCsrfToken_IsRejected()
    {
        _client.BaseAddress = new Uri("https://localhost");
        await SetCsrfHeaderAsync(_client);
        _client.DefaultRequestHeaders.Remove("X-Honua-CSRF");
        _client.DefaultRequestHeaders.Add("X-Honua-CSRF", "invalid-token");
        var response = await _client.PostAsJsonAsync("/api/v1/admin/auth/session", new { password = "test-admin-key" });
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    private static async Task SetCsrfHeaderAsync(HttpClient client)
    {
        var response = await client.GetAsync("/api/v1/admin/auth/csrf");
        response.EnsureSuccessStatusCode();
        using var document = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        client.DefaultRequestHeaders.Remove("X-Honua-CSRF");
        client.DefaultRequestHeaders.Add("X-Honua-CSRF", document.RootElement.GetProperty("requestToken").GetString());
    }

    [IntegrationTheory]
    [InlineData(null, "/console/")]
    [InlineData("/console/", "/console/")]
    [InlineData("/console/operate/connections", "/console/operate/connections")]
    [Endpoint("GET /admin/auth/callback")]
    [Endpoint("POST /api/v1/admin/auth/providers/{providerKey}/authorize-url")]
    public async Task BrowserCallback_ExchangesCodeAndRedirectsToStoredConsolePath(string? returnTo, string expected)
    {
        using var stubFactory = CreateCallbackStub();
        await using var fixture = CreateGenericOidcFixture(stubFactory);
        await fixture.InitializeAsync();
        using var browser = fixture.CreateClient(allowAutoRedirect: false);
        browser.BaseAddress = new Uri("https://localhost");
        using var authorize = await browser.PostAsJsonAsync(
            "/api/v1/admin/auth/providers/oidc/authorize-url", new { returnTo });
        authorize.StatusCode.Should().Be(HttpStatusCode.OK);
        authorize.Headers.GetValues("Set-Cookie").Should().Contain(value =>
            value.Contains("honua_admin_login=", StringComparison.Ordinal)
            && value.Contains("SameSite=Lax", StringComparison.OrdinalIgnoreCase)
            && value.Contains("Secure", StringComparison.OrdinalIgnoreCase));
        var body = await authorize.Content.ReadFromJsonAsync(AdminAuthJsonContext.Default.AdminAuthAuthorizeUrlResponse);
        var state = QueryHelpers.ParseQuery(new Uri(body!.AuthorizeUrl).Query)["state"].ToString();
        using var callback = await browser.GetAsync(
            "/admin/auth/callback?code=code-123&returnTo=https://unlisted.example.com&state=" + state);
        callback.StatusCode.Should().Be(HttpStatusCode.Redirect);
        callback.Headers.Location!.OriginalString.Should().Be(expected);
        callback.Headers.CacheControl!.NoStore.Should().BeTrue();
        using var identity = await browser.GetAsync("/api/v1/admin/auth/session");
        var session = await identity.Content.ReadFromJsonAsync(AdminAuthJsonContext.Default.AdminAuthSessionResponse);
        session!.IsAuthenticated.Should().BeTrue();
        using var replay = await browser.GetAsync("/admin/auth/callback?code=code-123&state=" + state);
        replay.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTheory]
    [InlineData("https://unlisted.example.com/console/")]
    [InlineData("//unlisted.example.com/console/")]
    [InlineData("/console/../admin")]
    [InlineData("/console/%2e%2e/admin")]
    [InlineData("/console/operate/connections?next=https://unlisted.example.com")]
    [InlineData("/admin/")]
    [Endpoint("POST /api/v1/admin/auth/providers/{providerKey}/authorize-url")]
    public async Task BrowserCallback_UnlistedReturnPath_IsRejected(string returnTo)
    {
        using var stubFactory = CreateCallbackStub();
        await using var fixture = CreateGenericOidcFixture(stubFactory);
        await fixture.InitializeAsync();
        using var browser = fixture.CreateClient();
        browser.BaseAddress = new Uri("https://localhost");
        using var response = await browser.PostAsJsonAsync(
            "/api/v1/admin/auth/providers/oidc/authorize-url", new { returnTo });
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Headers.Contains("Set-Cookie").Should().BeFalse();
    }

    [IntegrationTest]
    [Endpoint("GET /admin/auth/callback")]
    public async Task BrowserCallback_WithoutPendingSession_IsRejected()
    {
        using var browser = _fixture.CreateClient(allowAutoRedirect: false);
        browser.BaseAddress = new Uri("https://localhost");
        using var response = await browser.GetAsync("/admin/auth/callback?code=code-123&state=untrusted");
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTheory]
    [InlineData("wrong-state")]
    [InlineData("provider-error")]
    [InlineData("missing-code")]
    [Endpoint("GET /admin/auth/callback")]
    [Endpoint("POST /api/v1/admin/auth/providers/{providerKey}/authorize-url")]
    public async Task BrowserCallback_InvalidProviderResponse_DoesNotAuthenticate(string failure)
    {
        using var stubFactory = CreateCallbackStub();
        await using var fixture = CreateGenericOidcFixture(stubFactory);
        await fixture.InitializeAsync();
        using var browser = fixture.CreateClient(allowAutoRedirect: false);
        browser.BaseAddress = new Uri("https://localhost");
        using var authorize = await browser.PostAsJsonAsync(
            "/api/v1/admin/auth/providers/oidc/authorize-url", new { });
        authorize.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await authorize.Content.ReadFromJsonAsync(AdminAuthJsonContext.Default.AdminAuthAuthorizeUrlResponse);
        var state = QueryHelpers.ParseQuery(new Uri(body!.AuthorizeUrl).Query)["state"].ToString();
        var query = failure switch
        {
            "wrong-state" => "code=code-123&state=wrong",
            "provider-error" => "error=access_denied&state=" + state,
            _ => "state=" + state
        };
        using var callback = await browser.GetAsync("/admin/auth/callback?" + query);
        callback.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using var identity = await browser.GetAsync("/api/v1/admin/auth/session");
        var session = await identity.Content.ReadFromJsonAsync(AdminAuthJsonContext.Default.AdminAuthSessionResponse);
        session!.IsAuthenticated.Should().BeFalse();
    }

    private static StubHttpClientFactory CreateCallbackStub() => new(
        "https://auth.example.com/.well-known/openid-configuration",
        new StubOidcEndpoints("https://auth.example.com/authorize", "https://auth.example.com/token", "https://auth.example.com/logout"));
}
