// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using FluentAssertions;
using Honua.Infrastructure.Authentication;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Honua.Server.Tests.Infrastructure.Authentication;

/// <summary>
/// Unit tests for <see cref="OidcPortalCredentialVerifier"/> — the OIDC-backed
/// portal credential verifier (#1370). Proves a named-user OIDC token validated by
/// the shared OIDC core projects onto the same <c>PrincipalId</c> / <c>DisplayName</c>
/// / <c>TenantId</c> / <c>Roles</c> that a direct OIDC session yields, and that the
/// verifier is off-by-default and rejects invalid tokens. Uses a static symmetric
/// signing key so validation runs without IdP metadata discovery.
/// </summary>
[SecurityTest]
[Protocol(TestProtocols.FeatureServer)]
[Operation(Operations.Security)]
public sealed class OidcPortalCredentialVerifierTests
{
    private const string SigningKey = "ThisIsATestSigningKeyThatIsLongEnoughForHS256Algorithm!";
    private const string Issuer = "https://idp.example.com";
    private const string Audience = "honua-portal-client";

    [UnitTest]
    public async Task VerifyAsync_ValidOidcToken_ProjectsPrincipalIdNameTenantAndRoles()
    {
        var verifier = CreateVerifier(enabled: true);
        var token = CreateToken(
            subject: "user-123",
            name: "Ada Lovelace",
            roles: ["editor", "viewer"],
            tenantId: "tenant-A");

        var result = await verifier.VerifyAsync("ada", token, CancellationToken.None);

        result.Should().NotBeNull();
        result!.PrincipalId.Should().Be("user-123");
        result.DisplayName.Should().Be("Ada Lovelace");
        result.TenantId.Should().Be("tenant-A");
        result.Roles.Should().Contain("editor").And.Contain("viewer");
    }

    [UnitTest]
    public async Task VerifyAsync_Disabled_ReturnsNull()
    {
        var verifier = CreateVerifier(enabled: false);
        var token = CreateToken(subject: "user-123", name: "Ada", roles: ["editor"], tenantId: null);

        var result = await verifier.VerifyAsync("ada", token, CancellationToken.None);

        result.Should().BeNull();
    }

    [UnitTest]
    public async Task VerifyAsync_WrongSigningKey_ReturnsNull()
    {
        var verifier = CreateVerifier(enabled: true);
        var token = CreateToken(
            subject: "user-123",
            name: "Ada",
            roles: ["editor"],
            tenantId: null,
            signingKey: "AnEntirelyDifferentSigningKeyThatIsAlsoLongEnoughForHS256!");

        var result = await verifier.VerifyAsync("ada", token, CancellationToken.None);

        result.Should().BeNull();
    }

    [UnitTest]
    public async Task VerifyAsync_GarbageCredential_ReturnsNull()
    {
        var verifier = CreateVerifier(enabled: true);

        var result = await verifier.VerifyAsync("ada", "not-a-jwt", CancellationToken.None);

        result.Should().BeNull();
    }

    private static OidcPortalCredentialVerifier CreateVerifier(bool enabled)
    {
        var options = new OidcAuthenticationOptions
        {
            Enabled = enabled,
            RequireHttps = false,
            Generic = new GenericOidcProviderOptions
            {
                Enabled = true,
                Authority = Issuer,
                ClientId = Audience,
            },
            TokenValidation = new TokenValidationOptions
            {
                SymmetricSigningKey = SigningKey,
                ValidIssuers = [Issuer],
                ValidAudiences = [Audience],
                // The replay/lifetime defaults are fine; metadata discovery is
                // bypassed by the static symmetric key.
            },
        };

        var wrapped = Options.Create(options);
        var transformation = new OidcClaimsTransformation(wrapped, NullLogger<OidcClaimsTransformation>.Instance);
        return new OidcPortalCredentialVerifier(
            wrapped,
            transformation,
            NullLogger<OidcPortalCredentialVerifier>.Instance);
    }

    private static string CreateToken(
        string subject,
        string name,
        string[] roles,
        string? tenantId,
        string? signingKey = null)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey ?? SigningKey));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var claims = new List<Claim>
        {
            new("sub", subject),
            new("name", name),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
        };

        foreach (var role in roles)
        {
            claims.Add(new Claim("roles", role));
        }

        if (tenantId is not null)
        {
            claims.Add(new Claim("tenant_id", tenantId));
        }

        var token = new JwtSecurityToken(
            issuer: Issuer,
            audience: Audience,
            claims: claims,
            expires: DateTime.UtcNow.AddMinutes(30),
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
