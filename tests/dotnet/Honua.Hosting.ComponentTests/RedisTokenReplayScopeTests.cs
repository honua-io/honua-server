// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Infrastructure.Authentication;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging.Abstractions;
using StackExchange.Redis;
using Operations = Honua.TestKit.Constants.Operations;
using TestProtocols = Honua.TestKit.Constants.ProtocolNames;
using static Honua.Infrastructure.Authentication.OidcAuthenticationExtensions;

namespace Honua.Hosting.ComponentTests;

[Collection(AiAdmissionRedisFixture.CollectionName)]
[Protocol(TestProtocols.TestQuality)]
[Operation(Operations.Security)]
public sealed class RedisTokenReplayScopeTests(AiAdmissionRedisFixture fixture)
{
    [IntegrationTheory]
    [InlineData("http")]
    [InlineData("legacy")]
    [InlineData("modern")]
    public async Task Register_IndependentConnections_EnforceSurfaceAndFingerprintWithFixedExpiry(string initialSurface)
    {
        using var first = await ConnectionMultiplexer.ConnectAsync(fixture.ConnectionString);
        using var second = await ConnectionMultiplexer.ConnectAsync(fixture.ConnectionString);
        var key = Guid.NewGuid().ToString();
        var expiry = DateTime.UtcNow.AddMinutes(65);
        var modern = TokenReplayScope.ReusableSurface("mcp", "validated-token-a");
        var scope = initialSurface switch
        {
            "http" => TokenReplayScope.HttpApi,
            "legacy" => TokenReplayScope.ContinuationSurface(null),
            _ => modern
        };
        Task<TokenReplayRegistration> RegisterAsync(IConnectionMultiplexer connection, TokenReplayScope requested) =>
            TryRegisterTokenReplayAsync(key, expiry, connection, null, true, requested, NullLogger.Instance, default);
        Assert.Equal(TokenReplayRegistrationResult.Registered, (await RegisterAsync(first, scope)).Result);
        var originalExpiry = await first.GetDatabase().KeyExpireTimeAsync(key);
        Assert.NotNull(originalExpiry);
        var admission = new TokenReplayAdmissionFeature(key, expiry, TokenReplayStore.Redis) { Scope = scope };
        var bound = await TryBindTokenReplayContinuationAsync(admission, "session-a", second, null, NullLogger.Instance, default);
        Assert.Equal(initialSurface == "legacy", bound);
        var continued = initialSurface == "legacy"
            ? TokenReplayScope.ContinuationSurface(BuildTokenReplayContinuationMarker("session-a")) : scope;
        var accepted = await RegisterAsync(second, continued);
        Assert.Equal(initialSurface == "legacy" ? TokenReplayRegistrationResult.Continued : TokenReplayRegistrationResult.Reused, accepted.Result);
        var others = initialSurface == "modern"
            ? new[] { TokenReplayScope.HttpApi, TokenReplayScope.ContinuationSurface(null), TokenReplayScope.ContinuationSurface(BuildTokenReplayContinuationMarker("session-a")) }
            : new[] { modern };
        foreach (var other in others)
        {
            Assert.Equal(TokenReplayRegistrationResult.ReplayDetected, (await RegisterAsync(second, other)).Result);
        }
        var differentToken = TokenReplayScope.ReusableSurface("mcp", "validated-token-b");
        Assert.Equal(TokenReplayRegistrationResult.ReplayDetected, (await RegisterAsync(second, differentToken)).Result);
        Assert.Equal(originalExpiry, await second.GetDatabase().KeyExpireTimeAsync(key));
    }

    [IntegrationTest]
    public async Task Register_ConcurrentConnections_AdmitOnlyOneRegistrationWithoutExtendingExpiry()
    {
        using var first = await ConnectionMultiplexer.ConnectAsync(fixture.ConnectionString);
        using var second = await ConnectionMultiplexer.ConnectAsync(fixture.ConnectionString);
        var key = Guid.NewGuid().ToString();
        var expiry = DateTime.UtcNow.AddMinutes(65);
        var scope = TokenReplayScope.ReusableSurface("mcp", "validated-token");
        var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var requests = Enumerable.Range(0, 24).Select(async index =>
        {
            await barrier.Task;
            return await TryRegisterTokenReplayAsync(key, expiry, index % 2 == 0 ? first : second,
                null, true, scope, NullLogger.Instance, default);
        }).ToArray();
        barrier.SetResult();
        var results = await Task.WhenAll(requests);
        Assert.Single(results, result => result.Result == TokenReplayRegistrationResult.Registered);
        Assert.Equal(23, results.Count(result => result.Result == TokenReplayRegistrationResult.Reused));
        var original = await first.GetDatabase().KeyExpireTimeAsync(key);
        await TryRegisterTokenReplayAsync(key, expiry.AddHours(1), second, null, true, scope, NullLogger.Instance, default);
        Assert.Equal(original, await second.GetDatabase().KeyExpireTimeAsync(key));
        Assert.Equal(scope.RegistrationValue, (string?)await second.GetDatabase().StringGetAsync(key));
    }
}
