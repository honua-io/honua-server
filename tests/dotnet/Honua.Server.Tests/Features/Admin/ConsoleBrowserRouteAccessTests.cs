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
public sealed class ConsoleBrowserRouteAccessTests : IAsyncLifetime
{
    private const string ConsoleOrigin = "https://console.example.com";
    private const string AdminPassword = "test-admin-key";
    private const string OperatorBearerSigningKey = "operator-bearer-signing-key-at-least-32-bytes-long";

    private static readonly HttpStatusCode[] CredentialDenials =
        [HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden];

    private readonly WebAppFixture _fixture = new WebAppFixture()
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
        });

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
    [Endpoint("GET /rest/services")]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    [Endpoint("GET /api/styles/{layerId:int}.json")]
    [Endpoint("GET /tiles/{layerId}/{z}/{x}/{y}.mvt")]
    [Endpoint("GET /scenes/{sceneId}/tileset.json")]
    [Endpoint("GET /api/v1/studio/package-families")]
    [Endpoint("GET /api/v1/studio/ai/capabilities")]
    public async Task ConsoleRoute_CrossOriginOperatorBearer_MatchesSameOriginSession(string route)
    {
        using var session = await SignInSameOriginAsync();
        using var bearer = await SignInCrossOriginAsync();

        using var sessionResponse = await session.GetAsync(route);
        using var bearerResponse = await bearer.GetAsync(route);

        sessionResponse.StatusCode.Should().NotBeOneOf(CredentialDenials);
        bearerResponse.StatusCode.Should().Be(sessionResponse.StatusCode);
        (await ReadsAsEsriTokenDenialAsync(bearerResponse)).Should().BeFalse();
        if (sessionResponse.StatusCode == HttpStatusCode.OK)
        {
            (await bearerResponse.Content.ReadAsStringAsync()).Should()
                .Be(await sessionResponse.Content.ReadAsStringAsync());
        }
    }

    [IntegrationTest]
    [Endpoint("POST /mcp")]
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

    [IntegrationTest]
    [Endpoint("GET /api/v1/studio/package-families")]
    public async Task ConsoleRoute_WithoutCredential_StaysProtected()
    {
        using var anonymous = _fixture.CreateClient();
        using var response = await anonymous.GetAsync("/api/v1/studio/package-families");

        response.StatusCode.Should().BeOneOf(CredentialDenials);
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
