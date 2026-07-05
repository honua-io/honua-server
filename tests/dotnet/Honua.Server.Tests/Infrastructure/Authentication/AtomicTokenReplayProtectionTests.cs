// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.IdentityModel.Tokens.Jwt;
using System.Linq;
using System.Security.Claims;
using FluentAssertions;
using Honua.Infrastructure.Authentication;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Honua.Server.Tests.Infrastructure.Authentication;

/// <summary>
/// Unit tests for <see cref="AtomicTokenReplayProtection"/> covering replay detection,
/// distributed-cache fallback paths, and the BH-027 mutual-recursion regression.
/// </summary>
[SecurityTest]
[Protocol(TestProtocols.FeatureServer)]
[Operation(Operations.Security)]
public sealed class AtomicTokenReplayProtectionTests
{
    // ─── BH-027 regression ──────────────────────────────────────────────────────

    /// <summary>
    /// Regression test for BH-027: when the Redis IDatabase has not yet been initialized
    /// (lazy connect, _cache == null), the old fallback re-entered
    /// TryMarkTokenAsUsedDistributedAsync with the same RedisCache argument, which
    /// re-dispatched back to TryMarkTokenAsUsedRedisAsync — infinite mutual recursion
    /// → StackOverflowException on the very first JWT validation request.
    ///
    /// After the fix the fallback calls TryMarkTokenAsUsedNonAtomicAsync directly,
    /// breaking the cycle.  If the old code is restored, this test causes the test
    /// runner process to crash with a StackOverflowException instead of completing.
    /// </summary>
    [UnitTest]
    public async Task TryMarkTokenAsUsedAsync_RedisCacheWithNullDatabase_TerminatesWithoutRecursion()
    {
        // Port 65530 on loopback is almost certainly closed; a closed port returns
        // ECONNREFUSED immediately, so the connection fails in < 1 ms.
        var configOptions = new StackExchange.Redis.ConfigurationOptions
        {
            EndPoints = { "127.0.0.1:65530" },
            ConnectTimeout = 100,
            SyncTimeout = 100,
            AbortOnConnectFail = true,
            ConnectRetry = 0
        };
        using var redisCache = new Microsoft.Extensions.Caching.StackExchangeRedis.RedisCache(
            Microsoft.Extensions.Options.Options.Create(
                new Microsoft.Extensions.Caching.StackExchangeRedis.RedisCacheOptions
                {
                    ConfigurationOptions = configOptions
                }));

        // Precondition: _cache is null before any Redis operation (lazy initialization).
        // This is the exact state that triggered the StackOverflow: reflection returns null,
        // connection == null, the old code fell back into TryMarkTokenAsUsedDistributedAsync,
        // which re-dispatched to TryMarkTokenAsUsedRedisAsync, causing infinite recursion.
        var cacheField = redisCache.GetType().GetField(
            "_cache",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        cacheField.Should().NotBeNull("the private _cache field must be resolvable via reflection");
        cacheField!.GetValue(redisCache).Should().BeNull(
            "IDatabase is lazily initialized and must be null before the first Redis operation");

        var services = new ServiceCollection();
        services.AddSingleton<IDistributedCache>(redisCache);
        using var sp = services.BuildServiceProvider();

        var token = CreateTestJwtToken();
        var options = new TokenValidationOptions { EnableTokenReplayProtection = true };

        // Before the fix: StackOverflowException kills the test runner (no assertion reached).
        // After the fix: returns false because Redis is unreachable; the exception from the
        // non-atomic fallback path is caught and mapped to false (fail-secure).
        var result = await AtomicTokenReplayProtection.TryMarkTokenAsUsedAsync(
            token, options, sp, CancellationToken.None);

        result.Should().BeFalse(
            "the atomic Redis path failed (null IDatabase) and the non-atomic fallback also " +
            "failed (unreachable Redis); the method must degrade to false without recursing");
    }

    [UnitTest]
    public async Task TryMarkTokenAsUsedAsync_DistributedCacheAlwaysThrows_ReturnsFalseWithoutThrowing()
    {
        // When every distributed cache operation throws, the method must return false
        // (err on the side of security) and must NOT propagate the exception to the caller.
        var throwingCache = Substitute.For<IDistributedCache>();
        throwingCache
            .GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromException<byte[]?>(new InvalidOperationException("simulated Redis failure")));
        throwingCache
            .SetAsync(
                Arg.Any<string>(),
                Arg.Any<byte[]>(),
                Arg.Any<DistributedCacheEntryOptions>(),
                Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromException(new InvalidOperationException("simulated Redis failure")));

        var services = new ServiceCollection();
        services.AddSingleton<IDistributedCache>(throwingCache);
        using var sp = services.BuildServiceProvider();

        var token = CreateTestJwtToken();
        var options = new TokenValidationOptions { EnableTokenReplayProtection = true };

        var act = () => AtomicTokenReplayProtection.TryMarkTokenAsUsedAsync(
            token, options, sp, CancellationToken.None);

        var result = await act.Should().NotThrowAsync();
        result.Subject.Should().BeFalse(
            "a distributed cache failure is treated as a replay signal to fail-secure");
    }

    // ─── Happy-path replay detection ────────────────────────────────────────────

    [UnitTest]
    public async Task TryMarkTokenAsUsedAsync_MemoryDistributedCache_FirstUseTrueThenReplayFalse()
    {
        // Non-Redis IDistributedCache: first use accepted, replay rejected.
        var services = new ServiceCollection();
        services.AddDistributedMemoryCache();
        using var sp = services.BuildServiceProvider();

        var token = CreateTestJwtToken();
        var options = new TokenValidationOptions { EnableTokenReplayProtection = true };

        var firstResult = await AtomicTokenReplayProtection.TryMarkTokenAsUsedAsync(
            token, options, sp, CancellationToken.None);
        var replayResult = await AtomicTokenReplayProtection.TryMarkTokenAsUsedAsync(
            token, options, sp, CancellationToken.None);

        firstResult.Should().BeTrue("first use of a valid token must be accepted");
        replayResult.Should().BeFalse("a replayed token must be rejected");
    }

    [UnitTest]
    public async Task TryMarkTokenAsUsedAsync_MemoryCacheOnly_FirstUseTrueThenReplayFalse()
    {
        // In-memory-only path (no distributed cache): first use accepted, replay rejected.
        var services = new ServiceCollection();
        services.AddMemoryCache();
        using var sp = services.BuildServiceProvider();

        var token = CreateTestJwtToken();
        var options = new TokenValidationOptions { EnableTokenReplayProtection = true };

        var firstResult = await AtomicTokenReplayProtection.TryMarkTokenAsUsedAsync(
            token, options, sp, CancellationToken.None);
        var replayResult = await AtomicTokenReplayProtection.TryMarkTokenAsUsedAsync(
            token, options, sp, CancellationToken.None);

        firstResult.Should().BeTrue("first use must be accepted");
        replayResult.Should().BeFalse("replay must be rejected even in the in-memory path");
    }

    [UnitTest]
    public async Task TryMarkTokenAsUsedAsync_NoCacheAvailable_AllowsThrough()
    {
        // When no cache is registered, the method allows the token through (graceful
        // degradation; replay protection is best-effort when no cache is configured).
        var services = new ServiceCollection();
        using var sp = services.BuildServiceProvider();

        var token = CreateTestJwtToken();
        var options = new TokenValidationOptions { EnableTokenReplayProtection = true };

        var result = await AtomicTokenReplayProtection.TryMarkTokenAsUsedAsync(
            token, options, sp, CancellationToken.None);

        result.Should().BeTrue("with no cache, the method must degrade gracefully and allow the token");
    }

    [UnitTest]
    public async Task TryMarkTokenAsUsedAsync_DistinctTokens_EachAcceptedOnFirstUse()
    {
        // Two distinct tokens (different JTIs) are each accepted on first use.
        var services = new ServiceCollection();
        services.AddDistributedMemoryCache();
        using var sp = services.BuildServiceProvider();

        var tokenA = CreateTestJwtToken();
        var tokenB = CreateTestJwtToken();
        var options = new TokenValidationOptions { EnableTokenReplayProtection = true };

        var resultA = await AtomicTokenReplayProtection.TryMarkTokenAsUsedAsync(
            tokenA, options, sp, CancellationToken.None);
        var resultB = await AtomicTokenReplayProtection.TryMarkTokenAsUsedAsync(
            tokenB, options, sp, CancellationToken.None);

        resultA.Should().BeTrue("token A must be accepted on first use");
        resultB.Should().BeTrue("token B is a distinct token and must be accepted independently");
    }

    // ─── BH3-033 regression ─────────────────────────────────────────────────────

    /// <summary>
    /// Regression test for BH3-033: two concurrent requests carrying the same JWT must not
    /// both be accepted as first-use.  The old read-delay-verify pattern had a TOCTOU race
    /// where both requests could observe "not present" at T1, each write a unique marker,
    /// and each verify their own marker before the other's write arrived — causing both to
    /// return <see langword="true"/>.  After the fix a <see cref="SemaphoreSlim"/> serialises
    /// the check-then-set window so exactly one concurrent caller wins.
    /// </summary>
    [UnitTest]
    public async Task TryMarkTokenAsUsedAsync_ConcurrentRequestsSameToken_ExactlyOneWins()
    {
        var services = new ServiceCollection();
        services.AddDistributedMemoryCache();
        using var sp = services.BuildServiceProvider();

        var token = CreateTestJwtToken();
        var options = new TokenValidationOptions { EnableTokenReplayProtection = true };

        // Fire 8 concurrent requests for the same token and collect all results.
        var tasks = Enumerable.Range(0, 8)
            .Select(_ => AtomicTokenReplayProtection.TryMarkTokenAsUsedAsync(
                token, options, sp, CancellationToken.None))
            .ToArray();

        var results = await Task.WhenAll(tasks);

        results.Count(r => r).Should().Be(1,
            "exactly one concurrent first-use attempt must be accepted (BH3-033); " +
            "the rest must be treated as replays");
        results.Count(r => !r).Should().Be(7,
            "all other concurrent attempts must be rejected as replay");
    }

    // ─── Helpers ────────────────────────────────────────────────────────────────

    private static JwtSecurityToken CreateTestJwtToken()
    {
        var payload = new JwtPayload(
            issuer: "test-issuer",
            audience: "test-audience",
            claims: [new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())],
            notBefore: null,
            expires: DateTime.UtcNow.AddHours(1));
        return new JwtSecurityToken(new JwtHeader(), payload);
    }
}
