// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using FluentAssertions;
using Honua.Core.Features.Security;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Security;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Features.Admin;

/// <summary>
/// Unit tests for the console-consumable operator bearer issuer/validator (#2258).
/// </summary>
public sealed class OperatorBearerTokenServiceTests
{
    private const string SigningKey = "operator-bearer-signing-key-at-least-32-bytes-long";

    private static readonly IReadOnlyList<AdminAuthSessionClaim> SampleClaims =
    [
        new AdminAuthSessionClaim { Type = "name", Value = "Operator Admin" },
        new AdminAuthSessionClaim { Type = ClaimTypes.NameIdentifier, Value = "operator-1" },
        new AdminAuthSessionClaim { Type = "iss", Value = "https://identity.example/tenant-a" },
        new AdminAuthSessionClaim { Type = "auth_type", Value = "oidc" },
        new AdminAuthSessionClaim { Type = IdentityProtocolProvenance.ClaimType, Value = IdentityProtocolProvenance.Oidc },
        new AdminAuthSessionClaim { Type = ClaimTypes.Role, Value = "admin" }
    ];

    [Fact]
    public async Task IssueThenValidate_RoundTripsOperatorClaims()
    {
        var service = CreateService();

        var issuance = service.Issue(SampleClaims, DateTimeOffset.UtcNow.AddMinutes(10));

        issuance.Should().NotBeNull();
        var claims = await service.TryValidateAsync(issuance!.Token);

        claims.Should().NotBeNull();
        claims!.Should().Contain(c => c.Type == "name" && c.Value == "Operator Admin");
        claims.Should().Contain(c => c.Type == ClaimTypes.Role && c.Value == "admin");
        claims.Should().Contain(c => c.Type == ClaimTypes.NameIdentifier && c.Value == "operator-1");
        claims.Should().ContainSingle(c => c.Type == OperatorBearerTokenService.IdentityIssuerClaimType
            && c.Value == "https://identity.example/tenant-a");
        claims.Should().NotContain(c => c.Type == "iss",
            "the wrapper issuer is validated separately and must not replace upstream identity provenance");

        var principal = AdminAuthClaimsProjector.CreatePrincipal(
            claims,
            "OperatorBearer",
            "operator-bearer");
        var actor = CanonicalSecurityActor.Resolve(principal);
        actor.Should().NotBeNull();
        actor!.ActorId.Should().Be(
            "oidc:subject:https%3A%2F%2Fidentity.example%2Ftenant-a:operator-1");
        actor.SubjectIssuer.Should().Be("https://identity.example/tenant-a");
    }

    [Fact]
    public void CanonicalSecurityActor_OidcSubjectWithoutIssuerFailsClosed()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, "shared-subject"),
                new Claim("auth_type", "oidc"),
                new Claim(IdentityProtocolProvenance.ClaimType, IdentityProtocolProvenance.Oidc),
            ],
            authenticationType: "oidc"));

        CanonicalSecurityActor.Resolve(principal).Should().BeNull();
        CanonicalSecurityActor.IsBoundIdentity(
            "oidc:subject:-:shared-subject",
            "oidc",
            "shared-subject",
            subjectIssuer: null,
            apiKeyId: null,
            credentialKind: null).Should().BeFalse();
    }

    [Fact]
    public async Task OidcProjection_OverridesForgedSamlAuthTypeBeforeOperatorWrapping()
    {
        var sourceClaims = new Claim[]
        {
            new(ClaimTypes.NameIdentifier, "shared-subject"),
            new("iss", "https://oidc.example/tenant-a"),
            new("auth_type", "saml"),
            new(IdentityProtocolProvenance.ClaimType, IdentityProtocolProvenance.Saml),
        };

        AdminAuthClaimsProjector.TryProjectValidatedClaims(sourceClaims, out var sessionClaims)
            .Should().BeTrue();
        sessionClaims.Should().ContainSingle(claim => claim.Type == "auth_type")
            .Which.Value.Should().Be(IdentityProtocolProvenance.Oidc);
        sessionClaims.Should().ContainSingle(
                claim => claim.Type == IdentityProtocolProvenance.ClaimType)
            .Which.Value.Should().Be(IdentityProtocolProvenance.Oidc);

        var service = CreateService();
        var issuance = service.Issue(sessionClaims, DateTimeOffset.UtcNow.AddMinutes(10));
        var wrapperClaims = await service.TryValidateAsync(issuance!.Token);
        var wrapper = AdminAuthClaimsProjector.CreatePrincipal(
            wrapperClaims!,
            "OperatorBearer",
            "operator-bearer");

        var actor = CanonicalSecurityActor.Resolve(wrapper);
        actor.Should().NotBeNull();
        actor!.ActorId.Should().Be(
            "oidc:subject:https%3A%2F%2Foidc.example%2Ftenant-a:shared-subject");
    }

    [Fact]
    public async Task PersistedOidcProviderKey_OverridesForgedLegacySamlAndApiKeyProvenance()
    {
        var forgedKeyId = Guid.Parse("01234567-89ab-cdef-0123-456789abcdef");
        AdminAuthClaimsProjector.TryProjectPersistedSessionClaims(
        [
            new AdminAuthSessionClaim { Type = ClaimTypes.NameIdentifier, Value = "shared-subject" },
            new AdminAuthSessionClaim { Type = "iss", Value = "https://issuer-a.example" },
            new AdminAuthSessionClaim { Type = "auth_type", Value = "saml" },
            new AdminAuthSessionClaim { Type = IdentityProtocolProvenance.ClaimType, Value = IdentityProtocolProvenance.Saml },
            new AdminAuthSessionClaim { Type = "api_key_id", Value = forgedKeyId.ToString("D") },
            new AdminAuthSessionClaim { Type = ClaimTypes.Role, Value = "publisher" },
        ],
        providerKey: "okta",
        out var normalizedClaims,
        out var validatedProtocol).Should().BeTrue();

        validatedProtocol.Should().Be(IdentityProtocolProvenance.Oidc);
        normalizedClaims.Should().ContainSingle(
                claim => claim.Type == IdentityProtocolProvenance.ClaimType)
            .Which.Value.Should().Be(IdentityProtocolProvenance.Oidc);
        normalizedClaims.Should().NotContain(claim => claim.Type == "api_key_id");

        var direct = AdminAuthClaimsProjector.CreatePrincipal(
            normalizedClaims,
            "AdminAuthSession",
            validatedProtocol);
        var directActor = CanonicalSecurityActor.Resolve(direct);
        directActor.Should().NotBeNull();
        directActor!.ActorId.Should().Be(
            "oidc:subject:https%3A%2F%2Fissuer-a.example:shared-subject");

        var tokenService = CreateService();
        var issuance = tokenService.Issue(normalizedClaims, DateTimeOffset.UtcNow.AddMinutes(10));
        var bearerClaims = await tokenService.TryValidateAsync(issuance!.Token);
        bearerClaims.Should().NotContain(claim => claim.Type == "api_key_id");
        var bearer = AdminAuthClaimsProjector.CreatePrincipal(bearerClaims!, "OperatorBearer");
        CanonicalSecurityActor.Resolve(bearer)!.ActorId.Should().Be(directActor.ActorId);
    }

    [Fact]
    public void CanonicalSecurityActor_ForgedOidcApiKeyClaimsStayIssuerIsolated()
    {
        var forgedKeyId = Guid.Parse("01234567-89ab-cdef-0123-456789abcdef");
        var issuerA = CreateOidcPrincipal("shared-subject", "https://issuer-a.example", forgedKeyId);
        var issuerB = CreateOidcPrincipal("shared-subject", "https://issuer-b.example", forgedKeyId);

        CanonicalSecurityActor.Resolve(issuerA)!.ActorId.Should().Be(
            "oidc:subject:https%3A%2F%2Fissuer-a.example:shared-subject");
        CanonicalSecurityActor.Resolve(issuerB)!.ActorId.Should().Be(
            "oidc:subject:https%3A%2F%2Fissuer-b.example:shared-subject");
    }

    [Theory]
    [InlineData(FrameworkAuthenticationIdentity.ClientCertificateAuthenticationType, "client-certificate")]
    [InlineData(FrameworkAuthenticationIdentity.PortalTokenAuthenticationType, "portal-token")]
    [InlineData(FrameworkAuthenticationIdentity.ScopedJobTokenAuthenticationType, "scoped-job-token")]
    public void CanonicalSecurityActor_FrameworkOwnedSubjectHandlersUseStableNamespaces(
        string authenticationType,
        string expectedScheme)
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, "framework-user-1"),
            new Claim("auth_type", "provider-lookalike"),
        ],
        authenticationType));

        var actor = CanonicalSecurityActor.Resolve(principal);
        actor.Should().NotBeNull();
        actor!.ActorId.Should().Be($"{expectedScheme}:subject:-:framework-user-1");
        CanonicalSecurityActor.IsBoundIdentity(
            actor.ActorId,
            actor.AuthenticationScheme,
            actor.SubjectId,
            actor.SubjectIssuer,
            actor.ApiKeyId,
            actor.CredentialKind).Should().BeFalse(
                "framework subject handlers require dedicated live revalidation before deferred approval");
    }

    [Fact]
    public async Task Issue_OverwritesSourcePrivateIssuerClaimFromValidatedIssuer()
    {
        var service = CreateService();
        var claimsWithSpoofedPrivateValue = SampleClaims
            .Append(new AdminAuthSessionClaim
            {
                Type = OperatorBearerTokenService.IdentityIssuerClaimType,
                Value = "https://attacker.example"
            })
            .ToArray();

        var issuance = service.Issue(claimsWithSpoofedPrivateValue, DateTimeOffset.UtcNow.AddMinutes(10));
        var claims = await service.TryValidateAsync(issuance!.Token);

        claims.Should().ContainSingle(c => c.Type == OperatorBearerTokenService.IdentityIssuerClaimType
            && c.Value == "https://identity.example/tenant-a");
    }

    [Fact]
    public async Task Issue_SamlSessionWithoutIssuer_PreservesIssuerOptionalDurableActor()
    {
        var service = CreateService();
        var issuance = service.Issue(
        [
            new AdminAuthSessionClaim { Type = ClaimTypes.NameIdentifier, Value = "saml-operator-1" },
            new AdminAuthSessionClaim { Type = "auth_type", Value = "saml" },
            new AdminAuthSessionClaim { Type = IdentityProtocolProvenance.ClaimType, Value = IdentityProtocolProvenance.Saml },
            new AdminAuthSessionClaim { Type = ClaimTypes.Role, Value = "publisher" },
        ],
        DateTimeOffset.UtcNow.AddMinutes(10));

        var claims = await service.TryValidateAsync(issuance!.Token);
        claims.Should().NotContain(c => c.Type == OperatorBearerTokenService.IdentityIssuerClaimType);
        var principal = AdminAuthClaimsProjector.CreatePrincipal(
            claims!,
            "OperatorBearer",
            "operator-bearer");

        var actor = CanonicalSecurityActor.Resolve(principal);
        actor.Should().NotBeNull();
        actor!.ActorId.Should().Be("saml:subject:-:saml-operator-1");
        actor.SubjectIssuer.Should().BeNull();

        var direct = AdminAuthClaimsProjector.CreatePrincipal(
        [
            new AdminAuthSessionClaim { Type = ClaimTypes.NameIdentifier, Value = "saml-operator-1" },
            new AdminAuthSessionClaim { Type = "auth_type", Value = "saml" },
            new AdminAuthSessionClaim { Type = IdentityProtocolProvenance.ClaimType, Value = IdentityProtocolProvenance.Saml },
            new AdminAuthSessionClaim { Type = "iss", Value = "untrusted-saml-lookalike-issuer" },
        ],
        "AdminSession");
        CanonicalSecurityActor.Resolve(direct)!.ActorId.Should().Be(actor.ActorId);
        CanonicalSecurityActor.IsBoundIdentity(
            actor.ActorId,
            actor.AuthenticationScheme,
            actor.SubjectId,
            actor.SubjectIssuer,
            actor.ApiKeyId,
            actor.CredentialKind).Should().BeTrue();
    }

    [Fact]
    public void Issue_ClampsExpiryToSessionExpiry()
    {
        var service = CreateService(maxLifetimeMinutes: 60);
        var sessionExpiry = DateTimeOffset.UtcNow.AddMinutes(5);

        var issuance = service.Issue(SampleClaims, sessionExpiry);

        issuance.Should().NotBeNull();
        issuance!.ExpiresAt.Should().BeCloseTo(sessionExpiry, TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void Issue_ClampsExpiryToConfiguredMaxLifetime()
    {
        var service = CreateService(maxLifetimeMinutes: 15);
        var sessionExpiry = DateTimeOffset.UtcNow.AddHours(12);

        var issuance = service.Issue(SampleClaims, sessionExpiry);

        issuance.Should().NotBeNull();
        issuance!.ExpiresAt.Should().BeCloseTo(DateTimeOffset.UtcNow.AddMinutes(15), TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void Issue_WhenDisabled_ReturnsNull()
    {
        var service = CreateService(enabled: false);

        var issuance = service.Issue(SampleClaims, DateTimeOffset.UtcNow.AddMinutes(10));

        issuance.Should().BeNull();
        service.Enabled.Should().BeFalse();
    }

    [Fact]
    public void Issue_WhenSigningKeyTooShort_FeatureIsDisabled()
    {
        var service = CreateService(signingKey: "too-short");

        service.Enabled.Should().BeFalse();
        service.Issue(SampleClaims, DateTimeOffset.UtcNow.AddMinutes(10)).Should().BeNull();
    }

    [Fact]
    public void Issue_WhenSessionAlreadyExpired_ReturnsNull()
    {
        var service = CreateService();

        var issuance = service.Issue(SampleClaims, DateTimeOffset.UtcNow.AddMinutes(-1));

        issuance.Should().BeNull();
    }

    [Fact]
    public async Task TryValidate_WithDifferentSigningKey_ReturnsNull()
    {
        var issuer = CreateService();
        var validator = CreateService(signingKey: "a-totally-different-key-also-32-bytes-long!!");

        var issuance = issuer.Issue(SampleClaims, DateTimeOffset.UtcNow.AddMinutes(10));
        issuance.Should().NotBeNull();

        var claims = await validator.TryValidateAsync(issuance!.Token);

        claims.Should().BeNull();
    }

    [Fact]
    public async Task TryValidate_WithDifferentAudience_ReturnsNull()
    {
        var issuer = CreateService(audience: "honua-admin-api");
        var validator = CreateService(audience: "some-other-audience");

        var issuance = issuer.Issue(SampleClaims, DateTimeOffset.UtcNow.AddMinutes(10));
        issuance.Should().NotBeNull();

        var claims = await validator.TryValidateAsync(issuance!.Token);

        claims.Should().BeNull();
    }

    [Fact]
    public async Task TryValidate_WhenDisabled_ReturnsNull()
    {
        var issuer = CreateService();
        var issuance = issuer.Issue(SampleClaims, DateTimeOffset.UtcNow.AddMinutes(10));
        issuance.Should().NotBeNull();

        var disabled = CreateService(enabled: false);
        var claims = await disabled.TryValidateAsync(issuance!.Token);

        claims.Should().BeNull();
    }

    [Fact]
    public void IsOperatorBearerCandidate_MatchesIssuedTokenAndRejectsForeignToken()
    {
        var service = CreateService();
        var issuance = service.Issue(SampleClaims, DateTimeOffset.UtcNow.AddMinutes(10));

        service.IsOperatorBearerCandidate(issuance!.Token).Should().BeTrue();
        service.IsOperatorBearerCandidate("not-a-jwt").Should().BeFalse();
        service.IsOperatorBearerCandidate(null).Should().BeFalse();
    }

    private static OperatorBearerTokenService CreateService(
        bool enabled = true,
        string? signingKey = SigningKey,
        string issuer = "honua-operator-bearer",
        string audience = "honua-admin-api",
        int maxLifetimeMinutes = 30)
    {
        var options = new OperatorBearerOptions
        {
            Enabled = enabled,
            SigningKey = signingKey,
            Issuer = issuer,
            Audience = audience,
            MaxLifetimeMinutes = maxLifetimeMinutes
        };

        return new OperatorBearerTokenService(Options.Create(options));
    }

    private static ClaimsPrincipal CreateOidcPrincipal(
        string subject,
        string issuer,
        Guid forgedKeyId)
        => new(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, subject),
            new Claim("iss", issuer),
            new Claim("auth_type", "admin-api-key"),
            new Claim("api_key_id", forgedKeyId.ToString("D")),
            new Claim(IdentityProtocolProvenance.ClaimType, IdentityProtocolProvenance.Oidc),
        ],
        authenticationType: "Oidc"));
}
