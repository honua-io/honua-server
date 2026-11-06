// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Infrastructure.Redis;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
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
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var bound = await TryBindTokenReplayContinuationAsync(
            admission,
            "continuation-1",
            multiplexer,
            cache,
            NullLogger.Instance,
            CancellationToken.None);

        Assert.False(bound);
    }

    [UnitTest]
    public async Task Register_CompletedReaderOnce_RegistersInRedis()
    {
        var calls = 0;
        var database = Substitute.For<IDatabase>();
        database.StringSetAsync(
                Arg.Any<RedisKey>(),
                Arg.Any<RedisValue>(),
                Arg.Any<TimeSpan?>(),
                Arg.Any<When>())
            .Returns(_ =>
            {
                if (Interlocked.Increment(ref calls) == 1)
                {
                    throw new InvalidOperationException(RedisClientFault.CompletedReaderMessage);
                }

                return true;
            });
        var multiplexer = Connected(database);

        var registration = await TryRegisterTokenReplayAsync(
            "jti:completed-reader",
            DateTime.UtcNow.AddMinutes(5),
            multiplexer,
            memoryCache: null,
            failClosed: true,
            TokenReplayScope.HttpApi,
            NullLogger.Instance,
            CancellationToken.None);

        Assert.Equal(TokenReplayRegistrationResult.Registered, registration.Result);
        Assert.Equal(TokenReplayStore.Redis, registration.Store);
        Assert.Equal(2, calls);
    }

    [UnitTest]
    public async Task Register_CompletedReaderPersists_UsesMemoryWhenNotFailClosed()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var database = Substitute.For<IDatabase>();
        database.StringSetAsync(
                Arg.Any<RedisKey>(),
                Arg.Any<RedisValue>(),
                Arg.Any<TimeSpan?>(),
                Arg.Any<When>())
            .ThrowsAsync(new InvalidOperationException(RedisClientFault.CompletedReaderMessage));

        var registration = await TryRegisterTokenReplayAsync(
            "jti:completed-reader-down",
            DateTime.UtcNow.AddMinutes(5),
            Connected(database),
            cache,
            failClosed: false,
            TokenReplayScope.HttpApi,
            NullLogger.Instance,
            CancellationToken.None);

        Assert.Equal(TokenReplayRegistrationResult.Registered, registration.Result);
        Assert.Equal(TokenReplayStore.Memory, registration.Store);
    }

    [UnitTest]
    public async Task Register_OtherInvalidOperation_StillThrows()
    {
        var database = Substitute.For<IDatabase>();
        database.StringSetAsync(
                Arg.Any<RedisKey>(),
                Arg.Any<RedisValue>(),
                Arg.Any<TimeSpan?>(),
                Arg.Any<When>())
            .ThrowsAsync(new InvalidOperationException("changed on every attempt"));

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => TryRegisterTokenReplayAsync(
            "jti:other",
            DateTime.UtcNow.AddMinutes(5),
            Connected(database),
            memoryCache: null,
            failClosed: false,
            TokenReplayScope.HttpApi,
            NullLogger.Instance,
            CancellationToken.None));

        Assert.Equal("changed on every attempt", thrown.Message);
    }

    private static IConnectionMultiplexer Connected(IDatabase database)
    {
        var multiplexer = Substitute.For<IConnectionMultiplexer>();
        multiplexer.IsConnected.Returns(true);
        multiplexer.GetDatabase().Returns(database);
        multiplexer.GetDatabase(Arg.Any<int>(), Arg.Any<object?>()).Returns(database);
        return multiplexer;
    }

    private static IConnectionMultiplexer TimeoutMultiplexer()
    {
        var database = Substitute.For<IDatabase>();
        database.StringSetAsync(
                Arg.Any<RedisKey>(),
                Arg.Any<RedisValue>(),
                Arg.Any<TimeSpan?>(),
                Arg.Any<When>())
            .ThrowsAsync(new TimeoutException("redis timeout"));
        var multiplexer = Substitute.For<IConnectionMultiplexer>();
        multiplexer.IsConnected.Returns(true);
        multiplexer.GetDatabase().Returns(database);
        multiplexer.GetDatabase(Arg.Any<int>(), Arg.Any<object?>()).Returns(database);
        return multiplexer;
    }
}
