// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.TestKit.Attributes;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using StackExchange.Redis;
using static Honua.Infrastructure.Authentication.OidcAuthenticationExtensions;

namespace Honua.Server.Tests.Infrastructure.Authentication;

/// <summary>
/// OIDC bearer replay registration must catch Redis timeouts. A timeout must not escape
/// into HTTP 500, and fail-closed deployments still reject the token.
/// </summary>
[SecurityTest]
[Protocol(TestProtocols.Admin)]
[Operation(Operations.Security)]
public sealed class TokenReplayTimeoutTests
{
    [UnitTest]
    public async Task Register_RedisTimeout_UsesMemoryWhenNotFailClosed()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var registration = await TryRegisterTokenReplayAsync(
            "jti:timeout",
            DateTime.UtcNow.AddMinutes(5),
            TimeoutMultiplexer(),
            cache,
            failClosed: false,
            TokenReplayScope.HttpApi,
            NullLogger.Instance,
            CancellationToken.None);

        Assert.Equal(TokenReplayRegistrationResult.Registered, registration.Result);
        Assert.Equal(TokenReplayStore.Memory, registration.Store);
    }

    [UnitTest]
    public async Task Register_RedisTimeout_IsUnavailableWhenFailClosed()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var registration = await TryRegisterTokenReplayAsync(
            "jti:timeout-closed",
            DateTime.UtcNow.AddMinutes(5),
            TimeoutMultiplexer(),
            cache,
            failClosed: true,
            TokenReplayScope.HttpApi,
            NullLogger.Instance,
            CancellationToken.None);

        Assert.Equal(TokenReplayRegistrationResult.Unavailable, registration.Result);
    }

    [UnitTest]
    public async Task Bind_RedisTimeout_ReturnsFalse()
    {
        var database = Substitute.For<IDatabase>();
        database.ScriptEvaluateAsync(
                Arg.Any<string>(),
                Arg.Any<RedisKey[]>(),
                Arg.Any<RedisValue[]>(),
                Arg.Any<CommandFlags>())
            .ThrowsAsync(new TimeoutException("redis timeout"));
        var multiplexer = Substitute.For<IConnectionMultiplexer>();
        multiplexer.IsConnected.Returns(true);
        multiplexer.GetDatabase().Returns(database);
        multiplexer.GetDatabase(Arg.Any<int>(), Arg.Any<object?>()).Returns(database);

        var admission = new TokenReplayAdmissionFeature(
            "jti:bind",
            DateTime.UtcNow.AddMinutes(5),
            TokenReplayStore.Redis);
        var bound = await TryBindTokenReplayContinuationAsync(
            admission,
            "continuation-1",
            multiplexer,
            new MemoryCache(new MemoryCacheOptions()),
            NullLogger.Instance,
            CancellationToken.None);

        Assert.False(bound);
    }

    private static IConnectionMultiplexer TimeoutMultiplexer()
    {
        var database = Substitute.For<IDatabase>();
        database.StringSetAsync(
                Arg.Any<RedisKey>(),
                Arg.Any<RedisValue>(),
                Arg.Any<TimeSpan?>(),
                Arg.Any<When>(),
                Arg.Any<CommandFlags>())
            .ThrowsAsync(new TimeoutException("redis timeout"));
        var multiplexer = Substitute.For<IConnectionMultiplexer>();
        multiplexer.IsConnected.Returns(true);
        multiplexer.GetDatabase().Returns(database);
        multiplexer.GetDatabase(Arg.Any<int>(), Arg.Any<object?>()).Returns(database);
        return multiplexer;
    }
}
