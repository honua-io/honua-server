// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using FluentAssertions;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Licensing.Abstractions;
using Honua.Core.Features.Licensing.Domain;
using Honua.Infrastructure.Authentication;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Helpers;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

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

    [UnitTest]
    public async Task ValidateAndIntrospect_MixedMappedRoles_FallBackToDirectRolesAfterEntitlementExpires()
    {
        var entitlements = new MutableLicenseEntitlementService(HonuaEdition.Enterprise);
        var issuer = CreateIssuer(entitlements);
        var issuance = await issuer.IssueAsync(
            new PortalTokenIssueRequest(
                PrincipalId: "alice",
                DisplayName: null,
                TenantId: null,
                Roles: ["viewer", "editor"],
                ClientType: PortalTokenClientType.Ip,
                BindingValue: "192.0.2.10",
                ExpiresAt: DateTimeOffset.UtcNow.AddMinutes(30),
                RolesRequireClaimsMappingEntitlement: true,
                RolesWithoutClaimsMapping: ["viewer"]),
            CancellationToken.None);

        var entitled = await issuer.ValidateAsync(
            issuance.Token,
            new PortalTokenBinding(Referer: null, ClientIp: "192.0.2.10"),
            CancellationToken.None);
        var entitledIntrospection = await issuer.IntrospectAsync(issuance.Token, CancellationToken.None);

        entitled.Should().NotBeNull();
        entitled!.Principal.IsInRole("viewer").Should().BeTrue();
        entitled.Principal.IsInRole("editor").Should().BeTrue();
        entitledIntrospection!.Roles.Should().BeEquivalentTo("viewer", "editor");

        entitlements.Expire();

        var expired = await issuer.ValidateAsync(
            issuance.Token,
            new PortalTokenBinding(Referer: null, ClientIp: "192.0.2.10"),
            CancellationToken.None);
        var expiredIntrospection = await issuer.IntrospectAsync(issuance.Token, CancellationToken.None);

        expired.Should().NotBeNull();
        expired!.Principal.IsInRole("viewer").Should().BeTrue();
        expired.Principal.IsInRole("editor").Should().BeFalse();
        expiredIntrospection!.Roles.Should().Equal("viewer");
    }

    [UnitTest]
    public async Task ValidateAsync_MappingRolesWithUnknownFallback_FailsClosedAfterEntitlementExpires()
    {
        var entitlements = new MutableLicenseEntitlementService(HonuaEdition.Enterprise);
        var issuer = CreateIssuer(entitlements);
        var issuance = await issuer.IssueAsync(
            new PortalTokenIssueRequest(
                PrincipalId: "legacy-user",
                DisplayName: null,
                TenantId: null,
                Roles: ["admin"],
                ClientType: PortalTokenClientType.Ip,
                BindingValue: "192.0.2.11",
                ExpiresAt: DateTimeOffset.UtcNow.AddMinutes(30),
                RolesRequireClaimsMappingEntitlement: true,
                RolesWithoutClaimsMapping: null),
            CancellationToken.None);
        entitlements.Expire();

        var validation = await issuer.ValidateAsync(
            issuance.Token,
            new PortalTokenBinding(Referer: null, ClientIp: "192.0.2.11"),
            CancellationToken.None);

        validation.Should().NotBeNull();
        validation!.Principal.FindAll(ClaimTypes.Role).Should().BeEmpty();
    }

    // ─── BH-028 regression ──────────────────────────────────────────────────────

    [UnitTest]
    public async Task ValidateAsync_DistributedCacheThrows_FallsBackToMemoryCache()
    {
        // Regression test for BH-028: when distributedCache.GetAsync throws, the issuer
        // previously evicted the in-process memory cache entry and returned null, conflating
        // a transient Redis outage with key-not-found.  During a Redis cluster failover
        // (typically 15-60 s) this invalidated all portal sessions simultaneously.
        //
        // After the fix, a distributed cache read exception falls back to the memory tier,
        // preserving auth continuity for the duration of the outage.
        var mockDistCache = NSubstitute.Substitute.For<IDistributedCache>();
        // SetAsync returns Task.CompletedTask by default (NSubstitute) so issuance succeeds
        // and the record is committed to both the distributed cache (mock) and memory cache.
        mockDistCache
            .GetAsync(NSubstitute.Arg.Any<string>(), NSubstitute.Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromException<byte[]?>(new InvalidOperationException("Redis cluster failover")));

        var memCache = new MemoryCache(new MemoryCacheOptions());
        var issuer = new PortalTokenIssuer(memCache, NullLogger<PortalTokenIssuer>.Instance, mockDistCache);
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(30);

        var issuance = await issuer.IssueAsync(
            new PortalTokenIssueRequest(
                PrincipalId: "alice",
                DisplayName: null,
                TenantId: "tenant-A",
                Roles: ["viewer"],
                ClientType: PortalTokenClientType.Ip,
                BindingValue: "10.0.0.1",
                ExpiresAt: expiresAt),
            CancellationToken.None);

        // Distributed cache throws → before the fix: memory entry evicted, null returned.
        // After the fix: falls back to the memory tier and validation succeeds.
        var validation = await issuer.ValidateAsync(
            issuance.Token,
            new PortalTokenBinding(Referer: null, ClientIp: "10.0.0.1"),
            CancellationToken.None);

        validation.Should().NotBeNull(
            "the token must validate from memory cache during a Redis outage (BH-028)");
        validation!.Principal.Identity!.IsAuthenticated.Should().BeTrue();
        validation.Principal.FindFirstValue(TenantClaimType).Should().Be("tenant-A");
    }

    [UnitTest]
    public async Task ValidateAsync_DistributedCacheReturnsNull_EvictsMemory_ReturnsNull()
    {
        // When distributedCache.GetAsync returns null (key genuinely expired / absent),
        // the memory entry must be evicted and null returned — the correct
        // key-not-found semantics that the BH-028 fix must not regress.
        var mockDistCache = NSubstitute.Substitute.For<IDistributedCache>();
        mockDistCache
            .GetAsync(NSubstitute.Arg.Any<string>(), NSubstitute.Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<byte[]?>(null));

        var memCache = new MemoryCache(new MemoryCacheOptions());
        var issuer = new PortalTokenIssuer(memCache, NullLogger<PortalTokenIssuer>.Instance, mockDistCache);

        var issuance = await issuer.IssueAsync(
            new PortalTokenIssueRequest(
                PrincipalId: "bob",
                DisplayName: null,
                TenantId: null,
                Roles: [],
                ClientType: PortalTokenClientType.Ip,
                BindingValue: "10.0.0.2",
                ExpiresAt: DateTimeOffset.UtcNow.AddMinutes(30)),
            CancellationToken.None);

        var validation = await issuer.ValidateAsync(
            issuance.Token,
            new PortalTokenBinding(Referer: null, ClientIp: "10.0.0.2"),
            CancellationToken.None);

        validation.Should().BeNull(
            "a null distributed cache result means key-not-found; the memory entry should be evicted");
    }

    // ────────────────────────────────────────────────────────────────────────────

    private static PortalTokenIssuer CreateIssuer()
    {
        var memoryCache = new MemoryCache(new MemoryCacheOptions());
        return new PortalTokenIssuer(memoryCache, NullLogger<PortalTokenIssuer>.Instance);
    }

    private static PortalTokenIssuer CreateIssuer(ILicenseEntitlementService entitlements)
    {
        var memoryCache = new MemoryCache(new MemoryCacheOptions());
        var services = new ServiceCollection()
            .AddSingleton(entitlements)
            .BuildServiceProvider();
        return new PortalTokenIssuer(
            memoryCache,
            NullLogger<PortalTokenIssuer>.Instance,
            serviceProvider: services);
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

    private const string TenantClaimType = PortalTokenIssuer.TenantClaimType;
}
