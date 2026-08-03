// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using FluentAssertions;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Licensing.Abstractions;
using Honua.Core.Features.Licensing.Domain;
using Honua.Infrastructure.Authentication;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Helpers;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
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

    [UnitTest]
    public async Task VerifyAndIssue_ReservedProvenanceMappingsCannotPersistInjectedAdminAfterExpiry()
    {
        var entitlements = new MutableLicenseEntitlementService(HonuaEdition.Enterprise);
        using var services = new ServiceCollection()
            .AddSingleton<ILicenseEntitlementService>(entitlements)
            .BuildServiceProvider();
        var verifier = CreateVerifier(
            enabled: true,
            services,
            new ClaimsMappingOptions
            {
                CustomMappings = new Dictionary<string, string>
                {
                    ["forged_mapping_marker"] = OidcClaimsTransformation.RolesFromClaimsMappingClaimType,
                    ["forged_fallback_role"] = OidcClaimsTransformation.RolesWithoutClaimsMappingClaimType,
                    ["forged_tenant_marker"] = OidcClaimsTransformation.TenantFromClaimsMappingClaimType,
                },
            });
        var oidcToken = CreateToken(
            subject: "user-123",
            name: "Ada",
            roles: ["viewer"],
            tenantId: "tenant-A",
            additionalClaims:
            [
                new Claim("forged_mapping_marker", "1"),
                new Claim("forged_fallback_role", "admin"),
                new Claim("forged_tenant_marker", "tenant_id"),
            ]);

        var verified = await verifier.VerifyAsync("ada", oidcToken, CancellationToken.None);

        verified.Should().NotBeNull();
        verified!.Roles.Should().Equal("viewer");
        verified.RolesRequireClaimsMappingEntitlement.Should().BeFalse();
        verified.RolesWithoutClaimsMapping.Should().BeNull();
        verified.TenantRequiresClaimsMappingEntitlement.Should().BeFalse();

        var issuer = new PortalTokenIssuer(
            new MemoryCache(new MemoryCacheOptions()),
            NullLogger<PortalTokenIssuer>.Instance,
            serviceProvider: services);
        var issuance = await issuer.IssueAsync(
            new PortalTokenIssueRequest(
                verified.PrincipalId,
                verified.DisplayName,
                verified.TenantId,
                verified.Roles,
                PortalTokenClientType.Ip,
                "192.0.2.12",
                DateTimeOffset.UtcNow.AddMinutes(30),
                verified.RolesRequireClaimsMappingEntitlement,
                verified.TenantRequiresClaimsMappingEntitlement,
                verified.RolesWithoutClaimsMapping),
            CancellationToken.None);

        entitlements.Expire();

        var validation = await issuer.ValidateAsync(
            issuance.Token,
            new PortalTokenBinding(Referer: null, ClientIp: "192.0.2.12"),
            CancellationToken.None);
        var introspection = await issuer.IntrospectAsync(issuance.Token, CancellationToken.None);

        validation.Should().NotBeNull();
        validation!.Principal.IsInRole("viewer").Should().BeTrue();
        validation.Principal.IsInRole("admin").Should().BeFalse();
        validation.Principal.FindFirstValue(PortalTokenIssuer.TenantClaimType).Should().Be("tenant-A");
        introspection.Should().NotBeNull();
        introspection!.Roles.Should().Equal("viewer");
        introspection.TenantId.Should().Be("tenant-A");
    }


    /// <summary>
    /// Service provider carrying an Enterprise-entitled license so claims transformation tests
    /// exercise the pre-#2997 behavior (custom claims mapping applied); the unentitled path is
    /// covered by OidcClaimsMappingEntitlementTests.
    /// </summary>
    private static ServiceProvider EnterpriseEntitledServices()
        => new ServiceCollection()
            .AddSingleton<Honua.Core.Features.Licensing.Abstractions.ILicenseEntitlementService>(
                new Honua.TestKit.Helpers.TestLicenseEntitlementService(Honua.Core.Features.Licensing.Domain.HonuaEdition.Enterprise))
            .BuildServiceProvider();

    private static OidcPortalCredentialVerifier CreateVerifier(
        bool enabled,
        IServiceProvider? services = null,
        ClaimsMappingOptions? claimsMapping = null)
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
            ClaimsMapping = claimsMapping ?? new ClaimsMappingOptions(),
        };

        var wrapped = Options.Create(options);
        var transformation = new OidcClaimsTransformation(
            wrapped,
            NullLogger<OidcClaimsTransformation>.Instance,
            services ?? EnterpriseEntitledServices());
        return new OidcPortalCredentialVerifier(
            wrapped,
            transformation,
            new OidcConfigurationManagerCache(),
            NullLogger<OidcPortalCredentialVerifier>.Instance);
    }

    private static string CreateToken(
        string subject,
        string name,
        string[] roles,
        string? tenantId,
        string? signingKey = null,
        IReadOnlyCollection<Claim>? additionalClaims = null)
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

        if (additionalClaims is not null)
        {
            claims.AddRange(additionalClaims);
        }

        var token = new JwtSecurityToken(
            issuer: Issuer,
            audience: Audience,
            claims: claims,
            expires: DateTime.UtcNow.AddMinutes(30),
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private sealed class MutableLicenseEntitlementService : ILicenseEntitlementService
    {
        private LicenseSnapshot _snapshot;

        public MutableLicenseEntitlementService(HonuaEdition edition)
            => _snapshot = LicenseTestSupport.CreateSnapshot(edition);

        public void Expire()
            => _snapshot = LicenseTestSupport.CreateSnapshot(
                HonuaEdition.Community,
                LicenseValidationState.Expired,
                entitlements: []);

        public LicenseSnapshot GetSnapshot() => _snapshot;

        public LicenseEntitlementDecision CheckEntitlement(string entitlementKey)
        {
            var active = _snapshot.HasEntitlement(entitlementKey);
            return new LicenseEntitlementDecision(
                entitlementKey,
                active,
                _snapshot.Edition,
                _snapshot.ValidationState,
                RequiredEdition: null,
                UpgradeMessage: active ? string.Empty : $"'{entitlementKey}' is not active.");
        }
    }
}
