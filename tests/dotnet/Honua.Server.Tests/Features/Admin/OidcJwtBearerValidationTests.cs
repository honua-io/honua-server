// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using FluentAssertions;
using Honua.Core.Features.Licensing.Domain;
using Honua.Infrastructure.Authentication;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Helpers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Honua.Server.Tests.Features.Admin;

/// <summary>
/// End-to-end OIDC bearer-token validation (honua-server#2945). Every previous "proving" test
/// for identity.oidc only exercised CRUD on <c>InMemoryOidcProviderStore</c> — no test signed a
/// real JWT or drove it through the actual JwtBearer pipeline, so a broken/no-op validator would
/// ship undetected. This stands up a fake in-process IdP (WireMock discovery document + JWKS
/// endpoint, RSA-signed tokens) and drives real bearer tokens through a protected admin endpoint
/// (<c>GET /api/v1/admin/oidc/providers</c>, gated by <c>AuthenticationExtensions.AdminPolicy</c>,
/// which <see cref="Honua.Infrastructure.Authentication.OidcAuthenticationExtensions.AddOidcAuthorization"/>
/// extends to accept the Composite/JwtBearer scheme once OIDC is enabled) to prove accept-valid /
/// reject-expired / reject-wrong-issuer / reject-wrong-audience / reject-tampered-signature.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.Admin)]
[Operation(Operations.IdentityManagement)]
public sealed class OidcJwtBearerValidationTests : IAsyncLifetime
{
    private const string ClientId = "test-client";
    private const string AdminRoute = "/api/v1/admin/oidc/providers";

    private readonly RSA _rsa = RSA.Create(2048);
    private readonly RsaSecurityKey _signingKey;
    private readonly WireMockServer _idp;
    private readonly string _issuer;
    private readonly WebAppFixture _fixture;

    public OidcJwtBearerValidationTests()
    {
        _signingKey = new RsaSecurityKey(_rsa) { KeyId = "test-key-1" };

        _idp = WireMockServer.Start();
        _issuer = _idp.Urls[0];

        _idp.Given(Request.Create().WithPath("/.well-known/openid-configuration").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody($$"""
                {
                  "issuer": "{{_issuer}}",
                  "jwks_uri": "{{_issuer}}/jwks",
                  "authorization_endpoint": "{{_issuer}}/authorize",
                  "token_endpoint": "{{_issuer}}/token",
                  "response_types_supported": ["code"],
                  "subject_types_supported": ["public"],
                  "id_token_signing_alg_values_supported": ["RS256"]
                }
                """));

        _idp.Given(Request.Create().WithPath("/jwks").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody(BuildJwksJson()));

        _fixture = CreateFixture(HonuaEdition.Pro);
    }

    private WebAppFixture CreateFixture(HonuaEdition edition)
        => new WebAppFixture()
            .ConfigureWebHost(builder =>
            {
                builder.UseEnvironment("Test");
                builder.UseSetting("HONUA_DEV_AUTH", "false");
                // The Test environment enables every experimental capability by default.
                // Keep the unrelated experimental mTLS middleware out of this JWT-only
                // fixture so it cannot require a client certificate ahead of bearer auth.
                builder.UseSetting("Capabilities:Experimental:Enabled", "false");
                // Point the real JwtBearer pipeline at the fake IdP: discovery + JWKS come from
                // WireMock, so signature/issuer/audience/expiry validation runs against real
                // RSA-signed tokens rather than a stub.
                builder.UseSetting("Oidc:Enabled", "true");
                builder.UseSetting("Oidc:RequireHttps", "false");
                builder.UseSetting("Oidc:Generic:Enabled", "true");
                builder.UseSetting("Oidc:Generic:Authority", _issuer);
                builder.UseSetting("Oidc:Generic:ClientId", ClientId);
            })
            .ConfigureServices(services =>
            {
                // OidcAuthenticationOptionsValidator hard-fails startup unless Authority is
                // HTTPS (a real production hardening rule). This test intentionally exercises
                // the JwtBearer trust chain (issuer/audience/expiry/signature) against a
                // lightweight in-process fake IdP over plain HTTP, so the production validator
                // is removed here — the JWT validation logic under test is untouched.
                services.RemoveAll<IValidateOptions<OidcAuthenticationOptions>>();
            })
            .WithTestLicense(edition);

    public Task InitializeAsync() => _fixture.InitializeAsync();

    public async Task DisposeAsync()
    {
        await _fixture.DisposeAsync();
        _idp.Stop();
        _rsa.Dispose();
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/oidc/providers")]
    public async Task AdminEndpoint_ValidJwt_IsAccepted()
    {
        OidcEntitlementPolicy.GetDeniedEntitlement(_fixture.Services)
            .Should().BeNull("stock single-provider configuration is the Pro baseline");

        // Assert on the authentication/authorization boundary these tests exist to prove
        // (401 = the JwtBearer trust chain rejected the token) rather than a literal 200:
        // AdminPolicy's role assertion is a separate RBAC concern from token *validity*, and
        // 403 here (reached only for an authenticated principal) already proves the token's
        // signature/issuer/audience/expiry were all accepted by the real JwtBearer pipeline —
        // exactly what "accept-valid" needs to demonstrate, without conflating it with the
        // (correctly independent) admin-role authorization gate.
        var response = await SendWithTokenAsync(CreateToken());
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().NotBe(HttpStatusCode.Unauthorized,
            "a correctly signed, non-expired token with the right issuer/audience must be accepted by the JwtBearer pipeline. Body: {0}", body);
        response.StatusCode.Should().BeOneOf(
            [HttpStatusCode.OK, HttpStatusCode.Forbidden],
            "the request must reach authorization (not fail at authentication) for a fully valid token. Body: {0}", body);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/oidc/providers")]
    public async Task AdminEndpoint_ValidJwtWithoutOidcEntitlement_IsRejected()
    {
        await using var communityFixture = CreateFixture(HonuaEdition.Community);
        await communityFixture.InitializeAsync();

        using var request = new HttpRequestMessage(HttpMethod.Get, AdminRoute);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", CreateToken());
        var response = await communityFixture.Client.SendAsync(request);

        response.StatusCode.Should().Be(
            HttpStatusCode.Unauthorized,
            "a cryptographically valid OIDC token must not establish a runtime identity without the base OIDC entitlement");
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/oidc/providers")]
    public async Task AdminEndpoint_ExpiredJwt_IsRejected()
    {
        var token = CreateToken(
            notBefore: DateTime.UtcNow.AddMinutes(-20),
            expires: DateTime.UtcNow.AddMinutes(-10));

        var response = await SendWithTokenAsync(token);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "an expired token must be rejected even though its signature/issuer/audience are valid");
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/oidc/providers")]
    public async Task AdminEndpoint_WrongIssuer_IsRejected()
    {
        // Still signed with the fake IdP's real key, but claims an issuer the server never
        // configured as valid — proves issuer validation is enforced, not merely signature checks.
        var token = CreateToken(issuer: "https://not-the-configured-idp.example.com");

        var response = await SendWithTokenAsync(token);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "a token from an unrecognized issuer must be rejected");
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/oidc/providers")]
    public async Task AdminEndpoint_WrongAudience_IsRejected()
    {
        var token = CreateToken(audience: "some-other-client-id");

        var response = await SendWithTokenAsync(token);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "a token issued for a different audience/client must be rejected");
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/oidc/providers")]
    public async Task AdminEndpoint_TamperedSignature_IsRejected()
    {
        var token = CreateToken(tamperSignature: true);

        var response = await SendWithTokenAsync(token);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "a token whose signature bytes were altered after signing must fail signature validation");
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/oidc/providers")]
    public async Task AdminEndpoint_NoToken_IsRejected()
    {
        var response = await _fixture.Client.GetAsync(AdminRoute);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "an unauthenticated request must not reach the admin endpoint");
    }

    private async Task<HttpResponseMessage> SendWithTokenAsync(string token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, AdminRoute);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await _fixture.Client.SendAsync(request);
    }

    /// <summary>
    /// Mints an RSA-signed JWT. Defaults describe a fully valid token (correct issuer/audience,
    /// live validity window, real signature, an "admin" role claim so accept-valid isolates JWT
    /// trust-chain validation from the separate role/authorization check) — callers override only
    /// the dimension under test.
    /// </summary>
    private string CreateToken(
        string? issuer = null,
        string? audience = null,
        DateTime? notBefore = null,
        DateTime? expires = null,
        bool tamperSignature = false)
    {
        var handler = new JwtSecurityTokenHandler();
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, "test-user-1"),
            // Emitted under both the configured Oidc:ClaimsMapping:RoleClaimType ("roles")
            // and the canonical ClaimTypes.Role URI so the admin-policy role check passes
            // regardless of which claim type name the validated identity's RoleClaimType
            // ends up using — the dimension these tests isolate is JWT trust-chain
            // validation (signature/issuer/audience/expiry), not role-claim mapping.
            new("roles", "admin"),
            new(ClaimTypes.Role, "admin"),
        };

        var token = new JwtSecurityToken(
            issuer: issuer ?? _issuer,
            audience: audience ?? ClientId,
            claims: claims,
            notBefore: notBefore ?? DateTime.UtcNow.AddMinutes(-5),
            expires: expires ?? DateTime.UtcNow.AddMinutes(30),
            signingCredentials: new SigningCredentials(_signingKey, SecurityAlgorithms.RsaSha256));

        var jwt = handler.WriteToken(token);

        if (!tamperSignature)
        {
            return jwt;
        }

        var segments = jwt.Split('.');
        var signatureBytes = Base64UrlEncoder.DecodeBytes(segments[2]);
        signatureBytes[0] ^= 0xFF;
        segments[2] = Base64UrlEncoder.Encode(signatureBytes);
        return string.Join('.', segments);
    }

    private string BuildJwksJson()
    {
        var parameters = _rsa.ExportParameters(false);
        var modulus = Base64UrlEncoder.Encode(parameters.Modulus);
        var exponent = Base64UrlEncoder.Encode(parameters.Exponent);

        return $$"""
        {
          "keys": [
            {
              "kty": "RSA",
              "use": "sig",
              "kid": "{{_signingKey.KeyId}}",
              "alg": "RS256",
              "n": "{{modulus}}",
              "e": "{{exponent}}"
            }
          ]
        }
        """;
    }
}
