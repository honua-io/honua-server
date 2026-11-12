// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Honua.Infrastructure.Models;
using Honua.Server.Features.Admin.Models;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Helpers;
using Microsoft.AspNetCore.Hosting;

namespace Honua.Server.Tests.Features.Admin;

// The static Console calls the native data routes directly from the browser: same-origin with the
// session cookie, or from another origin with the operator bearer. Both credentials must reach the
// same authorization and tenant outcome on every route the Console used to proxy.
[Collection("Database")]
[Protocol(TestProtocols.Admin)]
[Operation(Operations.Security)]
public class ConsoleBrowserRouteAccessTests : IAsyncLifetime
{
    private const string ConsoleOrigin = "https://console.example.com";
    private const string AdminPassword = "test-admin-key";
    private const string OperatorBearerSigningKey = "operator-bearer-signing-key-at-least-32-bytes-long";
    private const string ConsoleEnvironmentId = "console-test-env";

    private static readonly HttpStatusCode[] CredentialDenials =
        [HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden];

    private readonly WebAppFixture _fixture;
    private readonly string _expectedSourcePathBase;

    public ConsoleBrowserRouteAccessTests()
        : this(multiTenant: false, configuredSourcePathBase: null)
    {
    }

    protected ConsoleBrowserRouteAccessTests(bool multiTenant, string? configuredSourcePathBase)
    {
        _expectedSourcePathBase = configuredSourcePathBase?.TrimEnd('/') ?? string.Empty;
        _fixture = new WebAppFixture()
            .UseSeed("tests/seed/server.yaml")
            .ConfigureWebHost(builder =>
            {
                builder.UseEnvironment("Test");
                builder.UseSetting("HONUA_DEV_AUTH", "false");
                builder.UseSetting("HONUA_ADMIN_PASSWORD", AdminPassword);
                builder.UseSetting("Oidc:Enabled", "false");
                builder.UseSetting("Cors:AllowedOrigins:0", ConsoleOrigin);
                builder.UseSetting("Authentication:OperatorBearer:Enabled", "true");
                builder.UseSetting("Authentication:OperatorBearer:SigningKey", OperatorBearerSigningKey);
                builder.UseSetting("Studio:Session:EnvironmentId", ConsoleEnvironmentId);
                // Multi-tenant mode takes the tenant middleware's resolution path instead of the
                // single-tenant early return, so the operator bearer's default-tenant branch runs.
                builder.UseSetting("MultiTenancy:Enabled", multiTenant ? "true" : "false");
                if (configuredSourcePathBase is not null)
                {
                    builder.UseSetting("Studio:Session:SourcePathBase", configuredSourcePathBase);
                }
            });
    }

    public Task InitializeAsync() => _fixture.InitializeAsync();

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTheory]
    [InlineData("/rest/services?f=json")]
    [InlineData("/rest/services/console-missing/FeatureServer/0/query?where=1%3D1&outFields=*&returnGeometry=false&resultRecordCount=1&f=json")]
    [InlineData("/api/styles/987654.json")]
    [InlineData("/tiles/987654/0/0/0.mvt")]
    [InlineData("/scenes/console-missing/tileset.json")]
    [InlineData("/api/v1/studio/package-families")]
    [InlineData("/api/v1/studio/ai/capabilities")]
    [InlineData("/api/v1/studio/session")]
    [Endpoint("GET /rest/services")]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    [Endpoint("GET /api/styles/{layerId:int}.json")]
    [Endpoint("GET /tiles/{layerId}/{z}/{x}/{y}.mvt")]
    [Endpoint("GET /scenes/{sceneId}/tileset.json")]
    [Endpoint("GET /api/v1/studio/package-families")]
    [Endpoint("GET /api/v1/studio/ai/capabilities")]
    [Endpoint("GET /api/v1/studio/session")]
    public async Task ConsoleRoute_CrossOriginOperatorBearer_MatchesSameOriginSession(string route)
    {
        using var session = await SignInSameOriginAsync();
        using var bearer = await SignInCrossOriginAsync();

        using var sessionResponse = await session.GetAsync(route);
        using var bearerResponse = await bearer.GetAsync(route);

        CredentialDenials.Should().NotContain(sessionResponse.StatusCode);
        bearerResponse.StatusCode.Should().Be(sessionResponse.StatusCode);
        (await ReadsAsEsriTokenDenialAsync(bearerResponse)).Should().BeFalse();
    }

    [IntegrationTest]
    [Endpoint("POST /mcp")]
    [InterfaceOperation(TestProtocols.Mcp, "initialize")]
    public async Task McpFromConsoleOrigin_PreflightAllowsAndResponseExposesSessionHeader()
    {
        using var bearer = await SignInCrossOriginAsync();
        using var preflight = new HttpRequestMessage(HttpMethod.Options, "/mcp");
        preflight.Headers.Add("Access-Control-Request-Method", "POST");
        preflight.Headers.Add("Access-Control-Request-Headers", "authorization, content-type, mcp-session-id, idempotency-key");

        using var preflightResponse = await bearer.SendAsync(preflight);
        using var initialized = await bearer.PostAsJsonAsync("/mcp", new
        {
            jsonrpc = "2.0",
            id = 1,
            method = "initialize",
            @params = new
            {
                protocolVersion = "2025-06-18",
                capabilities = new { },
                clientInfo = new { name = "console-spa-test", version = "1.0" }
            }
        });

        preflightResponse.Headers.GetValues("Access-Control-Allow-Origin").Should().Equal(ConsoleOrigin);
        string.Join(',', preflightResponse.Headers.GetValues("Access-Control-Allow-Headers")).Should()
            .ContainEquivalentOf("mcp-session-id").And.ContainEquivalentOf("idempotency-key");
        initialized.StatusCode.Should().Be(HttpStatusCode.OK);
        initialized.Headers.Contains("Mcp-Session-Id").Should().BeTrue();
        string.Join(',', initialized.Headers.GetValues("Access-Control-Expose-Headers")).Should()
            .ContainEquivalentOf("Mcp-Session-Id");
    }

    [IntegrationTheory]
    [InlineData("/api/v1/studio/package-families")]
    [InlineData("/api/v1/studio/session")]
    [Endpoint("GET /api/v1/studio/package-families")]
    [Endpoint("GET /api/v1/studio/session")]
    public async Task ConsoleRoute_WithoutCredential_StaysProtected(string route)
    {
        using var anonymous = _fixture.CreateClient();
        using var response = await anonymous.GetAsync(route);

        CredentialDenials.Should().Contain(response.StatusCode);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/studio/session")]
    public async Task StudioSession_CookieAndCrossOriginBearer_ReturnServerOwnedBootstrap()
    {
        using var session = await SignInSameOriginAsync();
        using var bearer = await SignInCrossOriginAsync();

        foreach (var client in new[] { session, bearer })
        {
            using var response = await client.GetAsync("/api/v1/studio/session");

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            response.Headers.CacheControl?.NoStore.Should().BeTrue();
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var data = document.RootElement.GetProperty("data");
            data.GetProperty("authenticated").GetBoolean().Should().BeTrue();
            data.GetProperty("principalId").GetString().Should().NotBeNullOrWhiteSpace();
            data.GetProperty("environmentId").GetString().Should().Be(ConsoleEnvironmentId);
            data.GetProperty("sourcePathBase").GetString().Should().Be(_expectedSourcePathBase);
        }
    }

    // The Console re-issues its bearer on 401 (lifetime <= 30 min). Studio records the caller's
    // actor id as draft owner, so a re-issued bearer must resolve to the same principal. The
    // cookie and the bearer are different credential schemes and resolve to different actor ids
    // (the bearer is issuer-qualified by the operator bearer issuer); a deployment uses one of them.
    [IntegrationTest]
    [Endpoint("GET /api/v1/studio/session")]
    public async Task StudioSession_ReissuedOperatorBearer_KeepsPrincipalId()
    {
        using var first = await SignInCrossOriginAsync();
        using var second = await SignInCrossOriginAsync();

        var firstId = await ReadPrincipalIdAsync(first);
        var secondId = await ReadPrincipalIdAsync(second);

        secondId.Should().Be(firstId);
    }

    private static async Task<string?> ReadPrincipalIdAsync(HttpClient client)
    {
        using var response = await client.GetAsync("/api/v1/studio/session");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("data").GetProperty("principalId").GetString();
    }

    private async Task<HttpClient> SignInSameOriginAsync()
    {
        var client = _fixture.CreateClient();
        client.BaseAddress = new Uri("https://localhost");
        using var csrf = await client.GetAsync("/api/v1/admin/auth/csrf");
        csrf.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await csrf.Content.ReadAsStringAsync());
        client.DefaultRequestHeaders.Add("X-Honua-CSRF", document.RootElement.GetProperty("requestToken").GetString());
        using var signIn = await client.PostAsJsonAsync("/api/v1/admin/auth/session", new { password = AdminPassword });
        signIn.StatusCode.Should().Be(HttpStatusCode.NoContent);
        return client;
    }

    private async Task<HttpClient> SignInCrossOriginAsync()
    {
        using var browser = _fixture.CreateClient();
        browser.BaseAddress = new Uri("https://localhost");
        browser.DefaultRequestHeaders.Add("Origin", ConsoleOrigin);
        using var issued = await browser.PostAsJsonAsync("/api/v1/admin/auth/password-token", new { password = AdminPassword });
        issued.StatusCode.Should().Be(HttpStatusCode.OK);
        var token = await issued.Content.ReadFromJsonAsync(AdminAuthJsonContext.Default.AdminOperatorBearerResponse);

        var client = _fixture.CreateClient();
        client.BaseAddress = new Uri("https://localhost");
        client.DefaultRequestHeaders.Add("Origin", ConsoleOrigin);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token!.AccessToken);
        return client;
    }

    private static async Task<bool> ReadsAsEsriTokenDenialAsync(HttpResponseMessage response)
    {
        if (response.Content.Headers.ContentType?.MediaType?.Contains("json", StringComparison.Ordinal) != true)
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("error", out var error)
                && error.ValueKind == JsonValueKind.Object
                && error.TryGetProperty("code", out var code)
                && code.TryGetInt32(out var value)
                && value is GeoServicesErrorCodes.InvalidToken or GeoServicesErrorCodes.TokenRequired;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}

// The same proofs with tenant resolution enabled and the API behind a configured path prefix.
[Collection("Database")]
[Protocol(TestProtocols.Admin)]
[Operation(Operations.Security)]
public sealed class ConsoleBrowserRouteAccessMultiTenantTests() : ConsoleBrowserRouteAccessTests(multiTenant: true, configuredSourcePathBase: "/gis/api/");
