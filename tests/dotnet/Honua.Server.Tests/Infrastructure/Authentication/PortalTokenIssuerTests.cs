// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using FluentAssertions;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Infrastructure.Authentication;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Server.Tests.Infrastructure.Authentication;

/// <summary>
/// Unit tests for <see cref="PortalTokenIssuer"/> covering binding, expiry, and
/// claim projection behavior independent of the HTTP pipeline.
/// </summary>
[SecurityTest]
[Protocol(TestProtocols.FeatureServer)]
[Operation(Operations.Security)]
public sealed class PortalTokenIssuerTests
{
    [UnitTest]
    public async Task IssueAsync_RoundTripsRefererBoundToken_HydratesPrincipalWithRolesAndTenant()
    {
        var issuer = CreateIssuer();
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(30);

        var issuance = await issuer.IssueAsync(
            new PortalTokenIssueRequest(
                PrincipalId: "alice",
                DisplayName: "Alice",
                TenantId: "tenant-A",
                Roles: ["editor"],
                ClientType: PortalTokenClientType.Referer,
                BindingValue: "https://app.example.com/maps/",
                ExpiresAt: expiresAt),
            CancellationToken.None);

        issuance.Token.Should().NotBeNullOrWhiteSpace();
        issuance.ExpiresAt.Should().Be(expiresAt);

        var validation = await issuer.ValidateAsync(
            issuance.Token,
            new PortalTokenBinding(Referer: "https://app.example.com/other", ClientIp: "192.0.2.1"),
            CancellationToken.None);

        validation.Should().NotBeNull();
        validation!.Principal.Identity!.IsAuthenticated.Should().BeTrue();
        validation.Principal.FindFirstValue(ClaimTypes.Name).Should().Be("alice");
        validation.Principal.FindFirstValue(PortalTokenIssuer.TenantClaimType).Should().Be("tenant-A");
        validation.Principal.IsInRole("editor").Should().BeTrue();
    }

    [UnitTest]
    public async Task ValidateAsync_RefererMismatch_ReturnsNull()
    {
        var issuer = CreateIssuer();
        var issuance = await issuer.IssueAsync(
            new PortalTokenIssueRequest(
                PrincipalId: "alice",
                DisplayName: null,
                TenantId: null,
                Roles: [],
                ClientType: PortalTokenClientType.Referer,
                BindingValue: "https://app.example.com/",
                ExpiresAt: DateTimeOffset.UtcNow.AddMinutes(5)),
            CancellationToken.None);

        var validation = await issuer.ValidateAsync(
            issuance.Token,
            new PortalTokenBinding(Referer: "https://attacker.example.com/", ClientIp: null),
            CancellationToken.None);

        validation.Should().BeNull();
    }

    [UnitTest]
    public async Task ValidateAsync_IpBoundToken_RequiresMatchingClientIp()
    {
        var issuer = CreateIssuer();
        var issuance = await issuer.IssueAsync(
            new PortalTokenIssueRequest(
                PrincipalId: "bob",
                DisplayName: null,
                TenantId: null,
                Roles: [],
                ClientType: PortalTokenClientType.Ip,
                BindingValue: "203.0.113.4",
                ExpiresAt: DateTimeOffset.UtcNow.AddMinutes(5)),
            CancellationToken.None);

        var matching = await issuer.ValidateAsync(
            issuance.Token,
            new PortalTokenBinding(Referer: null, ClientIp: "203.0.113.4"),
            CancellationToken.None);
        var mismatch = await issuer.ValidateAsync(
            issuance.Token,
            new PortalTokenBinding(Referer: null, ClientIp: "203.0.113.5"),
            CancellationToken.None);

        matching.Should().NotBeNull();
        mismatch.Should().BeNull();
    }

    [UnitTest]
    public async Task ValidateAsync_ExpiredToken_ReturnsNull()
    {
        var issuer = CreateIssuer();
        var issuance = await issuer.IssueAsync(
            new PortalTokenIssueRequest(
                PrincipalId: "carol",
                DisplayName: null,
                TenantId: null,
                Roles: [],
                ClientType: PortalTokenClientType.Ip,
                BindingValue: "198.51.100.7",
                // Issue with a tiny past expiry to force the expiry branch.
                ExpiresAt: DateTimeOffset.UtcNow.AddMilliseconds(-1)),
            CancellationToken.None);

        var validation = await issuer.ValidateAsync(
            issuance.Token,
            new PortalTokenBinding(Referer: null, ClientIp: "198.51.100.7"),
            CancellationToken.None);

        validation.Should().BeNull();
    }

    [UnitTest]
    public async Task ValidateAsync_UnknownToken_ReturnsNull()
    {
        var issuer = CreateIssuer();
        var validation = await issuer.ValidateAsync(
            "0000000000000000000000000000000000000000000000000000000000000000",
            new PortalTokenBinding(Referer: "https://app.example.com/", ClientIp: null),
            CancellationToken.None);

        validation.Should().BeNull();
    }

    private static PortalTokenIssuer CreateIssuer()
    {
        var memoryCache = new MemoryCache(new MemoryCacheOptions());
        return new PortalTokenIssuer(memoryCache, NullLogger<PortalTokenIssuer>.Instance);
    }
}
