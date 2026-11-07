// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.IdentityModel.Tokens.Jwt;
using Honua.Infrastructure.Authentication;
using Honua.TestKit.Attributes;
using Honua.TestKit.Helpers;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Internal;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using StackExchange.Redis;
using static Honua.Infrastructure.Authentication.OidcAuthenticationExtensions;

namespace Honua.Hosting.UnitTests.Authentication;

[SecurityTest]
[Protocol(TestProtocols.TestQuality)]
[Operation(Operations.Security)]
public sealed class TokenReplayScopeTests
{
    [UnitTheory]
    [InlineData("http")]
    [InlineData("legacy")]
    [InlineData("modern")]
    public async Task Memory_ValidTokenBeyondOldWindow_RetainsSurfaceIsolationAndNeverRefreshesExpiry(string initialSurface)
    {
        var time = new TransitionClock();
        var cacheClock = Substitute.For<ISystemClock>();
        cacheClock.UtcNow.Returns(_ => time.GetUtcNow());
        using var cache = new MemoryCache(new MemoryCacheOptions { Clock = cacheClock });
        var key = Guid.NewGuid().ToString();
        var token = new JwtSecurityToken(expires: time.GetUtcNow().UtcDateTime.AddHours(1));
        var expiry = GetReplayCacheExpiration(token, new TokenValidationOptions(), time.GetUtcNow().UtcDateTime)!.Value;
        var modern = TokenReplayScope.ReusableSurface("mcp", "validated-token");
        var initial = initialSurface switch
        {
            "http" => TokenReplayScope.HttpApi,
            "legacy" => TokenReplayScope.ContinuationSurface(null),
            _ => modern
        };
        Assert.Equal(TokenReplayRegistrationResult.Registered, (await RegisterAsync(cache, key, initial, expiry)).Result);
        var admission = new TokenReplayAdmissionFeature(key, expiry, TokenReplayStore.Memory, initial);
        var binding = await TryBindTokenReplayContinuationAsync(admission, "session-a", null, cache, NullLogger.Instance, default);
        Assert.Equal(initialSurface == "legacy", binding);
        var currentScope = initialSurface == "legacy"
            ? TokenReplayScope.ContinuationSurface(BuildTokenReplayContinuationMarker("session-a")) : initial;
        var expectedMarker = cache.Get<string>(key);
        time.Advance(TimeSpan.FromMinutes(15));
        var same = await RegisterAsync(cache, key, currentScope, expiry);
        Assert.Equal(initialSurface == "legacy" ? TokenReplayRegistrationResult.Continued : TokenReplayRegistrationResult.Reused, same.Result);
        var otherScopes = initialSurface == "modern"
            ? new[] { TokenReplayScope.HttpApi, TokenReplayScope.ContinuationSurface(null), TokenReplayScope.ContinuationSurface(BuildTokenReplayContinuationMarker("session-a")) }
            : new[] { modern };
        foreach (var other in otherScopes)
        {
            Assert.Equal(TokenReplayRegistrationResult.ReplayDetected, (await RegisterAsync(cache, key, other, expiry)).Result);
        }
        Assert.Equal(expectedMarker, cache.Get<string>(key));
        time.Advance(TimeSpan.FromMinutes(49));
        await RegisterAsync(cache, key, currentScope, expiry.AddHours(1));
        if (initialSurface == "legacy")
        {
            Assert.True(await TryBindTokenReplayContinuationAsync(admission, "session-a", null, cache, NullLogger.Instance, default));
        }
        Assert.True(cache.TryGetValue(key, out _));
        time.Advance(TimeSpan.FromMinutes(2));
        Assert.False(cache.TryGetValue(key, out _));
    }

    [UnitTest]
    public async Task Memory_RepeatedBindingWithLaterAdmissionExpiration_KeepsOriginalExpiration()
    {
        var time = new TransitionClock();
        var cacheClock = Substitute.For<ISystemClock>();
        cacheClock.UtcNow.Returns(_ => time.GetUtcNow());
        using var cache = new MemoryCache(new MemoryCacheOptions { Clock = cacheClock });
        var key = Guid.NewGuid().ToString();
        var originalExpiration = time.GetUtcNow().UtcDateTime.AddMinutes(10);
        var laterExpiration = time.GetUtcNow().UtcDateTime.AddMinutes(65);
        var initialScope = TokenReplayScope.ContinuationSurface(null);
        Assert.Equal(TokenReplayRegistrationResult.Registered, (await RegisterAsync(cache, key, initialScope, originalExpiration)).Result);
        var originalAdmission = new TokenReplayAdmissionFeature(key, originalExpiration, TokenReplayStore.Memory);
        Assert.True(await TryBindTokenReplayContinuationAsync(originalAdmission, "session-a", null, cache, NullLogger.Instance, default));
        var continuedScope = TokenReplayScope.ContinuationSurface(BuildTokenReplayContinuationMarker("session-a"));
        Assert.Equal(TokenReplayRegistrationResult.Continued, (await RegisterAsync(cache, key, continuedScope, laterExpiration)).Result);
        var laterAdmission = new TokenReplayAdmissionFeature(key, laterExpiration, TokenReplayStore.Memory);
        Assert.True(await TryBindTokenReplayContinuationAsync(laterAdmission, "session-a", null, cache, NullLogger.Instance, default));
        time.Advance(TimeSpan.FromMinutes(11));
        Assert.False(cache.TryGetValue(key, out _));
    }

    [UnitTest]
    public async Task Memory_ConcurrentModernFirstUse_RegistersOnceAndRefusesAnotherValidatedToken()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var key = Guid.NewGuid().ToString();
        var expiry = DateTime.UtcNow.AddHours(1);
        var scope = TokenReplayScope.ReusableSurface("mcp", "validated-token-a");
        var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var requests = Enumerable.Range(0, 24).Select(_ => Task.Run(async () =>
        {
            await barrier.Task;
            return await RegisterAsync(cache, key, scope, expiry);
        })).ToArray();
        barrier.SetResult();
        var results = await Task.WhenAll(requests);
        Assert.Single(results, result => result.Result == TokenReplayRegistrationResult.Registered);
        Assert.Equal(23, results.Count(result => result.Result == TokenReplayRegistrationResult.Reused));
        var changed = TokenReplayScope.ReusableSurface("mcp", "validated-token-b");
        Assert.Equal(TokenReplayRegistrationResult.ReplayDetected, (await RegisterAsync(cache, key, changed, expiry)).Result);
        Assert.Equal(scope.RegistrationValue, cache.Get<string>(key));
    }

    [UnitTest]
    public async Task Memory_ExpiredRegistration_IsNotCreatedWithSlidingFallback()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var key = Guid.NewGuid().ToString();
        var result = await RegisterAsync(cache, key, TokenReplayScope.HttpApi, DateTime.UtcNow.AddMinutes(-1));
        Assert.Equal(TokenReplayRegistrationResult.Unavailable, result.Result);
        Assert.False(cache.TryGetValue(key, out _));
    }

    [UnitTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Register_SharedStoreUnavailable_PreservesConfiguredFailClosedBehavior(bool failClosed)
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var redis = Substitute.For<IConnectionMultiplexer>();
        redis.IsConnected.Returns(false);
        var key = Guid.NewGuid().ToString();
        var result = await TryRegisterTokenReplayAsync(key, DateTime.UtcNow.AddHours(1), redis, cache, failClosed,
            TokenReplayScope.ReusableSurface("mcp", "validated-token"), NullLogger.Instance, default);
        Assert.Equal(failClosed ? TokenReplayRegistrationResult.Unavailable : TokenReplayRegistrationResult.Registered, result.Result);
        Assert.Equal(!failClosed, cache.TryGetValue(key, out _));
    }

    private static Task<TokenReplayRegistration> RegisterAsync(MemoryCache cache, string key, TokenReplayScope scope, DateTime expiry) =>
        TryRegisterTokenReplayAsync(key, expiry, null, cache, true, scope, NullLogger.Instance, default);
}
