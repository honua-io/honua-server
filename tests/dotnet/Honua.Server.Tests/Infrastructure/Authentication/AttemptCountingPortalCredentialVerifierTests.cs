// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using FluentAssertions;
using Honua.Core.Features.Authorization.Abstractions;
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
    public async Task VerifyAsync_CorrectPasswordBeforeThreshold_IsAccepted()
    {
        await _verifier.VerifyAsync("admin", "wrong", CancellationToken.None);

        (await _verifier.VerifyAsync("admin", AdminPassword, CancellationToken.None)).Should().NotBeNull();
    }
}
