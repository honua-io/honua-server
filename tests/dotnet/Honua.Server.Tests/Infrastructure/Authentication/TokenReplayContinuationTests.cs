// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Security;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using static Honua.Infrastructure.Authentication.OidcAuthenticationExtensions;

namespace Honua.Server.Tests.Infrastructure.Authentication;

// A token bound to a server-issued continuation is admitted only when the same continuation is
// presented, is never moved to another continuation, and stays a replay otherwise; a token admitted
// on the HTTP API stays reusable there and is a replay on a continuation surface.
[SecurityTest]
[Protocol(TestProtocols.TestQuality)]
[Operation(Operations.Security)]
public sealed class TokenReplayContinuationTests
{
    [UnitTest]
    public async Task BoundToken_IsAdmittedOnlyOnItsContinuation()
    {
        var replays = new InMemoryTokenReplayStore();

        await TokenReplayContinuationSemantics.AssertContinuationAsync(() => replays);
    }

    [UnitTest]
    public async Task HttpApiToken_IsReusableThereAndReplaysOnContinuationSurface()
    {
        var replays = new InMemoryTokenReplayStore();

        await TokenReplayContinuationSemantics.AssertHttpApiReuseAsync(() => replays);
    }
}

// Each call to nextNode may return a different node's store, so the same rules hold across nodes.
internal static class TokenReplayContinuationSemantics
{
    private const string SessionA = "mcp-session:session-a";
    private const string SessionB = "mcp-session:session-b";

    public static async Task AssertContinuationAsync(Func<ITokenReplayStore> nextNode)
    {
        var tokenKey = $"jti:test:{Guid.NewGuid():N}";
        var markerA = BuildTokenReplayContinuationMarker(SessionA);
        var markerB = BuildTokenReplayContinuationMarker(SessionB);
        Task<TokenReplayRegistration> RegisterAsync(TokenReplayScope scope) => Register(nextNode(), tokenKey, scope);

        var first = await RegisterAsync(TokenReplayScope.ContinuationSurface(null));
        first.Result.Should().Be(TokenReplayRegistrationResult.Registered);
        first.Recorded.Should().BeTrue();
        var admission = new TokenReplayAdmissionFeature(tokenKey);

        (await RegisterAsync(TokenReplayScope.ContinuationSurface(markerA))).Result.Should().Be(TokenReplayRegistrationResult.ReplayDetected,
            "an unbound token is single-use even when a continuation is presented");
        (await RegisterAsync(TokenReplayScope.HttpApi)).Result.Should().Be(TokenReplayRegistrationResult.ReplayDetected,
            "a token registered on a continuation surface is a replay on the HTTP API");

        (await BindAsync(nextNode(), admission, SessionA)).Should().BeTrue();
        (await BindAsync(nextNode(), admission, SessionA)).Should().BeTrue("binding the same continuation is idempotent");
        (await BindAsync(nextNode(), admission, SessionB)).Should().BeFalse("a bound token is never moved to another continuation");

        (await RegisterAsync(TokenReplayScope.ContinuationSurface(markerA))).Result.Should().Be(TokenReplayRegistrationResult.Continued);
        (await RegisterAsync(TokenReplayScope.ContinuationSurface(markerA))).Result.Should().Be(TokenReplayRegistrationResult.Continued);
        (await RegisterAsync(TokenReplayScope.ContinuationSurface(markerB))).Result.Should().Be(TokenReplayRegistrationResult.ReplayDetected,
            "another continuation cannot reuse the bound token");
        (await RegisterAsync(TokenReplayScope.ContinuationSurface(null))).Result.Should().Be(TokenReplayRegistrationResult.ReplayDetected,
            "a request presenting no continuation cannot reuse the bound token");
        (await RegisterAsync(TokenReplayScope.HttpApi)).Result.Should().Be(TokenReplayRegistrationResult.ReplayDetected,
            "a token bound to a continuation is a replay on the HTTP API");

        var unregistered = new TokenReplayAdmissionFeature($"jti:test:{Guid.NewGuid():N}");
        (await BindAsync(nextNode(), unregistered, SessionA)).Should().BeFalse("only a registered token can be bound");
    }

    public static async Task AssertHttpApiReuseAsync(Func<ITokenReplayStore> nextNode)
    {
        var tokenKey = $"jti:test:{Guid.NewGuid():N}";
        var markerA = BuildTokenReplayContinuationMarker(SessionA);
        Task<TokenReplayRegistration> RegisterAsync(TokenReplayScope scope) => Register(nextNode(), tokenKey, scope);

        (await RegisterAsync(TokenReplayScope.HttpApi)).Result.Should().Be(TokenReplayRegistrationResult.Registered);
        (await RegisterAsync(TokenReplayScope.HttpApi)).Result.Should().Be(TokenReplayRegistrationResult.Reused,
            "an access token stays reusable on the HTTP API within its lifetime");
        (await RegisterAsync(TokenReplayScope.ContinuationSurface(null))).Result.Should().Be(TokenReplayRegistrationResult.ReplayDetected,
            "a token admitted on the HTTP API cannot open a continuation");
        (await RegisterAsync(TokenReplayScope.ContinuationSurface(markerA))).Result.Should().Be(TokenReplayRegistrationResult.ReplayDetected,
            "a token admitted on the HTTP API cannot continue a session");

        var admission = new TokenReplayAdmissionFeature(tokenKey, TokenReplayScope.ContinuationSurface(null));
        (await BindAsync(nextNode(), admission, SessionA)).Should().BeFalse("a token admitted on the HTTP API is never bound to a continuation");
        (await RegisterAsync(TokenReplayScope.HttpApi)).Result.Should().Be(TokenReplayRegistrationResult.Reused,
            "a refused bind leaves the HTTP API registration intact");
    }

    private static Task<TokenReplayRegistration> Register(ITokenReplayStore replays, string tokenKey, TokenReplayScope scope)
        => TryRegisterTokenReplayAsync(
            new TokenReplayPresentation(tokenKey, DateTime.UtcNow.AddMinutes(10), scope),
            replays, failClosed: true, NullLogger.Instance, CancellationToken.None);

    private static Task<bool> BindAsync(ITokenReplayStore replays, TokenReplayAdmissionFeature admission, string continuationId)
    {
        var context = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection()
                .AddSingleton(replays)
                .AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>), typeof(NullLogger<>))
                .BuildServiceProvider()
        };
        context.Features.Set(admission);
        return TryBindTokenReplayContinuationAsync(context, continuationId);
    }
}
