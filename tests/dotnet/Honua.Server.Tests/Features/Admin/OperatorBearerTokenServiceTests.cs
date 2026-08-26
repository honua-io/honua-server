// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using System.Text.Encodings.Web;
using FluentAssertions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

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
        new AdminAuthSessionClaim { Type = ClaimTypes.Role, Value = "admin" },
        new AdminAuthSessionClaim { Type = "iss", Value = "https://idp.example.com" },
    ];

    [Fact]
    public async Task IssueThenValidate_RoundTripsOperatorClaims()
    {
        var service = CreateService();

        var issuance = service.Issue(SampleClaims, DateTimeOffset.UtcNow.AddMinutes(10));

        issuance.Should().NotBeNull();
        var validation = await service.TryValidateAsync(issuance!.Token);

        validation.Should().NotBeNull();
        validation!.Claims.Should().Contain(c => c.Type == "name" && c.Value == "Operator Admin");
        validation.Claims.Should().Contain(c => c.Type == ClaimTypes.Role && c.Value == "admin");
        validation.Claims.Should().Contain(c => c.Type == ClaimTypes.NameIdentifier && c.Value == "operator-1");
        validation.Claims.Should().NotContain(c => c.Type == "iss");
        validation.Claims.Should().NotContain(c => c.Type == OperationAuthorityContext.MembershipIssuerClaimType);
        validation.MembershipIssuer.Should().Be("https://idp.example.com");
    }

    [Fact]
    public async Task Issue_DerivesMembershipIssuerFromValidatedSessionIssuer_NotPrivateInputClaim()
    {
        var service = CreateService();
        var claims = SampleClaims.Append(new AdminAuthSessionClaim
        {
            Type = OperationAuthorityContext.MembershipIssuerClaimType,
            Value = "https://forged.example.com",
        }).ToArray();

        var issuance = service.Issue(claims, DateTimeOffset.UtcNow.AddMinutes(10));
        var validation = await service.TryValidateAsync(issuance!.Token);

        validation.Should().NotBeNull();
        validation!.MembershipIssuer.Should().Be("https://idp.example.com");
    }

    [Fact]
    public void Issue_WithMultipleUpstreamIssuers_ReturnsNull()
    {
        var service = CreateService();
        var claims = SampleClaims.Append(new AdminAuthSessionClaim
        {
            Type = "iss",
            Value = "https://another-idp.example.com",
        }).ToArray();

        service.Issue(claims, DateTimeOffset.UtcNow.AddMinutes(10)).Should().BeNull();
    }

    [Fact]
    public void Issue_WithOversizedUpstreamIssuer_ReturnsNull()
    {
        var service = CreateService();
        var claims = SampleClaims
            .Where(static claim => claim.Type != "iss")
            .Append(new AdminAuthSessionClaim { Type = "iss", Value = new string('x', 513) })
            .ToArray();

        service.Issue(claims, DateTimeOffset.UtcNow.AddMinutes(10)).Should().BeNull();
    }

    [Fact]
    public async Task Issue_WithoutUpstreamIssuer_RemainsValidForLegacySessions()
    {
        var service = CreateService();
        var claims = SampleClaims.Where(static claim => claim.Type != "iss").ToArray();

        var issuance = service.Issue(claims, DateTimeOffset.UtcNow.AddMinutes(10));
        var validation = await service.TryValidateAsync(issuance!.Token);

        validation.Should().NotBeNull();
        validation!.MembershipIssuer.Should().BeNull();
    }

    [Fact]
    public async Task AuthenticationHandler_PreservesTransportIssuerAndStampsMembershipIssuer()
    {
        var service = CreateService();
        var issuance = service.Issue(SampleClaims, DateTimeOffset.UtcNow.AddMinutes(10));
        var schemeOptions = Substitute.For<IOptionsMonitor<AuthenticationSchemeOptions>>();
        schemeOptions.Get(Arg.Any<string>()).Returns(new AuthenticationSchemeOptions());
        var handler = new OperatorBearerAuthenticationHandler(
            schemeOptions,
            NullLoggerFactory.Instance,
            UrlEncoder.Default,
            service);
        var context = new DefaultHttpContext();
        context.Request.Headers.Authorization = $"Bearer {issuance!.Token}";
        await handler.InitializeAsync(
            new AuthenticationScheme("OperatorBearer", null, typeof(OperatorBearerAuthenticationHandler)),
            context);

        var result = await handler.AuthenticateAsync();

        result.Succeeded.Should().BeTrue();
        result.Principal.Should().NotBeNull();
        var principal = result.Principal!;
        principal.FindFirstValue("iss").Should().Be("honua-operator-bearer");
        CanonicalSecurityActor.FindStampedValue(
                principal,
                OperationAuthorityContext.MembershipIssuerClaimType)
            .Should().Be("https://idp.example.com");
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
}
