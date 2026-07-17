// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Hosting;
using Microsoft.IdentityModel.Tokens;

namespace Honua.Server.Tests.Features.Protocols.Mcp;

/// <summary>
/// Integration tests for OAuth 2.1 scope narrowing on the <c>/mcp</c> transport
/// (honua-server#2851). Building on the bearer-token acceptance of #2850, these prove that a
/// validated token's scopes are intersected with the canonical operator grant model — scopes
/// can only ever narrow, never widen, what the principal's grants already permit.
/// </summary>
/// <remarks>
/// Every token minted here carries the <c>admin</c> role, so the operator grant check always
/// <em>passes</em> (admin bypass). That isolates the scope decision: a denial can only come
/// from scope narrowing, which is exactly the invariant #2851 requires — narrowing cannot be
/// escalated even for a caller whose grants would allow the operation. The suite exercises the
/// full ASP.NET Core pipeline with a configured OIDC authority (a symmetric-key generic
/// provider) and the dev-auth bypass disabled, so authorization is decided purely by the
/// token's scopes.
/// </remarks>
[Collection("Database")]
[Protocol(TestProtocols.Mcp)]
[Operation(Operations.Security)]
public sealed class McpScopeAuthorizationTests : IAsyncLifetime
{
    private const string JsonMediaType = "application/json";
    private const string Issuer = "https://idp.example.com";
    private const string Audience = "honua-mcp-client-id";
    private const string SigningKey = "mcp-scope-test-signing-key-at-least-32-characters-long";

    // honua_list_capabilities authorizes against Catalog/Discover — a lightweight tool that
    // reaches the operator authorization seam before doing any real work.
    private const string DiscoverToolCall =
        """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"honua_list_capabilities","arguments":{}}}""";

    private readonly WebAppFixture _fixture = new WebAppFixture()
        .UseSeed("tests/seed/server.yaml")
        .ConfigureWebHost(builder =>
        {
            builder.UseEnvironment("Test");
            builder.UseSetting("HONUA_DEV_AUTH", "false");
            builder.UseSetting("HONUA_ADMIN_PASSWORD", "test-admin-key");
            builder.UseSetting("Oidc:Enabled", "true");
            builder.UseSetting("Oidc:RequireHttps", "true");
            builder.UseSetting("Oidc:TokenValidation:SymmetricSigningKey", SigningKey);
            builder.UseSetting("Oidc:TokenValidation:EnableTokenReplayProtection", "false");
            builder.UseSetting("Oidc:Generic:Enabled", "true");
            builder.UseSetting("Oidc:Generic:Authority", Issuer);
            builder.UseSetting("Oidc:Generic:ClientId", Audience);
            builder.UseSetting("Oidc:Generic:DisplayName", "Test IdP");
        });

    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        _client = _fixture.Client;
    }

    public async Task DisposeAsync()
    {
        await _fixture.DisposeAsync();
    }

    [IntegrationTest]
    [Endpoint("POST /mcp")]
    [InterfaceOperation(TestProtocols.Mcp, "tools/call")]
    public async Task ToolsCall_AdminTokenWithMatchingScope_IsAuthorized()
    {
        // The token holds honua.mcp.discover, which permits Catalog/Discover — the operation
        // honua_list_capabilities authorizes against. Grant passes (admin) and scope permits.
        var token = CreateToken(scope: "honua.mcp.discover");

        using var response = await CallAsync(DiscoverToolCall, token);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = await ReadJsonAsync(response);
        var root = document.RootElement;
        root.TryGetProperty("error", out _).Should().BeFalse();

        var result = root.GetProperty("result");
        IsToolError(result).Should().BeFalse("a matching scope authorizes the tool");
        result.GetProperty("structuredContent").GetProperty("serverName").GetString()
            .Should().NotBeNullOrWhiteSpace();
    }

    [IntegrationTest]
    [Endpoint("POST /mcp")]
    [InterfaceOperation(TestProtocols.Mcp, "tools/call")]
    public async Task ToolsCall_AdminTokenWithNarrowerScope_IsDeniedByScopeEvenThoughGrantPasses()
    {
        // The token holds only honua.mcp.execute — NOT discover. The admin role means the grant
        // check passes, so the denial can only come from scope narrowing. This is the central
        // "narrowing cannot escalate, even for a tool that passes a grant check" assertion.
        var token = CreateToken(scope: "honua.mcp.execute");

        using var response = await CallAsync(DiscoverToolCall, token);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = await ReadJsonAsync(response);
        var result = document.RootElement.GetProperty("result");

        IsToolError(result).Should().BeTrue("honua.mcp.execute does not include Catalog/Discover");
        var structured = result.GetProperty("structuredContent");
        structured.GetProperty("code").GetString().Should().Be(
            "insufficient_scope",
            "a scope denial is a distinct structured reason from a grant permission_denied");
    }

    [IntegrationTest]
    [Endpoint("POST /mcp")]
    [InterfaceOperation(TestProtocols.Mcp, "tools/call")]
    public async Task ToolsCall_AdminTokenWithNoScope_IsFailClosed()
    {
        // A bearer token with no scope claim is fail-closed: it authorizes nothing, even though
        // the admin grant would allow the operation.
        var token = CreateToken(scope: null);

        using var response = await CallAsync(DiscoverToolCall, token);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = await ReadJsonAsync(response);
        var result = document.RootElement.GetProperty("result");

        IsToolError(result).Should().BeTrue("a token with no recognized scope is fail-closed");
        result.GetProperty("structuredContent").GetProperty("code").GetString()
            .Should().Be("insufficient_scope");
    }

    [IntegrationTest]
    [Endpoint("POST /mcp")]
    [InterfaceOperation(TestProtocols.Mcp, "tools/call")]
    public async Task ToolsCall_AdminTokenWithFullScope_IsAuthorized()
    {
        // honua.mcp.full opts the token out of narrowing, so it is bounded only by grants
        // (admin) and the tool is authorized.
        var token = CreateToken(scope: "honua.mcp.full");

        using var response = await CallAsync(DiscoverToolCall, token);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = await ReadJsonAsync(response);
        var result = document.RootElement.GetProperty("result");

        IsToolError(result).Should().BeFalse("honua.mcp.full permits every operation");
    }

    private static bool IsToolError(JsonElement result)
        => result.TryGetProperty("isError", out var isError)
            && isError.ValueKind == JsonValueKind.True;

    private async Task<HttpResponseMessage> CallAsync(string body, string bearer)
    {
        var content = new StringContent(body, Encoding.UTF8);
        content.Headers.ContentType = new MediaTypeHeaderValue(JsonMediaType);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp") { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        return await _client.SendAsync(request);
    }

    private static string CreateToken(string? scope)
    {
        var securityKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(SigningKey));
        var credentials = new SigningCredentials(securityKey, SecurityAlgorithms.HmacSha256);

        var claims = new List<Claim>
        {
            new("sub", "operator-123"),
            new("name", "Scope Test User"),
            // Admin role so the operator grant check always passes; this isolates the scope
            // decision so a denial can only come from scope narrowing.
            new("roles", "admin"),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
        };

        if (scope is not null)
        {
            claims.Add(new Claim("scope", scope));
        }

        var token = new JwtSecurityToken(
            issuer: Issuer,
            audience: Audience,
            claims: claims,
            expires: DateTime.UtcNow.AddMinutes(60),
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response)
    {
        var stream = await response.Content.ReadAsStreamAsync();
        return await JsonDocument.ParseAsync(stream);
    }
}
