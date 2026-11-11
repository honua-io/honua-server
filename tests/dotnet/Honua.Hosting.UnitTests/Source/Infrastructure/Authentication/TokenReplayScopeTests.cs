// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.IdentityModel.Tokens.Jwt;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Security;
using Honua.TestKit.Attributes;
using Honua.TestKit.Helpers;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
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
    public async Task ValidTokenBeyondOldWindow_RetainsSurfaceIsolationAndNeverRefreshesExpiry(string initialSurface)
    {
        var time = new TransitionClock();
        var replays = new InMemoryTokenReplayStore(time);
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
        Assert.Equal(TokenReplayRegistrationResult.Registered, (await RegisterAsync(replays, key, initial, expiry)).Result);
        var admission = new TokenReplayAdmissionFeature(key, initial);
        Assert.Equal(initialSurface == "legacy", await BindAsync(replays, admission, "session-a"));
        var currentScope = initialSurface == "legacy"
            ? TokenReplayScope.ContinuationSurface(BuildTokenReplayContinuationMarker("session-a")) : initial;
        time.Advance(TimeSpan.FromMinutes(15));
        var same = await RegisterAsync(replays, key, currentScope, expiry);
        Assert.Equal(initialSurface == "legacy" ? TokenReplayRegistrationResult.Continued : TokenReplayRegistrationResult.Reused, same.Result);
        var otherScopes = initialSurface == "modern"
            ? new[] { TokenReplayScope.HttpApi, TokenReplayScope.ContinuationSurface(null), TokenReplayScope.ContinuationSurface(BuildTokenReplayContinuationMarker("session-a")) }
            : new[] { modern };
        foreach (var other in otherScopes)
        {
            Assert.Equal(TokenReplayRegistrationResult.ReplayDetected, (await RegisterAsync(replays, key, other, expiry)).Result);
        }

        time.Advance(TimeSpan.FromMinutes(49));
        await RegisterAsync(replays, key, currentScope, expiry.AddHours(1));
        if (initialSurface == "legacy")
        {
            Assert.True(await BindAsync(replays, admission, "session-a"));
        }

        Assert.Equal(same.Result, (await RegisterAsync(replays, key, currentScope, expiry)).Result);
        time.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(TokenReplayRegistrationResult.Registered, (await RegisterAsync(replays, key, modern, expiry.AddHours(1))).Result);
    }

    [UnitTest]
    public async Task RepeatedBindingWithLaterAdmissionExpiration_KeepsOriginalExpiration()
    {
        var time = new TransitionClock();
        var replays = new InMemoryTokenReplayStore(time);
        var key = Guid.NewGuid().ToString();
        var initialScope = TokenReplayScope.ContinuationSurface(null);
        Assert.Equal(TokenReplayRegistrationResult.Registered,
            (await RegisterAsync(replays, key, initialScope, DateTime.UtcNow.AddMinutes(10))).Result);
        var admission = new TokenReplayAdmissionFeature(key);
        Assert.True(await BindAsync(replays, admission, "session-a"));
        var continuedScope = TokenReplayScope.ContinuationSurface(BuildTokenReplayContinuationMarker("session-a"));
        Assert.Equal(TokenReplayRegistrationResult.Continued,
            (await RegisterAsync(replays, key, continuedScope, DateTime.UtcNow.AddMinutes(65))).Result);
        Assert.True(await BindAsync(replays, admission, "session-a"));

        time.Advance(TimeSpan.FromMinutes(11));

        Assert.Equal(TokenReplayRegistrationResult.Registered,
            (await RegisterAsync(replays, key, initialScope, DateTime.UtcNow.AddMinutes(10))).Result);
    }

    [UnitTest]
    public async Task ConcurrentModernFirstUse_RegistersOnceAndRefusesAnotherValidatedToken()
    {
        var replays = new InMemoryTokenReplayStore();
        var key = Guid.NewGuid().ToString();
        var expiry = DateTime.UtcNow.AddHours(1);
        var scope = TokenReplayScope.ReusableSurface("mcp", "validated-token-a");
        var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var requests = Enumerable.Range(0, 24).Select(_ => Task.Run(async () =>
        {
            await barrier.Task;
            return await RegisterAsync(replays, key, scope, expiry);
        })).ToArray();
        barrier.SetResult();
        var results = await Task.WhenAll(requests);
        Assert.Single(results, result => result.Result == TokenReplayRegistrationResult.Registered);
        Assert.Equal(23, results.Count(result => result.Result == TokenReplayRegistrationResult.Reused));
        var changed = TokenReplayScope.ReusableSurface("mcp", "validated-token-b");
        Assert.Equal(TokenReplayRegistrationResult.ReplayDetected, (await RegisterAsync(replays, key, changed, expiry)).Result);
        Assert.Equal(TokenReplayRegistrationResult.Reused, (await RegisterAsync(replays, key, scope, expiry)).Result);
    }

    [UnitTest]
    public async Task ExpiredPresentation_IsNotRecorded()
    {
        var replays = new InMemoryTokenReplayStore();
        var key = Guid.NewGuid().ToString();

        var result = await RegisterAsync(replays, key, TokenReplayScope.HttpApi, DateTime.UtcNow.AddMinutes(-1));

        Assert.Equal(TokenReplayRegistrationResult.Unavailable, result.Result);
        Assert.False(result.Recorded);
        Assert.Equal(TokenReplayRegistrationResult.Registered,
            (await RegisterAsync(replays, key, TokenReplayScope.HttpApi, DateTime.UtcNow.AddMinutes(5))).Result);
    }

    [UnitTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Register_SharedStoreUnavailable_FollowsTheConfiguredFailClosedSetting(bool failClosed)
    {
        var replays = Substitute.For<ITokenReplayStore>();
        replays.TryRegisterAsync(default, default)
            .ReturnsForAnyArgs(Task.FromException<TokenReplayRegistrationOutcome>(new InvalidOperationException("database unavailable")));

        var result = await TryRegisterTokenReplayAsync(
            new TokenReplayPresentation(Guid.NewGuid().ToString(), DateTime.UtcNow.AddHours(1), TokenReplayScope.ReusableSurface("mcp", "validated-token")),
            replays, failClosed, NullLogger.Instance, default);

        Assert.Equal(failClosed ? TokenReplayRegistrationResult.Unavailable : TokenReplayRegistrationResult.Skipped, result.Result);
        Assert.False(result.Recorded);
    }

    [UnitTest]
    public async Task Bind_SharedStoreUnavailable_ReturnsFalse()
    {
        var replays = Substitute.For<ITokenReplayStore>();
        replays.TryRebindAsync(default, default)
            .ReturnsForAnyArgs(Task.FromException<bool>(new InvalidOperationException("database unavailable")));

        Assert.False(await BindAsync(replays, new TokenReplayAdmissionFeature(Guid.NewGuid().ToString()), "session-a"));
    }

    private static Task<TokenReplayRegistration> RegisterAsync(ITokenReplayStore replays, string key, TokenReplayScope scope, DateTime expiry) =>
        TryRegisterTokenReplayAsync(new TokenReplayPresentation(key, expiry, scope), replays, true, NullLogger.Instance, default);

    private static Task<bool> BindAsync(ITokenReplayStore replays, TokenReplayAdmissionFeature admission, string continuationId)
    {
        var context = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection()
                .AddSingleton(replays)
                .AddSingleton<ILogger<OidcAuthenticationOptions>>(NullLogger<OidcAuthenticationOptions>.Instance)
                .BuildServiceProvider()
        };
        context.Features.Set(admission);
        return TryBindTokenReplayContinuationAsync(context, continuationId);
    }
}
