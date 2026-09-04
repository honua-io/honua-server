// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Exceptions;
using Honua.Infrastructure.Authentication;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Honua.Server.Tests.Infrastructure.Authentication;

/// <summary>
/// Unit tests for <see cref="AdminAuthSessionStore"/> covering session lifecycle
/// and the BH-028 distributed-cache exception fallback regression.
/// </summary>
[SecurityTest]
[Protocol(TestProtocols.Admin)]
[Operation(Operations.Security)]
public sealed class AdminAuthSessionStoreTests
{
    // ─── BH-028 regression ──────────────────────────────────────────────────────

    [UnitTest]
    public async Task GetPendingSessionAsync_DistributedCacheThrows_FallsBackToMemoryCache()
    {
        // Regression test for BH-028: when distributedCache.GetAsync throws (e.g. Redis
        // cluster failover), the store previously evicted the in-process memory entry and
        // returned null.  Admin login flows in progress during the outage returned 401.
        // After the fix, the memory tier is preserved as a fallback.
        var mockDistCache = Substitute.For<IDistributedCache>();
        // SetAsync succeeds by default (NSubstitute returns Task.CompletedTask) so the
        // pending session is committed to both distributed and memory caches on creation.
        mockDistCache
            .GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromException<byte[]?>(new InvalidOperationException("Redis cluster failover")));

        var memCache = new MemoryCache(new MemoryCacheOptions());
        var store = new AdminAuthSessionStore(memCache, NullLogger<AdminAuthSessionStore>.Instance, mockDistCache);
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(10);

        var sessionId = await store.CreatePendingSessionAsync(
            providerKey: "oidc-test",
            state: "random-state-value",
            codeVerifier: "pkce-verifier",
            expiresAt: expiresAt,
            cancellationToken: CancellationToken.None);

        // Distributed cache throws → before the fix: memory entry evicted, null returned.
        // After the fix: falls back to the memory tier; pending session is returned.
        var session = await store.GetPendingSessionAsync(sessionId, CancellationToken.None);

        session.Should().NotBeNull(
            "the pending session must be retrievable from memory cache during a Redis outage (BH-028)");
        session!.ProviderKey.Should().Be("oidc-test");
        session.State.Should().Be("random-state-value");
    }

    [UnitTest]
    public async Task GetAuthenticatedSessionAsync_DistributedCacheThrows_FallsBackToMemoryCache()
    {
        // Same BH-028 regression but for authenticated sessions: an admin console page
        // load during a Redis outage must not evict the session and return 401.
        var mockDistCache = Substitute.For<IDistributedCache>();
        mockDistCache
            .GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromException<byte[]?>(new InvalidOperationException("Redis cluster failover")));

        var memCache = new MemoryCache(new MemoryCacheOptions());
        var store = new AdminAuthSessionStore(memCache, NullLogger<AdminAuthSessionStore>.Instance, mockDistCache);
        var expiresAt = DateTimeOffset.UtcNow.AddHours(1);

        var sessionId = await store.CreateAuthenticatedSessionAsync(
            providerKey: "oidc-test",
            accessToken: "at-test-value",
            idToken: null,
            claims: [new AdminAuthSessionClaim { Type = "sub", Value = "user-1" }],
            expiresAt: expiresAt,
            cancellationToken: CancellationToken.None);

        var session = await store.GetAuthenticatedSessionAsync(sessionId, CancellationToken.None);

        session.Should().NotBeNull(
            "the authenticated session must be retrievable from memory cache during a Redis outage (BH-028)");
        session!.ProviderKey.Should().Be("oidc-test");
        session.AccessToken.Should().Be("at-test-value");
    }

    [UnitTest]
    public async Task GetPendingSessionAsync_DistributedCacheReturnsNull_ReturnsNull()
    {
        // When distributedCache.GetAsync returns null (key genuinely absent / expired),
        // the correct behavior is to evict the memory entry and return null.
        // The BH-028 fix must not affect this path.
        var mockDistCache = Substitute.For<IDistributedCache>();
        mockDistCache
            .GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<byte[]?>(null));

        var memCache = new MemoryCache(new MemoryCacheOptions());
        var store = new AdminAuthSessionStore(memCache, NullLogger<AdminAuthSessionStore>.Instance, mockDistCache);

        var sessionId = await store.CreatePendingSessionAsync(
            providerKey: "oidc-test",
            state: "state-val",
            codeVerifier: "verifier-val",
            expiresAt: DateTimeOffset.UtcNow.AddMinutes(5),
            cancellationToken: CancellationToken.None);

        var session = await store.GetPendingSessionAsync(sessionId, CancellationToken.None);

        session.Should().BeNull(
            "a null distributed cache result means key-not-found; the store must return null");
    }

    [UnitTest]
    public async Task RemoveAuthenticatedSessionAsync_DistributedCacheFails_RejectsRevocationAndEvictsLocalSession()
    {
        var cache = Substitute.For<IDistributedCache>();
        cache.RemoveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new IOException("Redis unavailable")));
        cache.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<byte[]?>(new IOException("Redis unavailable")));
        using var memory = new MemoryCache(new MemoryCacheOptions());
        var store = new AdminAuthSessionStore(memory, NullLogger<AdminAuthSessionStore>.Instance, cache);
        var sessionId = await store.CreateAuthenticatedSessionAsync("oidc", "token", null,
            [new AdminAuthSessionClaim { Type = "sub", Value = "admin" }],
            DateTimeOffset.UtcNow.AddMinutes(5), CancellationToken.None);

        await Assert.ThrowsAsync<ServiceUnavailableException>(() =>
            store.RemoveAuthenticatedSessionAsync(sessionId, CancellationToken.None));
        Assert.Null(await store.GetAuthenticatedSessionAsync(sessionId, CancellationToken.None));
    }

    [UnitTest]
    public async Task RemovePendingSessionAsync_DistributedCacheFails_PreservesBestEffortCleanup()
    {
        var cache = Substitute.For<IDistributedCache>();
        cache.RemoveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new IOException("Redis unavailable")));
        cache.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<byte[]?>(new IOException("Redis unavailable")));
        using var memory = new MemoryCache(new MemoryCacheOptions());
        var store = new AdminAuthSessionStore(memory, NullLogger<AdminAuthSessionStore>.Instance, cache);
        var sessionId = await store.CreatePendingSessionAsync("oidc", "state", "verifier",
            DateTimeOffset.UtcNow.AddMinutes(5), CancellationToken.None);

        await store.RemovePendingSessionAsync(sessionId, CancellationToken.None);
        Assert.Null(await store.GetPendingSessionAsync(sessionId, CancellationToken.None));
    }

    [Theory]
    [Trait("Tier", "Fast")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RemoveAuthenticatedSessionAsync_DeleteDenied_RequiresConfirmedAbsence(bool recordExists)
    {
        var cache = Substitute.For<IDistributedCache>();
        byte[]? payload = null;
        cache.SetAsync(Arg.Any<string>(), Arg.Any<byte[]>(), Arg.Any<DistributedCacheEntryOptions>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                payload = call.ArgAt<byte[]>(1);
                return Task.CompletedTask;
            });
        cache.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(recordExists ? payload : null));
        cache.RemoveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new IOException("Delete denied")));
        using var memory = new MemoryCache(new MemoryCacheOptions());
        var store = new AdminAuthSessionStore(memory, NullLogger<AdminAuthSessionStore>.Instance, cache);
        var sessionId = await store.CreateAuthenticatedSessionAsync("oidc", "token", null,
            [new AdminAuthSessionClaim { Type = "sub", Value = "admin" }],
            DateTimeOffset.UtcNow.AddMinutes(5), CancellationToken.None);

        if (recordExists)
        {
            await Assert.ThrowsAsync<ServiceUnavailableException>(() =>
                store.RemoveAuthenticatedSessionAsync(sessionId, CancellationToken.None));
        }
        else
        {
            await store.RemoveAuthenticatedSessionAsync(sessionId, CancellationToken.None);
        }

        cache.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<byte[]?>(new IOException("Read unavailable")));
        Assert.Null(await store.GetAuthenticatedSessionAsync(sessionId, CancellationToken.None));
    }

    // ─── Basic session lifecycle ────────────────────────────────────────────────

    [UnitTest]
    public async Task CreateAndGetPendingSession_RoundTrips()
    {
        var store = CreateStore();
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(5);

        var sessionId = await store.CreatePendingSessionAsync(
            "provider-1", "state-abc", "verifier-xyz", expiresAt, CancellationToken.None);

        sessionId.Should().NotBeNullOrWhiteSpace();

        var session = await store.GetPendingSessionAsync(sessionId, CancellationToken.None);

        session.Should().NotBeNull();
        session!.ProviderKey.Should().Be("provider-1");
        session.State.Should().Be("state-abc");
        session.CodeVerifier.Should().Be("verifier-xyz");
    }

    [UnitTest]
    public async Task RemovePendingSession_SubsequentGetReturnsNull()
    {
        var store = CreateStore();
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(5);

        var sessionId = await store.CreatePendingSessionAsync(
            "provider-2", "state-def", "verifier-abc", expiresAt, CancellationToken.None);

        (await store.GetPendingSessionAsync(sessionId, CancellationToken.None)).Should().NotBeNull();

        await store.RemovePendingSessionAsync(sessionId, CancellationToken.None);

        (await store.GetPendingSessionAsync(sessionId, CancellationToken.None)).Should().BeNull();
    }

    [UnitTest]
    public async Task CreateAndGetAuthenticatedSession_RoundTrips()
    {
        var store = CreateStore();
        var expiresAt = DateTimeOffset.UtcNow.AddHours(1);

        var sessionId = await store.CreateAuthenticatedSessionAsync(
            "provider-1",
            accessToken: "at-value",
            idToken: "id-token-value",
            claims: [new AdminAuthSessionClaim { Type = "email", Value = "admin@honua.io" }],
            expiresAt: expiresAt,
            cancellationToken: CancellationToken.None);

        sessionId.Should().NotBeNullOrWhiteSpace();

        var session = await store.GetAuthenticatedSessionAsync(sessionId, CancellationToken.None);

        session.Should().NotBeNull();
        session!.AccessToken.Should().Be("at-value");
        session.IdToken.Should().Be("id-token-value");
        session.Claims.Should().ContainSingle(c => c.Type == "email" && c.Value == "admin@honua.io");
    }

    [UnitTest]
    public async Task GetPendingSession_UnknownId_ReturnsNull()
    {
        var store = CreateStore();
        var session = await store.GetPendingSessionAsync("unknown-session-id", CancellationToken.None);
        session.Should().BeNull();
    }

    // ────────────────────────────────────────────────────────────────────────────

    private static AdminAuthSessionStore CreateStore()
    {
        var memoryCache = new MemoryCache(new MemoryCacheOptions());
        return new AdminAuthSessionStore(memoryCache, NullLogger<AdminAuthSessionStore>.Instance);
    }
}
