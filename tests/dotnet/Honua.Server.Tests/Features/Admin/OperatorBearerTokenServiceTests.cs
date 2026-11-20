// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Claims;
using FluentAssertions;
using Honua.Infrastructure.Authentication;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Features.Admin;

public sealed class OperatorBearerTokenServiceTests
{
    private const string SigningKey = "operator-bearer-signing-key-at-least-32-bytes-long";

    private static readonly IReadOnlyList<AdminAuthSessionClaim> SampleClaims =
    [
        new AdminAuthSessionClaim { Type = "name", Value = "Operator Admin" },
        new AdminAuthSessionClaim { Type = ClaimTypes.NameIdentifier, Value = "operator-1" },
        new AdminAuthSessionClaim { Type = ClaimTypes.Role, Value = "admin" }
    ];

    [Fact]
    public async Task IssueThenValidate_RoundTripsOperatorClaims()
    {
        var service = CreateService();

        var issuance = service.Issue(SampleClaims, DateTimeOffset.UtcNow.AddMinutes(10));

        issuance.Should().NotBeNull();
        var validation = await service.TryValidateAsync(issuance!.Token);

        validation.Should().NotBeNull();
        validation!.Binding.Should().Be(default(OperatorBearerBinding));
        var claims = validation.Claims;
        claims.Should().Contain(c => c.Type == "name" && c.Value == "Operator Admin");
        claims.Should().Contain(c => c.Type == ClaimTypes.Role && c.Value == "admin");
        claims.Should().Contain(c => c.Type == ClaimTypes.NameIdentifier && c.Value == "operator-1");
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

    [UnitTest]
    public async Task Issue_WithSessionBinding_CarriesItOutsideTheClaims()
    {
        var service = CreateService();
        var spoofed = SampleClaims
            .Append(new AdminAuthSessionClaim { Type = OperatorBearerTokenService.SessionBindingClaimType, Value = "from-session" })
            .ToArray();

        var bound = await service.TryValidateAsync(service.Issue(spoofed, DateTimeOffset.UtcNow.AddMinutes(10), new OperatorBearerBinding(Session: "binding-1"))!.Token);
        var unbound = await service.TryValidateAsync(service.Issue(spoofed, DateTimeOffset.UtcNow.AddMinutes(10))!.Token);

        bound!.Binding.Session.Should().Be("binding-1");
        bound.Claims.Should().NotContain(c => c.Type == OperatorBearerTokenService.SessionBindingClaimType);
        unbound!.Binding.Session.Should().BeNull("a session claim cannot choose the binding");
    }

    [UnitTest]
    public async Task Issue_WithAdminPasswordVersion_CarriesItOutsideTheClaims()
    {
        var service = CreateService();
        var spoofed = SampleClaims
            .Append(new AdminAuthSessionClaim { Type = OperatorBearerTokenService.AdminPasswordVersionClaimType, Value = "from-claims" })
            .ToArray();

        var bound = await service.TryValidateAsync(
            service.Issue(spoofed, DateTimeOffset.UtcNow.AddMinutes(10), new OperatorBearerBinding(AdminPasswordVersion: "v1"))!.Token);
        var unbound = await service.TryValidateAsync(service.Issue(spoofed, DateTimeOffset.UtcNow.AddMinutes(10))!.Token);

        bound!.Binding.AdminPasswordVersion.Should().Be("v1");
        bound.Claims.Should().NotContain(c => c.Type == OperatorBearerTokenService.AdminPasswordVersionClaimType);
        unbound!.Binding.AdminPasswordVersion.Should().BeNull("a session claim cannot choose the password version");
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
