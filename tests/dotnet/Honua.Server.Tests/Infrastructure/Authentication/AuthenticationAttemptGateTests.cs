// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using FluentAssertions;
using Honua.Infrastructure.Authentication;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Honua.Server.Tests.Infrastructure.Authentication;

[SecurityTest]
[Protocol(TestProtocols.Admin)]
[Operation(Operations.Security)]
public sealed class AuthenticationAttemptGateTests
{
    private readonly IAuthenticationFailureLimiter _limiter = Substitute.For<IAuthenticationFailureLimiter>();

    public AuthenticationAttemptGateTests()
    {
        _limiter.TryBeginAttempt(Arg.Any<AuthenticationAttemptKey>()).Returns(AuthenticationAttemptAdmission.Admit);
    }

    [UnitTest]
    public void Begin_FailedAttempt_IsCountedAgainstThePartitionedAddress()
    {
        var gate = CreateGate();

        using (var attempt = gate.Begin(IPAddress.Parse("::ffff:198.51.100.7"), "guess"))
        {
            attempt.CountFailure();
        }

        _limiter.Received(1).EndAttempt(
            Arg.Is<AuthenticationAttemptKey>(key => key.Source == "198.51.100.7"), AuthenticationAttemptOutcome.Failed);
    }

    [UnitTheory]
    [InlineData("10.1.2.3")]
    [InlineData("::ffff:10.1.2.3")]
    public void Begin_ExemptNetwork_IsNeitherCountedNorRefused(string address)
    {
        var gate = CreateGate(options => options.ExemptNetworks = ["10.0.0.0/8"]);

        using (var attempt = gate.Begin(IPAddress.Parse(address), "guess"))
        {
            attempt.RetryAfter.Should().BeNull();
            attempt.CountFailure();
        }

        _limiter.ReceivedCalls().Should().BeEmpty();
    }

    [UnitTest]
    public void Begin_WhenDisabled_IsNeitherCountedNorRefused()
    {
        var gate = CreateGate(options => options.Enabled = false);

        using (var attempt = gate.Begin(IPAddress.Parse("198.51.100.7"), "guess"))
        {
            attempt.CountFailure();
        }

        _limiter.ReceivedCalls().Should().BeEmpty();
    }

    [UnitTest]
    public void Begin_WithoutClientAddress_WarnsOnceAndDoesNotCount()
    {
        var logger = Substitute.For<ILogger<AuthenticationAttemptGate>>();
        logger.IsEnabled(Arg.Any<LogLevel>()).Returns(true);
        var gate = CreateGate(logger: logger);

        for (var attempt = 0; attempt < 3; attempt++)
        {
            using var untracked = gate.Begin(null, "guess");
            untracked.CountFailure();
        }

        _limiter.ReceivedCalls().Should().BeEmpty();
        logger.ReceivedCalls()
            .Where(call => call.GetMethodInfo().Name == nameof(ILogger.Log) && call.GetArguments()[1] is EventId { Id: 4121 })
            .Should().ContainSingle();
    }

    [UnitTest]
    public void Begin_RefusedSource_ReportsRetryAfterWithoutEndingAnAttempt()
    {
        _limiter.TryBeginAttempt(Arg.Any<AuthenticationAttemptKey>())
            .Returns(AuthenticationAttemptAdmission.Refuse(TimeSpan.FromMinutes(3)));
        var gate = CreateGate();

        using (var attempt = gate.Begin(IPAddress.Parse("198.51.100.7"), "guess"))
        {
            attempt.RetryAfter.Should().Be(TimeSpan.FromMinutes(3));
        }

        _limiter.DidNotReceive().EndAttempt(Arg.Any<AuthenticationAttemptKey>(), Arg.Any<AuthenticationAttemptOutcome>());
    }

    private AuthenticationAttemptGate CreateGate(
        Action<AuthenticationFailureLimiterOptions>? configure = null,
        ILogger<AuthenticationAttemptGate>? logger = null)
    {
        var options = new AuthenticationFailureLimiterOptions();
        configure?.Invoke(options);
        return new AuthenticationAttemptGate(_limiter, Options.Create(options), logger ?? NullLogger<AuthenticationAttemptGate>.Instance);
    }
}
