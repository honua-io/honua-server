// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using FluentAssertions;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Security.Abstractions;
using Honua.Infrastructure.Authentication;
using Honua.TestKit.Attributes;
using Honua.TestKit.Helpers;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Honua.Server.Tests.Infrastructure.Authentication;

[SecurityTest]
[Protocol(TestProtocols.Admin)]
[Operation(Operations.Security)]
public sealed class AttemptCountingPortalCredentialVerifierTests
{
    private const string AdminPassword = "generate-token-admin-password";
    private const int Threshold = 3;

    private readonly IAdminApiKeyStore _store = Substitute.For<IAdminApiKeyStore>();
    private readonly IConnectionSecretResolver _secretResolver = Substitute.For<IConnectionSecretResolver>();
    private readonly AttemptCountingPortalCredentialVerifier _verifier;

    public AttemptCountingPortalCredentialVerifierTests()
    {
        var options = Options.Create(new AuthenticationFailureLimiterOptions { Threshold = Threshold });
        var limiter = new AuthenticationFailureLimiter(options, new TransitionClock(), NullLogger<AuthenticationFailureLimiter>.Instance);
        var gate = new AuthenticationAttemptGate(limiter, options, NullLogger<AuthenticationAttemptGate>.Instance);
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.30");
        var admin = new AdminPortalCredentialVerifier(
            Options.Create(new ApiKeyAuthenticationOptions { AdminPassword = AdminPassword, EnvironmentName = "Test" }),
            secretResolver: _secretResolver,
            adminApiKeyStore: _store,
            logger: NullLogger<AdminPortalCredentialVerifier>.Instance);
        _verifier = new AttemptCountingPortalCredentialVerifier(admin, gate, new HttpContextAccessor { HttpContext = context });
    }

    [UnitTest]
    public async Task VerifyAsync_RepeatedWrongPasswords_RefusesTheSourceWithoutComparing()
    {
        for (var attempt = 0; attempt < Threshold; attempt++)
        {
            (await _verifier.VerifyAsync("admin", "wrong-" + attempt, CancellationToken.None)).Should().BeNull();
        }

        _store.ClearReceivedCalls();
        var refused = await _verifier.VerifyAsync("admin", AdminPassword, CancellationToken.None);

        refused.Should().BeNull();
        _store.ReceivedCalls().Should().BeEmpty();
    }

    [UnitTest]
    public async Task VerifyAsync_RepeatedUnknownKeysUnderAnotherUsername_RefusesTheSource()
    {
        for (var attempt = 0; attempt < Threshold; attempt++)
        {
            (await _verifier.VerifyAsync("client", "unknown-key-" + attempt, CancellationToken.None)).Should().BeNull();
        }

        _store.ClearReceivedCalls();
        var refused = await _verifier.VerifyAsync("admin", AdminPassword, CancellationToken.None);

        refused.Should().BeNull();
        _store.ReceivedCalls().Should().BeEmpty();
    }

    [UnitTest]
    public async Task VerifyAsync_MoreDistinctValidKeysInFlightThanTheThreshold_AreAllAdmitted()
    {
        var release = new TaskCompletionSource<AdminApiKeyValidationResult?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _store.ValidateAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(_ => release.Task);
        var now = DateTimeOffset.UtcNow;
        var key = new AdminApiKeyValidationResult(new AdminApiKeyRecord(
            Guid.NewGuid(), "reader", "hnua_reader", new byte[32], ["admin:*"], now, now,
            ExpiresAt: null, LastUsedAt: null, RotatedAt: null, RevokedAt: null, CreatedBy: "test"));

        var inFlight = Enumerable.Range(0, Threshold + 8)
            .Select(index => _verifier.VerifyAsync("admin", "valid-key-" + index, CancellationToken.None)).ToArray();
        release.SetResult(key);
        var results = await Task.WhenAll(inFlight);

        results.Should().AllSatisfy(principal => principal.Should().NotBeNull());
    }

    [UnitTest]
    public async Task VerifyAsync_ConcurrentDistinctWrongPasswords_AdmitsOnlyTheThresholdToComparison()
    {
        var release = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _secretResolver.CanResolveSecretAsync(AdminPassword, Arg.Any<CancellationToken>()).Returns(true);
        _secretResolver.ResolveSecretFreshAsync(AdminPassword, Arg.Any<CancellationToken>()).Returns(_ => release.Task);
        var inFlight = Enumerable.Range(0, Threshold + 8)
            .Select(index => _verifier.VerifyAsync("admin", "wrong-" + index, CancellationToken.None)).ToArray();
        try
        {
            _secretResolver.ReceivedCalls().Count(call => call.GetMethodInfo().Name == nameof(IConnectionSecretResolver.ResolveSecretFreshAsync))
                .Should().Be(Threshold);
        }
        finally
        {
            release.SetResult(AdminPassword);
        }

        (await Task.WhenAll(inFlight)).Should().AllSatisfy(principal => principal.Should().BeNull());
        _store.ClearReceivedCalls();
        (await _verifier.VerifyAsync("admin", AdminPassword, CancellationToken.None)).Should().BeNull();
        _store.ReceivedCalls().Should().BeEmpty();
    }

    [UnitTest]
    public async Task VerifyAsync_StoreDownPasswordMismatch_IsCounted()
    {
        _store.ValidateAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).ThrowsAsync(new TimeoutException("store timeout"));

        for (var attempt = 0; attempt < Threshold; attempt++)
        {
            await _verifier.VerifyAsync("admin", "wrong-" + attempt, CancellationToken.None);
        }

        (await _verifier.VerifyAsync("admin", AdminPassword, CancellationToken.None)).Should().BeNull();
    }

    [UnitTest]
    public async Task VerifyAsync_SecretProviderUnavailable_DoesNotConsumeTheFailureBudget()
    {
        _secretResolver.CanResolveSecretAsync(AdminPassword, Arg.Any<CancellationToken>()).Returns(true);
        _secretResolver.ResolveSecretFreshAsync(AdminPassword, Arg.Any<CancellationToken>())
            .ThrowsAsync(new TimeoutException("secret provider unavailable"));

        for (var attempt = 0; attempt < Threshold + 1; attempt++)
        {
            (await _verifier.VerifyAsync("admin", AdminPassword, CancellationToken.None)).Should().BeNull();
        }

        _secretResolver.CanResolveSecretAsync(AdminPassword, Arg.Any<CancellationToken>()).Returns(false);
        (await _verifier.VerifyAsync("admin", AdminPassword, CancellationToken.None)).Should().NotBeNull();
    }

    [UnitTest]
    public async Task VerifyAsync_CorrectPasswordBeforeThreshold_IsAccepted()
    {
        await _verifier.VerifyAsync("admin", "wrong", CancellationToken.None);

        (await _verifier.VerifyAsync("admin", AdminPassword, CancellationToken.None)).Should().NotBeNull();
    }
}
