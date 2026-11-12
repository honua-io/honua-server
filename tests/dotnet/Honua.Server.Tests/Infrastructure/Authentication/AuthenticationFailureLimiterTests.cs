// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Infrastructure.Authentication;
using Honua.TestKit.Attributes;
using Honua.TestKit.Helpers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Honua.Server.Tests.Infrastructure.Authentication;

[SecurityTest]
[Protocol(TestProtocols.Admin)]
[Operation(Operations.Security)]
public sealed class AuthenticationFailureLimiterTests
{
    private const string Source = "198.51.100.7";
    private const string OtherSource = "198.51.100.8";

    private readonly TransitionClock _clock = new();

    [UnitTest]
    public void TryBeginAttempt_BelowThreshold_Admits()
    {
        var limiter = CreateLimiter();

        Fail(limiter, Source, 2);

        limiter.TryBeginAttempt(Key(Source)).Admitted.Should().BeTrue();
    }

    [UnitTest]
    public void EndAttempt_FailuresReachThreshold_RefusesOnlyThatSourceForTheBlockDuration()
    {
        var limiter = CreateLimiter();

        Fail(limiter, Source, 3);

        limiter.TryBeginAttempt(Key(Source)).Should().Be(AuthenticationAttemptAdmission.Refuse(TimeSpan.FromMinutes(5)));
        limiter.TryBeginAttempt(Key(OtherSource)).Admitted.Should().BeTrue();
    }

    [UnitTest]
    public void TryBeginAttempt_AfterWindowElapses_StartsANewCount()
    {
        var limiter = CreateLimiter();
        Fail(limiter, Source, 2);

        _clock.Advance(TimeSpan.FromMinutes(10));
        Fail(limiter, Source, 2);

        limiter.TryBeginAttempt(Key(Source)).Admitted.Should().BeTrue();
    }

    [UnitTest]
    public void TryBeginAttempt_AfterBlockElapses_AdmitsWithAFreshCount()
    {
        var limiter = CreateLimiter();
        Fail(limiter, Source, 3);

        _clock.Advance(TimeSpan.FromMinutes(4));
        limiter.TryBeginAttempt(Key(Source)).RetryAfter.Should().Be(TimeSpan.FromMinutes(1));
        _clock.Advance(TimeSpan.FromMinutes(1));
        Fail(limiter, Source, 2);

        limiter.TryBeginAttempt(Key(Source)).Admitted.Should().BeTrue();
    }

    [UnitTest]
    public void EndAttempt_SuccessfulAttempt_DoesNotClearEarlierFailures()
    {
        var limiter = CreateLimiter();
        Fail(limiter, Source, 2);

        Attempt(limiter, Key(Source, credential: 7), AuthenticationAttemptOutcome.NotCounted);
        Fail(limiter, Source, 1);

        limiter.TryBeginAttempt(Key(Source)).Admitted.Should().BeFalse();
    }

    [UnitTest]
    public void TryBeginAttempt_ParallelDistinctCredentials_AdmitsOnlyUpToTheThreshold()
    {
        var limiter = CreateLimiter();
        Fail(limiter, Source, 1);

        var admitted = Enumerable.Range(10, 5)
            .Select(credential => limiter.TryBeginAttempt(Key(Source, credential)).Admitted)
            .ToArray();

        admitted.Should().Equal(true, true, false, false, false);
        limiter.TryBeginAttempt(Key(Source)).RetryAfter.Should().Be(TimeSpan.FromSeconds(1));
    }

    [UnitTest]
    public void TryBeginAttempt_SameCredentialInFlight_SharesOneSlot()
    {
        var limiter = CreateLimiter();
        Fail(limiter, Source, 2);

        var admitted = Enumerable.Range(0, 20).Select(_ => limiter.TryBeginAttempt(Key(Source, 42)).Admitted).ToArray();

        admitted.Should().AllSatisfy(value => value.Should().BeTrue());
        limiter.TryBeginAttempt(Key(Source, 43)).Admitted.Should().BeFalse();
    }

    [UnitTest]
    public void TryBeginAttempt_AtCapacity_EvictsAnExpiredSourceBeforeACountingOne()
    {
        var limiter = CreateLimiter(options => options.MaxTrackedSources = 2);
        Fail(limiter, "198.51.100.9", 1);
        _clock.Advance(TimeSpan.FromMinutes(6));
        Fail(limiter, OtherSource, 2);
        _clock.Advance(TimeSpan.FromMinutes(5));

        Fail(limiter, "198.51.100.10", 1);
        Fail(limiter, OtherSource, 1);

        limiter.TryBeginAttempt(Key(OtherSource)).Admitted.Should().BeFalse("only the expired source was evicted");
    }

    [UnitTest]
    public void TryBeginAttempt_AtCapacity_EvictsTheOldestUnrefusedSource()
    {
        var limiter = CreateLimiter(options => options.MaxTrackedSources = 2);
        Fail(limiter, Source, 3);
        _clock.Advance(TimeSpan.FromMinutes(1));
        Fail(limiter, OtherSource, 2);

        Fail(limiter, "198.51.100.10", 1);
        Fail(limiter, OtherSource, 1);

        limiter.TryBeginAttempt(Key(Source)).Admitted.Should().BeFalse("a refused source is the last to be evicted");
        limiter.TryBeginAttempt(Key(OtherSource)).Admitted.Should().BeTrue("the oldest unrefused source was evicted");
    }

    [UnitTest]
    public void EndAttempt_WhenSourceBecomesRefused_LogsOneWarningWithoutTheAddress()
    {
        var logger = Substitute.For<ILogger<AuthenticationFailureLimiter>>();
        logger.IsEnabled(Arg.Any<LogLevel>()).Returns(true);
        var limiter = CreateLimiter(logger: logger);

        Fail(limiter, Source, 3);

        var warning = logger.ReceivedCalls()
            .Where(call => call.GetMethodInfo().Name == nameof(ILogger.Log))
            .Select(call => call.GetArguments())
            .Where(arguments => arguments[1] is EventId { Id: 4120 })
            .Should().ContainSingle().Subject;
        warning[0].Should().Be(LogLevel.Warning);
        warning[2]!.ToString().Should().NotContain(Source);
    }

    [UnitTheory]
    [InlineData(0, 15, 15, null)]
    [InlineData(10, 0, 15, null)]
    [InlineData(10, 15, 2000, null)]
    [InlineData(10, 15, 15, "not-a-network")]
    public void Validate_OutOfRangeSettings_AreRejected(int threshold, int windowMinutes, int blockMinutes, string? exemptNetwork)
    {
        var options = new AuthenticationFailureLimiterOptions
        {
            Threshold = threshold,
            Window = TimeSpan.FromMinutes(windowMinutes),
            BlockDuration = TimeSpan.FromMinutes(blockMinutes),
            ExemptNetworks = exemptNetwork is null ? [] : [exemptNetwork],
        };

        new AuthenticationFailureLimiterOptionsValidator().Validate(null, options).Failed.Should().BeTrue();
    }

    [UnitTest]
    public void Validate_Defaults_AreAccepted()
    {
        var options = new AuthenticationFailureLimiterOptions { ExemptNetworks = ["10.0.0.0/8", "2001:db8::/32"] };

        new AuthenticationFailureLimiterOptionsValidator().Validate(null, options).Succeeded.Should().BeTrue();
    }

    private AuthenticationFailureLimiter CreateLimiter(
        Action<AuthenticationFailureLimiterOptions>? configure = null,
        ILogger<AuthenticationFailureLimiter>? logger = null)
    {
        var options = new AuthenticationFailureLimiterOptions
        {
            Threshold = 3,
            Window = TimeSpan.FromMinutes(10),
            BlockDuration = TimeSpan.FromMinutes(5),
        };
        configure?.Invoke(options);
        return new AuthenticationFailureLimiter(
            Options.Create(options), _clock, logger ?? NullLogger<AuthenticationFailureLimiter>.Instance);
    }

    private static AuthenticationAttemptKey Key(string source, int credential = 1) => new(source, credential);

    private static void Fail(AuthenticationFailureLimiter limiter, string source, int times)
    {
        for (var attempt = 0; attempt < times; attempt++)
        {
            Attempt(limiter, Key(source, credential: 1000 + attempt), AuthenticationAttemptOutcome.Failed);
        }
    }

    private static void Attempt(AuthenticationFailureLimiter limiter, AuthenticationAttemptKey key, AuthenticationAttemptOutcome outcome)
    {
        if (limiter.TryBeginAttempt(key).Admitted)
        {
            limiter.EndAttempt(key, outcome);
        }
    }
}
