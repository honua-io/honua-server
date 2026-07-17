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
/// Integration tests for OAuth 2.1 resource-server acceptance of
/// <c>Authorization: Bearer</c> tokens on the <c>/mcp</c> transport
/// (honua-server#2850). Exercised through the full ASP.NET Core pipeline with a
/// configured OIDC authority (a symmetric-key generic provider, so tokens are
/// minted and validated in-process without a live IdP) and the dev-auth bypass
/// disabled, so authentication is decided purely by the presented credential.
/// </summary>
/// <remarks>
/// The suite proves the three behaviours the acceptance criteria require: a valid
/// token authenticates the caller (observed through the session principal binding,
/// which only rejects a *different* identity), a presented-but-invalid token is
/// rejected with HTTP 401 plus the RFC 9728 <c>WWW-Authenticate</c> challenge and an
/// MCP-structured error, and an unauthenticated request keeps its prior anonymous
/// handshake — the change is additive.
/// </remarks>
[Collection("Database")]
[Protocol(TestProtocols.Mcp)]
[Operation(Operations.Security)]
public sealed class McpBearerAuthenticationTests : IAsyncLifetime
{
    private const string JsonMediaType = "application/json";
    private const string PublicBaseUrl = "https://mcp.example.com";
    private const string Issuer = "https://idp.example.com";
    private const string Audience = "honua-mcp-client-id";
    private const string SigningKey = "mcp-bearer-test-signing-key-at-least-32-characters-long";

    private readonly WebAppFixture _fixture = new WebAppFixture()
        .UseSeed("tests/seed/server.yaml")
        .ConfigureWebHost(builder =>
        {
            builder.UseEnvironment("Test");
            // Disable the dev-auth bypass so a request is only authenticated by the
            // credential it actually presents — the whole point of this suite.
            builder.UseSetting("HONUA_DEV_AUTH", "false");
            builder.UseSetting("HONUA_ADMIN_PASSWORD", "test-admin-key");
            builder.UseSetting("Public:BaseUrl", PublicBaseUrl);

            // Reuse the multi-authority OIDC stack (#2849) — a single generic
            // provider validated against a static symmetric key so tokens can be
            // minted in-process. Issuer/audience/lifetime validation stay at their
            // secure defaults (true).
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
    [InterfaceOperation(TestProtocols.Mcp, "initialize")]
    public async Task Post_InitializeWithValidBearer_IsAcceptedAndBindsAuthenticatedPrincipal()
    {
        // A valid token authenticates the caller: initialize succeeds (not 401) and
        // the resulting session is bound to the token's subject, not the anonymous
        // principal. The binding is observed by presenting the issued session id on a
        // follow-up request that carries NO credential — the anonymous identity does
        // not match the authenticated subject, so the transport returns the A3
        // principal-mismatch error. That mismatch is only possible if the bearer
        // authenticated initialize to a non-anonymous identity.
        var token = CreateToken(subject: "operator-123");

        using var initialize = BuildInitialize(bearer: token);
        var initializeResponse = await _client.SendAsync(initialize);

        initializeResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        initializeResponse.Headers.TryGetValues("Mcp-Session-Id", out var sessionIds).Should().BeTrue(
            "a valid bearer authenticates the caller and initialize establishes a session");
        var sessionId = sessionIds!.Single();

        using var followUp = BuildRpc(
            """{"jsonrpc":"2.0","id":2,"method":"tools/list"}""",
            sessionId: sessionId,
            bearer: null);
        var followUpResponse = await _client.SendAsync(followUp);

        followUpResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = await ReadJsonAsync(followUpResponse);
        var error = document.RootElement.GetProperty("error");
        error.GetProperty("data").GetProperty("code").GetString().Should().Be(
            "permission_denied",
            "the session was bound to the bearer subject, so an anonymous follow-up is a principal mismatch");
        error.GetProperty("data").GetProperty("requiresReauthentication").GetBoolean().Should().BeTrue();
    }

    [IntegrationTest]
    [Endpoint("POST /mcp")]
    public async Task Post_WithInvalidSignatureBearer_Returns401WithChallengeAndStructuredError()
    {
        // A token signed with the wrong key fails signature validation. A presented
        // token that fails validation is an RFC 6750 invalid_token rejection: HTTP 401
        // with the RFC 9728 WWW-Authenticate challenge and an MCP-structured body.
        var token = CreateToken(subject: "operator-123", signingKey: "a-different-wrong-signing-key-also-32-characters");

        using var request = BuildInitialize(bearer: token);
        var response = await _client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        response.Headers.TryGetValues("WWW-Authenticate", out var challenges).Should().BeTrue(
            "an OAuth 2.1 resource server signals rejection with a WWW-Authenticate challenge");
        var challenge = string.Join(" ", challenges!);
        challenge.Should().Contain("Bearer");
        challenge.Should().Contain(
            "resource_metadata=",
            "RFC 9728 section 5.1 points the client at the protected-resource metadata");

        using var document = await ReadJsonAsync(response);
        var error = document.RootElement.GetProperty("error");
        error.GetProperty("data").GetProperty("code").GetString().Should().Be("unauthenticated");
        error.GetProperty("data").GetProperty("requiresReauthentication").GetBoolean().Should().BeTrue();
    }

    [IntegrationTest]
    [Endpoint("POST /mcp")]
    public async Task Post_WithExpiredBearer_Returns401()
    {
        var token = CreateToken(subject: "operator-123", expiresInMinutes: -30);

        using var request = BuildInitialize(bearer: token);
        var response = await _client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        using var document = await ReadJsonAsync(response);
        document.RootElement.GetProperty("error").GetProperty("data").GetProperty("code").GetString()
            .Should().Be("unauthenticated");
    }

    [IntegrationTest]
    [Endpoint("POST /mcp")]
    public async Task Post_WithTokenForDifferentAudience_Returns401()
    {
        // Audience validation binds the token to this resource: a token minted for a
        // different audience (a different client/resource) must be rejected even
        // though its signature, issuer, and lifetime are all valid.
        var token = CreateToken(subject: "operator-123", audience: "some-other-resource");

        using var request = BuildInitialize(bearer: token);
        var response = await _client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        using var document = await ReadJsonAsync(response);
        document.RootElement.GetProperty("error").GetProperty("data").GetProperty("code").GetString()
            .Should().Be("unauthenticated");
    }

    [IntegrationTest]
    [Endpoint("POST /mcp")]
    [InterfaceOperation(TestProtocols.Mcp, "initialize")]
    public async Task Post_WithoutBearer_AnonymousHandshakeStillSucceeds()
    {
        // The change is additive: with no Authorization header the anonymous
        // initialize/tools-list handshake keeps working exactly as before, and the
        // anonymous session accepts follow-up requests without a principal mismatch.
        using var initialize = BuildInitialize(bearer: null);
        var initializeResponse = await _client.SendAsync(initialize);

        initializeResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        initializeResponse.Headers.TryGetValues("Mcp-Session-Id", out var sessionIds).Should().BeTrue();
        var sessionId = sessionIds!.Single();

        using var followUp = BuildRpc(
            """{"jsonrpc":"2.0","id":2,"method":"tools/list"}""",
            sessionId: sessionId,
            bearer: null);
        var followUpResponse = await _client.SendAsync(followUp);

        followUpResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = await ReadJsonAsync(followUpResponse);
        document.RootElement.TryGetProperty("error", out _).Should().BeFalse(
            "an anonymous session accepts the same anonymous caller");
    }

    private static string CreateToken(
        string subject,
        string issuer = Issuer,
        string audience = Audience,
        string signingKey = SigningKey,
        int expiresInMinutes = 60)
    {
        var securityKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey));
        var credentials = new SigningCredentials(securityKey, SecurityAlgorithms.HmacSha256);

        var claims = new List<Claim>
        {
            new("sub", subject),
            new("name", "Bearer Test User"),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())
        };

        var token = new JwtSecurityToken(
            issuer: issuer,
            audience: audience,
            claims: claims,
            notBefore: DateTime.UtcNow.AddMinutes(-1),
            expires: DateTime.UtcNow.AddMinutes(expiresInMinutes),
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private static HttpRequestMessage BuildInitialize(string? bearer) => BuildRpc(
        """
        {"jsonrpc":"2.0","id":1,"method":"initialize","params":{
            "protocolVersion":"2025-06-18",
            "capabilities":{},
            "clientInfo":{"name":"honua-tests","version":"1.0.0"}
        }}
        """,
        sessionId: null,
        bearer: bearer);

    private static HttpRequestMessage BuildRpc(string body, string? sessionId, string? bearer)
    {
        var content = new StringContent(body, Encoding.UTF8);
        content.Headers.ContentType = new MediaTypeHeaderValue(JsonMediaType);

        var request = new HttpRequestMessage(HttpMethod.Post, "/mcp") { Content = content };
        if (sessionId is not null)
        {
            request.Headers.Add("Mcp-Session-Id", sessionId);
        }

        if (bearer is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        }

        return request;
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response)
    {
        var stream = await response.Content.ReadAsStreamAsync();
        return await JsonDocument.ParseAsync(stream);
    }
}
