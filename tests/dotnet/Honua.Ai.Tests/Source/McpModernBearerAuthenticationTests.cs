// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Security;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using static Honua.Server.Tests.Features.Protocols.Mcp.McpBearerAuthenticationTests;

namespace Honua.Server.Tests.Features.Protocols.Mcp;

[Collection("Database")]
[Protocol(TestProtocols.Mcp)]
[Operation(Operations.Security)]
public sealed class McpModernBearerAuthenticationTests : IAsyncLifetime
{
    private static readonly Claim[] Authority =
    [
        new("tid", "tenant-a"), new("roles", "admin"), new("scope", "honua.mcp.full")
    ];
    private readonly WebAppFixture _fixture = CreateReplayProtectedFixture().ConfigureServices(services =>
        services.PostConfigure<CorsOptions>(options => options.AddPolicy(HttpCorsPolicyNames.Production,
            policy => policy.WithOrigins("https://approved.example"))));

    public Task InitializeAsync() => _fixture.InitializeAsync();
    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTheory]
    [InlineData("modern", "modern")]
    [InlineData("modern", "http")]
    [InlineData("http", "modern")]
    [InlineData("legacy", "modern")]
    [InlineData("modern", "legacy")]
    [Endpoint("POST /mcp")]
    [Endpoint("GET /api/v1/studio/package-families")]
    [InterfaceOperation(TestProtocols.Mcp, "server/discover")]
    public async Task ReplayProtectionEnabled_BearerReuse_IsAllowedOnlyWithinTheSameModernSurface(string firstSurface, string secondSurface)
    {
        var token = CreateToken("modern-replay-owner", additionalClaims: Authority);
        using var first = BuildReplayProbe(firstSurface, token);
        using var admitted = await _fixture.Client.SendAsync(first);
        admitted.StatusCode.Should().Be(HttpStatusCode.OK);
        using var second = BuildReplayProbe(secondSurface, token);
        if (admitted.Headers.TryGetValues("Mcp-Session-Id", out var session))
        {
            second.Headers.Add("Mcp-Session-Id", session);
        }
        using var response = await _fixture.Client.SendAsync(second);
        response.StatusCode.Should().Be(firstSurface == secondSurface ? HttpStatusCode.OK : HttpStatusCode.Unauthorized);
    }

    [IntegrationTheory]
    [InlineData(null)]
    [InlineData("")]
    [Endpoint("POST /mcp")]
    [InterfaceOperation(TestProtocols.Mcp, "server/discover")]
    [InterfaceOperation(TestProtocols.Mcp, "tools/list")]
    [InterfaceOperation(TestProtocols.Mcp, "tools/call")]
    public async Task Post_SameValidatedToken_DiscoversListsAndCallsWithoutSession(string? tokenId)
    {
        var token = CreateToken("modern-tool-owner", additionalClaims: Authority, tokenId: tokenId);
        await AssertModernSuccessAsync("server/discover", token);
        var tools = await AssertModernSuccessAsync("tools/list", token);
        tools.GetProperty("tools").GetArrayLength().Should().BeGreaterThan(0);
        var called = await AssertModernSuccessAsync("tools/call", token, "honua_list_capabilities");
        called.GetProperty("isError").GetBoolean().Should().BeFalse();
        using var foreignSession = BuildModernRequest("tools/list", token);
        foreignSession.Headers.Add("Mcp-Session-Id", "foreign-session");
        using var foreignResponse = await _fixture.Client.SendAsync(foreignSession);
        foreignResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        foreignResponse.Headers.Contains("Mcp-Session-Id").Should().BeFalse();
        await AssertModernSuccessAsync("server/discover", token);
    }

    [IntegrationTheory]
    [InlineData(true)]
    [InlineData(false)]
    [Endpoint("POST /mcp")]
    public async Task Post_ModernMethodHeaderWithInitializeBody_NeverCreatesOrBindsSession(bool firstUse)
    {
        var token = CreateToken("initialize-header-owner", additionalClaims: Authority);
        if (!firstUse)
        {
            await AssertModernSuccessAsync("server/discover", token);
        }
        using var request = BuildInitialize(token);
        request.Headers.Add("MCP-Protocol-Version", "2026-07-28");
        request.Headers.Add("Mcp-Method", "server/discover");
        using var response = await _fixture.Client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        response.Headers.Contains("Mcp-Session-Id").Should().BeFalse();
        await AssertModernSuccessAsync("server/discover", token);
    }

    [IntegrationTheory]
    [InlineData(null)]
    [InlineData("initialize")]
    [Endpoint("POST /mcp")]
    [InterfaceOperation(TestProtocols.Mcp, "initialize")]
    public async Task Initialize_ModernVersionWithoutModernMethod_RetainsLegacyNegotiationAndContinuation(string? methodHeader)
    {
        var token = CreateToken("legacy-fallback-owner", additionalClaims: Authority);
        using var request = BuildInitialize(token);
        var body = (await request.Content!.ReadAsStringAsync()).Replace("2025-06-18", "2026-07-28", StringComparison.Ordinal);
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        request.Headers.Add("MCP-Protocol-Version", "2026-07-28");
        if (methodHeader is not null)
        {
            request.Headers.Add("Mcp-Method", methodHeader);
        }
        using var response = await _fixture.Client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var session = response.Headers.GetValues("Mcp-Session-Id").Single();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        document.RootElement.GetProperty("result").GetProperty("protocolVersion").GetString().Should().Be("2025-06-18");
        using var continued = BuildRpc("""{"jsonrpc":"2.0","id":2,"method":"tools/list"}""", session, token);
        using var continuation = await _fixture.Client.SendAsync(continued);
        continuation.StatusCode.Should().Be(HttpStatusCode.OK);
        using var modern = BuildModernDiscovery(token);
        using var refused = await _fixture.Client.SendAsync(modern);
        refused.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/studio/package-families")]
    [Endpoint("POST /mcp")]
    public async Task Get_OrdinaryHttpWithModernHeaders_RefusesModernToken()
    {
        var token = CreateToken("modern-route-owner", additionalClaims: Authority);
        await AssertModernSuccessAsync("server/discover", token);
        using var request = BuildPackageFamiliesRequest(token);
        request.Headers.Add("MCP-Protocol-Version", "2026-07-28");
        request.Headers.Add("Mcp-Method", "server/discover");
        using var response = await _fixture.Client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        await AssertModernSuccessAsync("server/discover", token);
    }

    [IntegrationTheory]
    [InlineData("GET")]
    [InlineData("DELETE")]
    [Endpoint("POST /mcp")]
    [Endpoint("GET /mcp")]
    [Endpoint("DELETE /mcp")]
    public async Task Request_ModernSessionVerbWithReusedBearer_Returns405AndPreservesScope(string method)
    {
        var token = CreateToken("modern-verb-owner", additionalClaims: Authority);
        await AssertModernSuccessAsync("server/discover", token);
        using var request = new HttpRequestMessage(new HttpMethod(method), "/mcp");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add("MCP-Protocol-Version", "2026-07-28");
        request.Headers.Add("Mcp-Session-Id", "foreign-session");
        using var response = await _fixture.Client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.MethodNotAllowed);
        await AssertModernSuccessAsync("server/discover", token);
    }

    [IntegrationTest]
    [Endpoint("POST /mcp")]
    public async Task Post_DifferentValidatedTokenWithSameIssuerAndId_IsNotReusable()
    {
        var id = Guid.NewGuid().ToString();
        var token = CreateToken("fingerprint-owner", additionalClaims: Authority, tokenId: id);
        var changed = CreateToken("fingerprint-owner", tokenId: id,
            additionalClaims: [.. Authority, new Claim("nonce", "different-signed-content")]);
        changed.Should().NotBe(token);
        var validationParameters = _fixture.Services.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>()
            .Get(OidcAuthenticationExtensions.JwtBearerScheme).TokenValidationParameters;
        var validation = await new JsonWebTokenHandler().ValidateTokenAsync(changed, validationParameters);
        validation.IsValid.Should().BeTrue();
        await AssertModernSuccessAsync("server/discover", token);
        using var request = BuildModernDiscovery(changed);
        using var response = await _fixture.Client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        await AssertModernSuccessAsync("server/discover", token);
    }

    [IntegrationTheory]
    [InlineData("malformed")]
    [InlineData("expired")]
    [InlineData("audience")]
    [InlineData("issuer")]
    [InlineData("signature")]
    [Endpoint("POST /mcp")]
    public async Task Post_InvalidOrOverLimitToken_IsRefused(string invalidity)
    {
        var token = invalidity switch
        {
            "expired" => CreateToken("invalid-owner", expiresInMinutes: -30),
            "audience" => CreateToken("invalid-owner", audience: "different-audience"),
            "issuer" => CreateToken("invalid-owner", issuer: "https://different-issuer.example"),
            "signature" => CreateToken("invalid-owner", signingKey: new string('x', 64)),
            _ => "invalid-token"
        };
        using var request = BuildModernDiscovery(token);
        using var response = await _fixture.Client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        response.Headers.Contains("Mcp-Session-Id").Should().BeFalse();
    }

    [IntegrationTheory]
    [InlineData("modern")]
    [InlineData("legacy")]
    [InlineData("http")]
    [Endpoint("POST /mcp")]
    [Endpoint("GET /api/v1/studio/package-families")]
    public async Task Request_TokenValidityExceedsRetentionMaximum_IsRefusedOnEverySurface(string surface)
    {
        var token = CreateToken("over-limit-owner", expiresInMinutes: 25 * 60, additionalClaims: Authority);
        using var request = BuildReplayProbe(surface, token);
        using var response = await _fixture.Client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [IntegrationTest]
    [Endpoint("POST /mcp")]
    public async Task Post_PreviouslyAdmittedTokenExpiresAtZeroSkew_IsRefusedOnReuse()
    {
        var fixture = CreateReplayProtectedFixture().ConfigureWebHost(builder =>
            builder.UseSetting("Oidc:TokenValidation:ClockSkew", "00:00:00"));
        await fixture.InitializeAsync();
        try
        {
            var token = CreateToken("expiring-owner", expiresInMinutes: 1, additionalClaims: Authority);
            using var first = BuildModernDiscovery(token);
            using var admitted = await fixture.Client.SendAsync(first);
            admitted.StatusCode.Should().Be(HttpStatusCode.OK);
            await Task.Delay(TimeSpan.FromSeconds(65));
            using var retry = BuildModernDiscovery(token);
            using var expired = await fixture.Client.SendAsync(retry);
            expired.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("POST /mcp")]
    public async Task Post_InvalidModernOriginWithInvalidBearer_Returns403BeforeAuthentication()
    {
        using var request = BuildModernDiscovery("invalid-token");
        request.Headers.Add("Origin", "https://unapproved.example");
        request.Content = new StringContent("{broken", Encoding.UTF8, "application/json");
        using var response = await _fixture.Client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await response.Content.ReadAsStringAsync()).Should().BeEmpty();
    }

    private async Task<JsonElement> AssertModernSuccessAsync(string method, string token, string? name = null)
    {
        using var request = BuildModernRequest(method, token, name);
        using var response = await _fixture.Client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.Contains("Mcp-Session-Id").Should().BeFalse();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        document.RootElement.TryGetProperty("error", out _).Should().BeFalse();
        var result = document.RootElement.GetProperty("result");
        result.GetProperty("resultType").GetString().Should().Be("complete");
        return result.Clone();
    }

    private static HttpRequestMessage BuildReplayProbe(string surface, string bearer) => surface switch
    {
        "http" => BuildPackageFamiliesRequest(bearer),
        "legacy" => BuildInitialize(bearer),
        _ => BuildModernDiscovery(bearer)
    };

    private static HttpRequestMessage BuildModernDiscovery(string bearer) => BuildModernRequest("server/discover", bearer);

    private static HttpRequestMessage BuildModernRequest(string method, string bearer, string? name = null)
    {
        var parameters = new JsonObject
        {
            ["_meta"] = new JsonObject
            {
                ["io.modelcontextprotocol/protocolVersion"] = "2026-07-28",
                ["io.modelcontextprotocol/clientCapabilities"] = new JsonObject()
            }
        };
        if (name is not null)
        {
            parameters["name"] = name;
            parameters["arguments"] = new JsonObject();
        }
        var body = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = Guid.NewGuid().ToString("N"), ["method"] = method, ["params"] = parameters };
        var request = BuildRpc(body.ToJsonString(), sessionId: null, bearer: bearer);
        request.Headers.Add("MCP-Protocol-Version", "2026-07-28");
        request.Headers.Add("Mcp-Method", method);
        if (name is not null)
        {
            request.Headers.Add("Mcp-Name", name);
        }
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        return request;
    }
}
