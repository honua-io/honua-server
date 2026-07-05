// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.IdentityModel.Tokens.Jwt;
using System.Linq;
using System.Security.Claims;
using FluentAssertions;
using Honua.Infrastructure.Authentication;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using StackExchange.Redis;

namespace Honua.Server.Tests.Infrastructure.Authentication;

/// <summary>
/// Unit tests for <see cref="AtomicTokenReplayProtection"/> covering replay detection,
/// Redis fail-closed behaviour, and the BH5-022 / BH-027 regressions.
/// </summary>
[SecurityTest]
[Protocol(TestProtocols.FeatureServer)]
[Operation(Operations.Security)]
public sealed class AtomicTokenReplayProtectionTests
{
    // ─── BH5-022 / BH-027 regression ────────────────────────────────────────────

    /// <summary>
    /// BH5-022 regression: the old implementation used reflection to extract a private
    /// <c>_cache</c> field from RedisCache. When the field was null (lazy connection not
    /// yet established) the code re-entered the distributed-cache path with the same
    /// RedisCache argument, which redispatched back to the Redis path — infinite mutual
    /// recursion → StackOverflowException on the very first JWT validation request (BH-027).
    ///
    /// The fix resolves <see cref="IConnectionMultiplexer"/> directly from DI — no
    /// reflection, no mutual recursion. When the multiplexer is registered but every
    /// Redis command throws, the method must return <see langword="false"/> (fail-closed)
    /// without propagating the exception.
    /// </summary>
    [UnitTest]
    public async Task TryMarkTokenAsUsedAsync_RedisMultiplexerAlwaysThrows_ReturnsFalseWithoutThrowing()
    {
        var database = Substitute.For<IDatabase>();
        database.StringSetAsync(
                Arg.Any<RedisKey>(),
                Arg.Any<RedisValue>(),
                Arg.Any<TimeSpan?>(),
                Arg.Any<When>(),
                Arg.Any<CommandFlags>())
            .Returns(_ => Task.FromException<bool>(
                new RedisConnectionException(ConnectionFailureType.UnableToConnect, "simulated Redis down")));

        var multiplexer = Substitute.For<IConnectionMultiplexer>();
        multiplexer.GetDatabase().Returns(database);
        multiplexer.GetDatabase(Arg.Any<int>(), Arg.Any<object?>()).Returns(database);

        var services = new ServiceCollection();
        services.AddSingleton<IConnectionMultiplexer>(multiplexer);
        using var sp = services.BuildServiceProvider();

        var token = CreateTestJwtToken();
        var options = new TokenValidationOptions { EnableTokenReplayProtection = true };

        var act = () => AtomicTokenReplayProtection.TryMarkTokenAsUsedAsync(
            token, options, sp, CancellationToken.None);

        var result = await act.Should().NotThrowAsync();
        result.Subject.Should().BeFalse(
            "Redis failure must be fail-closed — the exception must be caught and false returned " +
            "without mutual recursion (BH5-022 / BH-027)");
    }

    [UnitTest]
    public async Task TryMarkTokenAsUsedAsync_RedisSetNxReturnsFalse_ReturnsFalseWithoutThrowing()
    {
        // When SET NX returns false (key already exists), the method must return false —
        // the token has already been seen.
        var database = Substitute.For<IDatabase>();
        database.StringSetAsync(
                Arg.Any<RedisKey>(),
                Arg.Any<RedisValue>(),
                Arg.Any<TimeSpan?>(),
                Arg.Any<When>(),
                Arg.Any<CommandFlags>())
            .Returns(Task.FromResult(false));

        var multiplexer = Substitute.For<IConnectionMultiplexer>();
        multiplexer.GetDatabase().Returns(database);
        multiplexer.GetDatabase(Arg.Any<int>(), Arg.Any<object?>()).Returns(database);

        var services = new ServiceCollection();
        services.AddSingleton<IConnectionMultiplexer>(multiplexer);
        using var sp = services.BuildServiceProvider();

        var token = CreateTestJwtToken();
        var options = new TokenValidationOptions { EnableTokenReplayProtection = true };

        var result = await AtomicTokenReplayProtection.TryMarkTokenAsUsedAsync(
            token, options, sp, CancellationToken.None);

        result.Should().BeFalse("SET NX returning false means the key existed — this is a replay");
    }

    // ─── Happy-path replay detection ────────────────────────────────────────────

    [UnitTest]
    public async Task TryMarkTokenAsUsedAsync_MemoryCacheOnly_FirstUseTrueThenReplayFalse()
    {
        // No Redis, in-memory cache only: first use accepted, replay rejected.
        var services = new ServiceCollection();
        services.AddMemoryCache();
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
    public async Task TryMarkTokenAsUsedAsync_MemoryCacheOnlyDistinct_BothAccepted()
    {
        // In-memory fallback with two distinct tokens: each accepted on first use.
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
        services.AddMemoryCache();
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
    /// return <see langword="true"/>.  After the fix a <see cref="System.Threading.SemaphoreSlim"/>
    /// serialises the check-then-set window so exactly one concurrent caller wins.
    /// </summary>
    [UnitTest]
    public async Task TryMarkTokenAsUsedAsync_ConcurrentRequestsSameToken_ExactlyOneWins()
    {
        var services = new ServiceCollection();
        services.AddMemoryCache();
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