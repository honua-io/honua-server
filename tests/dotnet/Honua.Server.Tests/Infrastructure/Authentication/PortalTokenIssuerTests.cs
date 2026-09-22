// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

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
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
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

    // ─── SEC-9: the token is bound to the credential it was minted from ─────────

    [UnitTest]
    public async Task IssueAsync_SourceCredentialExpiresFirst_ClampsTokenLifetimeToSourceExpiry()
    {
        var context = CreateSourceBoundIssuer();
        var keyExpiry = DateTimeOffset.UtcNow.AddMinutes(5);

        var issuance = await context.Issuer.IssueAsync(
            new PortalTokenIssueRequest(
                PrincipalId: "admin",
                DisplayName: null,
                TenantId: null,
                Roles: ["admin"],
                ClientType: PortalTokenClientType.Ip,
                BindingValue: "192.0.2.30",
                // The caller asked for the configured maximum; the credential behind the
                // token lives five minutes.
                ExpiresAt: DateTimeOffset.UtcNow.AddMinutes(
                    PortalTokenAuthenticationOptions.DefaultMaxExpirationMinutesValue),
                Source: new PortalCredentialSource(
                    PortalCredentialSourceKind.None,
                    ExpiresAt: keyExpiry)),
            CancellationToken.None);

        issuance.ExpiresAt.Should().Be(keyExpiry);

        var validation = await context.Issuer.ValidateAsync(
            issuance.Token,
            new PortalTokenBinding(Referer: null, ClientIp: "192.0.2.30"),
            CancellationToken.None);
        validation!.ExpiresAt.Should().Be(keyExpiry);
    }

    [UnitTest]
    public async Task ValidateAndIntrospect_ManagedKeySourceRevoked_RefusesToken()
    {
        var context = CreateSourceBoundIssuer();
        var key = await context.KeyStore.CreateAsync(
            "automation", ["admin:*"], DateTimeOffset.UtcNow.AddHours(2), "test", CancellationToken.None);
        var issuance = await IssueFromManagedKeyAsync(context, key.Record, "192.0.2.31");
        var binding = new PortalTokenBinding(Referer: null, ClientIp: "192.0.2.31");

        (await context.Issuer.ValidateAsync(issuance.Token, binding, CancellationToken.None))
            .Should().NotBeNull("the key is live");
        (await context.Issuer.IntrospectAsync(issuance.Token, CancellationToken.None))
            .Should().NotBeNull();

        await context.KeyStore.RevokeAsync(key.Record.Id, CancellationToken.None);

        (await context.Issuer.ValidateAsync(issuance.Token, binding, CancellationToken.None))
            .Should().BeNull("the credential the token was minted from was revoked");
        (await context.Issuer.IntrospectAsync(issuance.Token, CancellationToken.None))
            .Should().BeNull("introspection must agree with validation");
    }

    [UnitTest]
    public async Task ValidateAsync_ManagedKeySourceRotated_RefusesToken()
    {
        var context = CreateSourceBoundIssuer();
        var key = await context.KeyStore.CreateAsync(
            "automation", ["admin:*"], DateTimeOffset.UtcNow.AddHours(2), "test", CancellationToken.None);
        var issuance = await IssueFromManagedKeyAsync(context, key.Record, "192.0.2.32");
        var binding = new PortalTokenBinding(Referer: null, ClientIp: "192.0.2.32");

        (await context.Issuer.ValidateAsync(issuance.Token, binding, CancellationToken.None))
            .Should().NotBeNull();

        // Rotation keeps the key identifier and replaces the key material, so identity alone
        // is not enough: the recorded version marker is what detects it.
        await context.KeyStore.RotateAsync(key.Record.Id, CancellationToken.None);

        (await context.Issuer.ValidateAsync(issuance.Token, binding, CancellationToken.None))
            .Should().BeNull("the key material the token was minted from was replaced");
    }

    [UnitTest]
    public async Task ValidateAsync_ManagedKeySourceExpired_RefusesToken()
    {
        var clock = new MutableTimeProvider(DateTimeOffset.UtcNow);
        var context = CreateSourceBoundIssuer(timeProvider: clock);
        var key = await context.KeyStore.CreateAsync(
            "automation", ["admin:*"], clock.GetUtcNow().AddMinutes(1), "test", CancellationToken.None);

        // Deliberately mint without the source expiry so only the re-check can refuse it; the
        // clamp is proven separately above.
        var issuance = await context.Issuer.IssueAsync(
            new PortalTokenIssueRequest(
                PrincipalId: "admin",
                DisplayName: null,
                TenantId: null,
                Roles: ["admin"],
                ClientType: PortalTokenClientType.Ip,
                BindingValue: "192.0.2.33",
                ExpiresAt: DateTimeOffset.UtcNow.AddMinutes(30),
                Source: new PortalCredentialSource(
                    PortalCredentialSourceKind.ManagedApiKey,
                    Reference: key.Record.Id.ToString("D"),
                    Version: PortalCredentialSourceVersion.ForManagedKey(key.Record))),
            CancellationToken.None);

        var binding = new PortalTokenBinding(Referer: null, ClientIp: "192.0.2.33");
        (await context.Issuer.ValidateAsync(issuance.Token, binding, CancellationToken.None))
            .Should().NotBeNull("the key is still live");

        clock.Advance(TimeSpan.FromMinutes(2));

        (await context.Issuer.ValidateAsync(issuance.Token, binding, CancellationToken.None))
            .Should().BeNull("the credential expired before the token did");
    }

    [UnitTest]
    public async Task ValidateAsync_AdminPasswordSourceChanged_RefusesToken()
    {
        const string original = "Or1ginal-Admin-Password!";
        var context = CreateSourceBoundIssuer(original);
        var issuance = await context.Issuer.IssueAsync(
            new PortalTokenIssueRequest(
                PrincipalId: "admin",
                DisplayName: null,
                TenantId: null,
                Roles: ["admin"],
                ClientType: PortalTokenClientType.Ip,
                BindingValue: "192.0.2.34",
                ExpiresAt: DateTimeOffset.UtcNow.AddMinutes(30),
                Source: new PortalCredentialSource(
                    PortalCredentialSourceKind.AdminPassword,
                    Version: PortalCredentialSourceVersion.ForAdminPassword(original))),
            CancellationToken.None);
        var binding = new PortalTokenBinding(Referer: null, ClientIp: "192.0.2.34");

        (await context.Issuer.ValidateAsync(issuance.Token, binding, CancellationToken.None))
            .Should().NotBeNull();

        context.ApiKeyOptions.AdminPassword = "R0tated-Admin-Password!";

        (await context.Issuer.ValidateAsync(issuance.Token, binding, CancellationToken.None))
            .Should().BeNull("the password the token was minted from was rotated");
    }

    [UnitTest]
    public async Task ValidateAsync_RecordWithoutSourceProvenance_RefusesToken()
    {
        // A record written before the source binding existed deserializes with the member
        // absent. Absent is UNKNOWN provenance, not "no source", so it fails closed and the
        // holder re-authenticates rather than keeping an unbounded credential.
        var context = CreateSourceBoundIssuer();
        const string token = "1111111111111111111111111111111111111111111111111111111111111111";
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(30);
        var legacyPayload = Encoding.UTF8.GetBytes(
            $$"""
            {"PrincipalId":"legacy","Roles":["admin"],"ClientType":1,
             "BindingValue":"192.0.2.35","ExpiresAt":"{{expiresAt:O}}"}
            """);

        // "portal-auth:token:" is the issuer's storage prefix; a legacy entry is written here
        // exactly as a pre-upgrade instance would have left it.
        context.MemoryCache.Set("portal-auth:token:" + token, legacyPayload, expiresAt);

        (await context.Issuer.ValidateAsync(
                token,
                new PortalTokenBinding(Referer: null, ClientIp: "192.0.2.35"),
                CancellationToken.None))
            .Should().BeNull();
        (await context.Issuer.IntrospectAsync(token, CancellationToken.None)).Should().BeNull();
    }

    [UnitTest]
    public async Task AdminCredentialBridge_ManagedKey_BindsIssuedTokenToThatKey()
    {
        const string adminPassword = "Br1dge-Admin-Password!";
        var context = CreateSourceBoundIssuer(adminPassword);
        var keyExpiry = DateTimeOffset.UtcNow.AddMinutes(3);
        var key = await context.KeyStore.CreateAsync(
            "desktop", ["admin:*"], keyExpiry, "test", CancellationToken.None);
        var verifier = new AdminPortalCredentialVerifier(
            Options.Create(context.ApiKeyOptions),
            secretResolver: null,
            adminApiKeyStore: context.KeyStore);

        var verified = await verifier.VerifyAsync("desktop-user", key.Key, CancellationToken.None);

        verified.Should().NotBeNull();
        verified!.Source.Should().NotBeNull();
        verified.Source!.Kind.Should().Be(PortalCredentialSourceKind.ManagedApiKey);
        verified.Source.Reference.Should().Be(key.Record.Id.ToString("D"));
        verified.Source.ExpiresAt.Should().Be(keyExpiry);

        var issuance = await context.Issuer.IssueAsync(
            new PortalTokenIssueRequest(
                PrincipalId: verified.PrincipalId,
                DisplayName: verified.DisplayName,
                TenantId: verified.TenantId,
                Roles: verified.Roles,
                ClientType: PortalTokenClientType.Ip,
                BindingValue: "192.0.2.36",
                ExpiresAt: DateTimeOffset.UtcNow.AddMinutes(
                    PortalTokenAuthenticationOptions.DefaultMaxExpirationMinutesValue),
                Source: verified.Source),
            CancellationToken.None);

        issuance.ExpiresAt.Should().Be(keyExpiry, "a token may not outlive the key it came from");

        var binding = new PortalTokenBinding(Referer: null, ClientIp: "192.0.2.36");
        (await context.Issuer.ValidateAsync(issuance.Token, binding, CancellationToken.None))
            .Should().NotBeNull();

        await context.KeyStore.RevokeAsync(key.Record.Id, CancellationToken.None);

        (await context.Issuer.ValidateAsync(issuance.Token, binding, CancellationToken.None))
            .Should().BeNull();
    }

    [UnitTest]
    public async Task AdminCredentialBridge_AdminPassword_BindsIssuedTokenToThatPassword()
    {
        const string adminPassword = "P4ssword-Bridge-Test!";
        var context = CreateSourceBoundIssuer(adminPassword);
        var verifier = new AdminPortalCredentialVerifier(
            Options.Create(context.ApiKeyOptions),
            secretResolver: null,
            adminApiKeyStore: context.KeyStore);

        var verified = await verifier.VerifyAsync("admin", adminPassword, CancellationToken.None);

        verified.Should().NotBeNull();
        verified!.Source!.Kind.Should().Be(PortalCredentialSourceKind.AdminPassword);
        verified.Source.Version.Should().Be(PortalCredentialSourceVersion.ForAdminPassword(adminPassword));
        verified.Source.Version.Should().NotContain(adminPassword);
    }

    private static Task<PortalTokenIssuance> IssueFromManagedKeyAsync(
        SourceBoundIssuer context,
        AdminApiKeyRecord record,
        string clientIp)
        => context.Issuer.IssueAsync(
            new PortalTokenIssueRequest(
                PrincipalId: "admin",
                DisplayName: record.Name,
                TenantId: null,
                Roles: ["admin"],
                ClientType: PortalTokenClientType.Ip,
                BindingValue: clientIp,
                ExpiresAt: DateTimeOffset.UtcNow.AddMinutes(30),
                Source: new PortalCredentialSource(
                    PortalCredentialSourceKind.ManagedApiKey,
                    Reference: record.Id.ToString("D"),
                    Version: PortalCredentialSourceVersion.ForManagedKey(record),
                    ExpiresAt: record.ExpiresAt)),
            CancellationToken.None);

    /// <summary>
    /// An issuer wired to the real source validator over an in-memory key store, with source
    /// re-validation caching disabled so a revocation is observable immediately.
    /// </summary>
    private static SourceBoundIssuer CreateSourceBoundIssuer(
        string? adminPassword = null,
        TimeProvider? timeProvider = null)
    {
        var memoryCache = new MemoryCache(new MemoryCacheOptions());
        var keyStore = new InMemoryAdminApiKeyStore(timeProvider);
        var apiKeyOptions = new ApiKeyAuthenticationOptions { AdminPassword = adminPassword };
        var portalOptions = Options.Create(
            new PortalTokenAuthenticationOptions { SourceRevalidationSeconds = 0 });

        var services = new ServiceCollection()
            .AddSingleton<IMemoryCache>(memoryCache)
            .AddSingleton<IAdminApiKeyStore>(keyStore)
            .AddSingleton(Options.Create(apiKeyOptions))
            .AddSingleton(portalOptions)
            .AddSingleton<IPortalTokenSourceValidator>(sp => new PortalTokenSourceValidator(
                sp.GetRequiredService<IMemoryCache>(),
                sp.GetRequiredService<IOptions<PortalTokenAuthenticationOptions>>(),
                sp,
                timeProvider))
            .BuildServiceProvider();

        var issuer = new PortalTokenIssuer(
            memoryCache,
            NullLogger<PortalTokenIssuer>.Instance,
            serviceProvider: services);
        return new SourceBoundIssuer(issuer, keyStore, memoryCache, apiKeyOptions);
    }

    private sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan delta) => _utcNow += delta;
    }

    private sealed record SourceBoundIssuer(
        PortalTokenIssuer Issuer,
        InMemoryAdminApiKeyStore KeyStore,
        MemoryCache MemoryCache,
        ApiKeyAuthenticationOptions ApiKeyOptions);

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
