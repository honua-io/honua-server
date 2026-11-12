// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Text;
using System.Text.Encodings.Web;
using FluentAssertions;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Infrastructure.Authentication;
using Honua.TestKit.Attributes;
using Honua.TestKit.Helpers;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Honua.Server.Tests.Infrastructure.Authentication;

[SecurityTest]
[Protocol(TestProtocols.Admin)]
[Operation(Operations.Security)]
public sealed class ApiKeyAuthenticationFailureLimitTests
{
    private const string AdminPassword = "failure-limit-admin-password";
    private const string ScopedKey = "hnua_scoped-read-key";
    private const int Threshold = 3;
    private static readonly IPAddress Client = IPAddress.Parse("203.0.113.20");

    private readonly TransitionClock _clock = new();
    private readonly IAdminApiKeyStore _store = Substitute.For<IAdminApiKeyStore>();
    private readonly AuthenticationAttemptGate _gate;

    public ApiKeyAuthenticationFailureLimitTests()
    {
        var options = Options.Create(new AuthenticationFailureLimiterOptions { Threshold = Threshold });
        var limiter = new AuthenticationFailureLimiter(options, _clock, NullLogger<AuthenticationFailureLimiter>.Instance);
        _gate = new AuthenticationAttemptGate(limiter, options, NullLogger<AuthenticationAttemptGate>.Instance);
    }

    [UnitTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AuthenticateAsync_RepeatedInvalidKeys_RefusesSourceWithoutCheckingTheKey(bool basic)
    {
        await FailAsync(Threshold, basic);

        _store.ClearReceivedCalls();
        var (result, context, handler) = await AuthenticateAsync(AdminPassword, basic);
        await handler.ChallengeAsync(new AuthenticationProperties());

        result.Succeeded.Should().BeFalse();
        _store.ReceivedCalls().Should().BeEmpty();
        context.Response.StatusCode.Should().Be(StatusCodes.Status429TooManyRequests);
        context.Response.Headers.RetryAfter.ToString().Should().Be("900");
    }

    [UnitTest]
    public async Task AuthenticateAsync_ScopedKeySuccess_DoesNotResetTheCount()
    {
        _store.ValidateAsync(ScopedKey, Arg.Any<CancellationToken>()).Returns(ScopedKeyRecord());
        await FailAsync(Threshold - 1);
        (await AuthenticateAsync(ScopedKey)).Result.Succeeded.Should().BeTrue();

        await FailAsync(1);

        (await AuthenticateAsync(AdminPassword)).Result.Succeeded.Should().BeFalse();
    }

    [UnitTest]
    public async Task AuthenticateAsync_AfterRefusalElapses_AcceptsTheValidKey()
    {
        await FailAsync(Threshold);

        _clock.Advance(TimeSpan.FromMinutes(15));

        (await AuthenticateAsync(AdminPassword)).Result.Succeeded.Should().BeTrue();
    }

    [UnitTest]
    public async Task AuthenticateAsync_StoreDownPasswordMismatch_IsCounted()
    {
        _store.ValidateAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new TimeoutException("store timeout"));

        await FailAsync(Threshold);

        (await AuthenticateAsync(AdminPassword)).Result.Succeeded.Should().BeFalse("the break-glass password is refused too");
    }

    [UnitTest]
    public async Task AuthenticateAsync_AdminPasswordNotConfigured_IsNotCounted()
    {
        for (var attempt = 0; attempt < Threshold * 2; attempt++)
        {
            await AuthenticateAsync("wrong-key", basic: false, adminPassword: null);
        }

        using var probe = _gate.Begin(Client, "probe");
        probe.RetryAfter.Should().BeNull();
    }

    [UnitTheory]
    [InlineData("Authorization", "Bearer ")]
    [InlineData("X-Esri-Authorization", "Bearer ")]
    public async Task PortalTokenHandler_RepeatedBearerGuesses_AreCountedAndRefused(string header, string prefix)
    {
        for (var attempt = 0; attempt < Threshold; attempt++)
        {
            (await AuthenticatePortalAsync(context => context.Request.Headers[header] = prefix + "guess-" + attempt)).Succeeded.Should().BeFalse();
        }

        _store.ClearReceivedCalls();
        var refused = await AuthenticatePortalAsync(context => context.Request.Headers[header] = prefix + AdminPassword);

        refused.Succeeded.Should().BeFalse();
        _store.ReceivedCalls().Should().BeEmpty();
    }

    [UnitTest]
    public async Task PortalTokenHandler_QueryTokenWhileRefused_DoesNotRevealAnAdministrativeKey()
    {
        await FailAsync(Threshold);

        var refused = await AuthenticatePortalAsync(context => context.Request.QueryString = new QueryString("?token=" + AdminPassword));

        refused.Succeeded.Should().BeFalse();
        refused.Failure!.Message.Should().NotContain("administrative authority");
    }

    [UnitTest]
    public async Task PortalTokenHandler_ExpiredIssuedToken_IsNotCounted()
    {
        var expiredPortalToken = new string('a', 64);
        for (var attempt = 0; attempt < Threshold * 2; attempt++)
        {
            await AuthenticatePortalAsync(context => context.Request.Headers.Authorization = "Bearer " + expiredPortalToken);
        }

        (await AuthenticateAsync(AdminPassword)).Result.Succeeded.Should().BeTrue();
    }

    private async Task FailAsync(int times, bool basic = false)
    {
        for (var attempt = 0; attempt < times; attempt++)
        {
            (await AuthenticateAsync("wrong-key-" + attempt, basic)).Result.Succeeded.Should().BeFalse();
        }
    }

    private async Task<(AuthenticateResult Result, HttpContext Context, ApiKeyAuthenticationHandler Handler)> AuthenticateAsync(
        string key, bool basic = false, string? adminPassword = AdminPassword)
    {
        var handler = new ApiKeyAuthenticationHandler(Schemes(), NullLoggerFactory.Instance, UrlEncoder.Default, Dependencies(adminPassword));
        var context = CreateContext();
        if (basic)
        {
            context.Request.Headers.Authorization = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("admin:" + key));
        }
        else
        {
            context.Request.Headers["X-API-Key"] = key;
        }

        await handler.InitializeAsync(new AuthenticationScheme("ApiKey", null, typeof(ApiKeyAuthenticationHandler)), context);
        return (await handler.AuthenticateAsync(), context, handler);
    }

    private async Task<AuthenticateResult> AuthenticatePortalAsync(Action<HttpContext> presentToken)
    {
        var handler = new PortalTokenAuthenticationHandler(
            Schemes(), NullLoggerFactory.Instance, UrlEncoder.Default, Substitute.For<IPortalTokenIssuer>());
        var context = CreateContext();
        presentToken(context);
        await handler.InitializeAsync(new AuthenticationScheme("PortalToken", null, typeof(PortalTokenAuthenticationHandler)), context);
        return await handler.AuthenticateAsync();
    }

    private ApiKeyAuthenticationDependencies Dependencies(string? adminPassword = AdminPassword) => new(
        Options.Create(new ApiKeyAuthenticationOptions
        {
            AdminPassword = adminPassword,
            EnvironmentName = "Test",
            EnableBasicAuthCompatibility = true,
        }),
        adminApiKeyStore: _store)
    { AttemptGate = _gate };

    private DefaultHttpContext CreateContext()
    {
        var context = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().AddLogging().AddSingleton(Dependencies()).BuildServiceProvider(),
        };
        context.Connection.RemoteIpAddress = Client;
        context.Request.IsHttps = true;
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static IOptionsMonitor<AuthenticationSchemeOptions> Schemes()
    {
        var schemes = Substitute.For<IOptionsMonitor<AuthenticationSchemeOptions>>();
        schemes.Get(Arg.Any<string>()).Returns(new AuthenticationSchemeOptions());
        return schemes;
    }

    private static AdminApiKeyValidationResult ScopedKeyRecord()
    {
        var now = DateTimeOffset.UtcNow;
        return new AdminApiKeyValidationResult(new AdminApiKeyRecord(
            Guid.NewGuid(), "scoped", "hnua_scoped", new byte[32], ["read:svcA/layer1"], now, now,
            ExpiresAt: null, LastUsedAt: null, RotatedAt: null, RevokedAt: null, CreatedBy: "test"));
    }
}
