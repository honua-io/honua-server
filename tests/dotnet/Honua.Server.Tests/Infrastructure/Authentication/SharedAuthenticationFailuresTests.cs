// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Infrastructure.Authentication;
using Honua.TestKit.Attributes;
using Honua.TestKit.Helpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Honua.Server.Tests.Infrastructure.Authentication;

[SecurityTest]
[Protocol(TestProtocols.Admin)]
[Operation(Operations.Security)]
public sealed class SharedAuthenticationFailuresTests
{
    private const string Source = "198.51.100.7";

    private readonly TransitionClock _clock = new();
    private readonly IAuthenticationFailureLedger _ledger = Substitute.For<IAuthenticationFailureLedger>();
    private readonly IOptions<AuthenticationFailureLimiterOptions> _options = Options.Create(new AuthenticationFailureLimiterOptions
    {
        Threshold = 3,
        Window = TimeSpan.FromMinutes(10),
        BlockDuration = TimeSpan.FromMinutes(5),
    });

    [UnitTest]
    public async Task PolledRefusal_RefusesASourceThisInstanceNeverSawFail()
    {
        var shared = new SharedAuthenticationFailures(_ledger, _options);
        var refusedUntil = _clock.GetUtcNow().AddMinutes(4);
        _ledger.ReadRefusalsAsync(Arg.Any<DateTimeOffset>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new[] { KeyValuePair.Create(SharedAuthenticationFailures.Digest(Source), new SharedFailureWindow(3, refusedUntil, refusedUntil)) });
        var limiter = CreateLimiter(shared);

        using var sync = CreateSync(shared);
        await sync.PollAsync(CancellationToken.None);

        limiter.RefusalFor(Source).Should().Be(TimeSpan.FromMinutes(4));
        limiter.TryBeginAttempt(new(Source, 1)).Should().Be(AuthenticationAttemptAdmission.Refuse(TimeSpan.FromMinutes(4)));
        limiter.RefusalFor("198.51.100.8").Should().BeNull();
    }

    [UnitTest]
    public async Task SharedTotal_CountsTowardTheThresholdOnThisInstance()
    {
        var shared = new SharedAuthenticationFailures(_ledger, _options);
        var expires = _clock.GetUtcNow().AddMinutes(10);
        _ledger.RecordFailureAsync(Arg.Any<AuthenticationFailure>(), Arg.Any<CancellationToken>())
            .Returns(new SharedFailureWindow(2, null, expires));
        var limiter = CreateLimiter(shared);
        var first = new AuthenticationAttemptKey(Source, 1);

        limiter.TryBeginAttempt(first).Admitted.Should().BeTrue();
        limiter.EndAttempt(first, AuthenticationAttemptOutcome.Failed);
        shared.Recorded.TryRead(out var recorded).Should().BeTrue();
        using var sync = CreateSync(shared);
        await sync.RecordAsync(recorded!, CancellationToken.None);

        limiter.TryBeginAttempt(new(Source, 2)).Admitted.Should().BeTrue("two failures in the deployment, one slot left");
        limiter.TryBeginAttempt(new(Source, 3)).Admitted.Should().BeFalse("the in-flight attempt holds the last slot");
    }

    [UnitTest]
    public async Task UnreachableLedger_LeavesThePerInstanceLimiterCounting()
    {
        var shared = new SharedAuthenticationFailures(_ledger, _options);
        _ledger.RecordFailureAsync(Arg.Any<AuthenticationFailure>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("database unavailable"));
        _ledger.ReadRefusalsAsync(Arg.Any<DateTimeOffset>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("database unavailable"));
        var limiter = CreateLimiter(shared);
        using var sync = CreateSync(shared);

        for (var credential = 0; credential < 3; credential++)
        {
            var key = new AuthenticationAttemptKey(Source, credential);
            limiter.TryBeginAttempt(key).Admitted.Should().BeTrue();
            limiter.EndAttempt(key, AuthenticationAttemptOutcome.Failed);
            shared.Recorded.TryRead(out var recorded).Should().BeTrue();
            await sync.RecordAsync(recorded!, CancellationToken.None);
        }

        await sync.PollAsync(CancellationToken.None);
        limiter.RefusalFor(Source).Should().Be(TimeSpan.FromMinutes(5));
    }

    [UnitTest]
    public void OutOfOrderSnapshots_PreserveTheCurrentRefusal()
    {
        var shared = new SharedAuthenticationFailures(_ledger, _options);
        var now = _clock.GetUtcNow();
        var digest = SharedAuthenticationFailures.Digest(Source);
        var current = new SharedFailureWindow(3, now.AddMinutes(4), now.AddMinutes(4));
        var earlier = new SharedFailureWindow(3, now.AddMinutes(1), now.AddMinutes(1));

        shared.Apply(digest, current, now);
        shared.Apply(digest, earlier, now);

        shared.For(Source, now).Should().Be(current);
    }

    [UnitTest]
    public void Validator_RefusesAPollIntervalAboveOneMinute()
    {
        var options = new AuthenticationFailureLimiterOptions { SharedPollInterval = TimeSpan.FromMinutes(2) };

        new AuthenticationFailureLimiterOptionsValidator().Validate(null, options).Failures
            .Should().ContainSingle(failure => failure.Contains("SharedPollInterval", StringComparison.Ordinal));
    }

    private AuthenticationFailureLimiter CreateLimiter(SharedAuthenticationFailures shared)
        => new(_options, _clock, NullLogger<AuthenticationFailureLimiter>.Instance) { Shared = shared };

    private SharedAuthenticationFailureSync CreateSync(SharedAuthenticationFailures shared)
        => new(shared, _clock, NullLogger<SharedAuthenticationFailureSync>.Instance);
}
