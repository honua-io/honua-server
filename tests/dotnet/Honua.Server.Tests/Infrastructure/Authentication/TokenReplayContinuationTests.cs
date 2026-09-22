// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Infrastructure.Authentication;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using StackExchange.Redis;
using Testcontainers.Redis;
using static Honua.Infrastructure.Authentication.OidcAuthenticationExtensions;

namespace Honua.Server.Tests.Infrastructure.Authentication;

/// <summary>
/// Token replay surfaces and continuations (honua-server#4909, honua-server#4899) against
/// both replay stores: a token bound to a server-issued continuation is admitted only when
/// the same continuation is presented, is never moved to another continuation, and stays a
/// replay otherwise; a token admitted on the HTTP API stays reusable there and is a replay
/// on a continuation surface.
/// </summary>
/// <remarks>Store-level tests issue no HTTP requests and claim no endpoint coverage.</remarks>
[SecurityTest]
[Protocol(TestProtocols.TestQuality)]
[Operation(Operations.Security)]
public sealed class TokenReplayContinuationTests
{
    private const string SessionA = "mcp-session:session-a";
    private const string SessionB = "mcp-session:session-b";

    [UnitTest]
    public async Task MemoryStore_BoundToken_IsAdmittedOnlyOnItsContinuation()
    {
        using var memoryCache = new MemoryCache(new MemoryCacheOptions());

        await AssertContinuationSemanticsAsync(redis: null, memoryCache, TokenReplayStore.Memory);
    }

    [IntegrationTest]
    public async Task RedisStore_BoundToken_IsAdmittedOnlyOnItsContinuationAndKeepsExpiry()
    {
        await using var container = new RedisBuilder("redis:7.2-alpine").Build();
        await container.StartAsync();
        using var redis = await ConnectionMultiplexer.ConnectAsync(container.GetConnectionString());

        var tokenKey = await AssertContinuationSemanticsAsync(redis, memoryCache: null, TokenReplayStore.Redis);

        var ttl = await redis.GetDatabase().KeyTimeToLiveAsync(tokenKey);
        ttl.Should().NotBeNull("binding a continuation must keep the registration's expiry");
        ttl!.Value.Should().BeGreaterThan(TimeSpan.Zero).And.BeLessThanOrEqualTo(TimeSpan.FromMinutes(10));
    }

    [UnitTest]
    public async Task MemoryStore_HttpApiToken_IsReusableThereAndReplaysOnContinuationSurface()
    {
        using var memoryCache = new MemoryCache(new MemoryCacheOptions());

        await AssertHttpApiReuseSemanticsAsync(redis: null, memoryCache, TokenReplayStore.Memory);
    }

    [IntegrationTest]
    public async Task RedisStore_HttpApiToken_IsReusableThereAndReplaysOnContinuationSurfaceAndKeepsExpiry()
    {
        await using var container = new RedisBuilder("redis:7.2-alpine").Build();
        await container.StartAsync();
        using var redis = await ConnectionMultiplexer.ConnectAsync(container.GetConnectionString());

        var tokenKey = await AssertHttpApiReuseSemanticsAsync(redis, memoryCache: null, TokenReplayStore.Redis);

        var ttl = await redis.GetDatabase().KeyTimeToLiveAsync(tokenKey);
        ttl.Should().NotBeNull("reuse must not extend or drop the registration's expiry");
        ttl!.Value.Should().BeGreaterThan(TimeSpan.Zero).And.BeLessThanOrEqualTo(TimeSpan.FromMinutes(10));
    }

    private static async Task<string> AssertContinuationSemanticsAsync(
        IConnectionMultiplexer? redis,
        IMemoryCache? memoryCache,
        TokenReplayStore expectedStore)
    {
        var tokenKey = $"jti:test:{Guid.NewGuid():N}";
        var expiresOn = DateTime.UtcNow.AddMinutes(10);
        var markerA = BuildTokenReplayContinuationMarker(SessionA);
        var markerB = BuildTokenReplayContinuationMarker(SessionB);

        Task<TokenReplayRegistration> RegisterAsync(string? marker) =>
            TryRegisterTokenReplayAsync(
                tokenKey, expiresOn, redis, memoryCache, failClosed: true, TokenReplayScope.ContinuationSurface(marker),
                NullLogger.Instance, CancellationToken.None);
        Task<TokenReplayRegistration> RegisterOnHttpApiAsync() =>
            TryRegisterTokenReplayAsync(
                tokenKey, expiresOn, redis, memoryCache, failClosed: true, TokenReplayScope.HttpApi,
                NullLogger.Instance, CancellationToken.None);
        Task<bool> BindAsync(TokenReplayAdmissionFeature admission, string continuationId) =>
            TryBindTokenReplayContinuationAsync(
                admission, continuationId, redis, memoryCache, NullLogger.Instance, CancellationToken.None);

        var first = await RegisterAsync(marker: null);
        first.Result.Should().Be(TokenReplayRegistrationResult.Registered);
        first.Store.Should().Be(expectedStore);
        var admission = new TokenReplayAdmissionFeature(tokenKey, expiresOn, expectedStore);

        (await RegisterAsync(markerA)).Result.Should().Be(TokenReplayRegistrationResult.ReplayDetected,
            "an unbound token is single-use even when a continuation is presented");
        (await RegisterOnHttpApiAsync()).Result.Should().Be(TokenReplayRegistrationResult.ReplayDetected,
            "a token registered on a continuation surface is a replay on the HTTP API");

        (await BindAsync(admission, SessionA)).Should().BeTrue();
        (await BindAsync(admission, SessionA)).Should().BeTrue("binding the same continuation is idempotent");
        (await BindAsync(admission, SessionB)).Should().BeFalse("a bound token is never moved to another continuation");

        (await RegisterAsync(markerA)).Result.Should().Be(TokenReplayRegistrationResult.Continued);
        (await RegisterAsync(markerA)).Result.Should().Be(TokenReplayRegistrationResult.Continued);
        (await RegisterAsync(markerB)).Result.Should().Be(TokenReplayRegistrationResult.ReplayDetected,
            "another continuation cannot reuse the bound token");
        (await RegisterAsync(marker: null)).Result.Should().Be(TokenReplayRegistrationResult.ReplayDetected,
            "a request presenting no continuation cannot reuse the bound token");
        (await RegisterOnHttpApiAsync()).Result.Should().Be(TokenReplayRegistrationResult.ReplayDetected,
            "a token bound to a continuation is a replay on the HTTP API");

        var unregistered = new TokenReplayAdmissionFeature($"jti:test:{Guid.NewGuid():N}", expiresOn, expectedStore);
        (await BindAsync(unregistered, SessionA)).Should().BeFalse("only a registered token can be bound");

        return tokenKey;
    }

    private static async Task<string> AssertHttpApiReuseSemanticsAsync(
        IConnectionMultiplexer? redis,
        IMemoryCache? memoryCache,
        TokenReplayStore expectedStore)
    {
        var tokenKey = $"jti:test:{Guid.NewGuid():N}";
        var expiresOn = DateTime.UtcNow.AddMinutes(10);
        var markerA = BuildTokenReplayContinuationMarker(SessionA);

        Task<TokenReplayRegistration> RegisterAsync(TokenReplayScope scope) =>
            TryRegisterTokenReplayAsync(
                tokenKey, expiresOn, redis, memoryCache, failClosed: true, scope,
                NullLogger.Instance, CancellationToken.None);

        var first = await RegisterAsync(TokenReplayScope.HttpApi);
        first.Result.Should().Be(TokenReplayRegistrationResult.Registered);
        first.Store.Should().Be(expectedStore);

        var reused = await RegisterAsync(TokenReplayScope.HttpApi);
        reused.Result.Should().Be(TokenReplayRegistrationResult.Reused,
            "an access token stays reusable on the HTTP API within its lifetime");
        reused.Store.Should().Be(expectedStore);
        (await RegisterAsync(TokenReplayScope.HttpApi)).Result.Should().Be(TokenReplayRegistrationResult.Reused);

        (await RegisterAsync(TokenReplayScope.ContinuationSurface(null))).Result
            .Should().Be(TokenReplayRegistrationResult.ReplayDetected,
                "a token admitted on the HTTP API cannot open a continuation");
        (await RegisterAsync(TokenReplayScope.ContinuationSurface(markerA))).Result
            .Should().Be(TokenReplayRegistrationResult.ReplayDetected,
                "a token admitted on the HTTP API cannot continue a session");

        var admission = new TokenReplayAdmissionFeature(tokenKey, expiresOn, expectedStore);
        (await TryBindTokenReplayContinuationAsync(
                admission, SessionA, redis, memoryCache, NullLogger.Instance, CancellationToken.None))
            .Should().BeFalse("a token admitted on the HTTP API is never bound to a continuation");
        (await RegisterAsync(TokenReplayScope.ContinuationSurface(markerA))).Result
            .Should().Be(TokenReplayRegistrationResult.ReplayDetected);
        (await RegisterAsync(TokenReplayScope.HttpApi)).Result.Should().Be(TokenReplayRegistrationResult.Reused,
            "a refused bind leaves the HTTP API registration intact");

        return tokenKey;
    }
}
