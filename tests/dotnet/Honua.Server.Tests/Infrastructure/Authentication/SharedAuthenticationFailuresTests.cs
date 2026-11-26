// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Infrastructure.Authentication;
using Honua.TestKit.Attributes;
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

    private readonly TestClock _clock = new();
    private readonly IAuthenticationFailureLedger _ledger = Substitute.For<IAuthenticationFailureLedger>();
    private readonly IOptions<AuthenticationFailureLimiterOptions> _options = Options.Create(new AuthenticationFailureLimiterOptions
    {
        Threshold = 3,
        Window = TimeSpan.FromMinutes(10),
        BlockDuration = TimeSpan.FromMinutes(5),
    });

    [UnitTest]
    public async Task PollAsync_RemoteRefusalForPreviouslyUnseenSource_RefusesTheSource()
    {
        var shared = new SharedAuthenticationFailures(_ledger, _options, _clock);
        var remaining = TimeSpan.FromMinutes(4);
        _ledger.ReadRefusalsAsync(Arg.Any<CancellationToken>())
            .Returns(new[] { KeyValuePair.Create(SharedAuthenticationFailures.Digest(Source), new LedgerFailureWindow(3, remaining, new(_clock.GetTimestamp(), remaining))) });
        var limiter = CreateLimiter(shared);

        using var sync = CreateSync(shared);
        await sync.PollAsync(CancellationToken.None);

        limiter.RefusalFor(Source).Should().Be(TimeSpan.FromMinutes(4));
        limiter.TryBeginAttempt(new(Source, 1)).Should().Be(AuthenticationAttemptAdmission.Refuse(TimeSpan.FromMinutes(4)));
        limiter.RefusalFor("198.51.100.8").Should().BeNull();
    }

    [UnitTest]
    public void EndAttempt_SharedRefusalArrivesDuringCredentialCheck_PreservesTheSharedDeadline()
    {
        var shared = new SharedAuthenticationFailures(_ledger, _options, _clock);
        var limiter = CreateLimiter(shared);
        for (var credential = 1; credential < _options.Value.Threshold; credential++)
        {
            var attempt = new AuthenticationAttemptKey(Source, credential);
            limiter.TryBeginAttempt(attempt).Admitted.Should().BeTrue();
            limiter.EndAttempt(attempt, AuthenticationAttemptOutcome.Failed);
            shared.Recorded.TryRead(out _).Should().BeTrue();
            shared.Acknowledge(Source, null, shared.Now);
        }

        var pending = new AuthenticationAttemptKey(Source, _options.Value.Threshold);
        limiter.TryBeginAttempt(pending).Admitted.Should().BeTrue();
        var duration = TimeSpan.FromMinutes(1);
        var now = shared.Now;
        var deadline = now + duration;
        shared.Apply(SharedAuthenticationFailures.Digest(Source), new(_options.Value.Threshold, deadline, deadline), now);

        limiter.EndAttempt(pending, AuthenticationAttemptOutcome.Failed);

        limiter.RefusalFor(Source).Should().Be(duration);
        shared.Recorded.TryRead(out _).Should().BeFalse();
        _clock.Elapsed += duration;
        limiter.TryBeginAttempt(pending).Admitted.Should().BeTrue();
    }

    [UnitTest]
    public async Task PollAsync_DelayedLedgerResponse_SubtractsTimeSinceTheSample()
    {
        var shared = new SharedAuthenticationFailures(_ledger, _options, _clock);
        var remaining = TimeSpan.FromMinutes(2);
        _ledger.ReadRefusalsAsync(Arg.Any<CancellationToken>()).Returns(_ =>
        {
            _clock.Elapsed += TimeSpan.FromMinutes(3);
            var window = new LedgerFailureWindow(3, remaining, new(_clock.GetTimestamp(), remaining));
            _clock.Elapsed += TimeSpan.FromMinutes(1);
            return Task.FromResult<IReadOnlyList<KeyValuePair<byte[], LedgerFailureWindow>>>(
                [KeyValuePair.Create(SharedAuthenticationFailures.Digest(Source), window)]);
        });
        using var sync = CreateSync(shared);

        await sync.PollAsync(CancellationToken.None);

        CreateLimiter(shared).RefusalFor(Source).Should().Be(TimeSpan.FromMinutes(1));
    }

    [UnitTest]
    public async Task RecordAsync_RefusalExpiresBeforeTheResultArrives_DiscardsTheRefusal()
    {
        var shared = new SharedAuthenticationFailures(_ledger, _options, _clock);
        var remaining = TimeSpan.FromMinutes(2);
        _ledger.RecordFailureAsync(Arg.Any<AuthenticationFailure>(), Arg.Any<CancellationToken>()).Returns(_ =>
        {
            _clock.Elapsed += TimeSpan.FromMinutes(3);
            var window = new LedgerFailureWindow(3, remaining, new(_clock.GetTimestamp(), remaining));
            _clock.Elapsed += remaining;
            return Task.FromResult<LedgerFailureWindow?>(window);
        });
        shared.Record(Source, shared.Now).Should().BeTrue();
        shared.Recorded.TryRead(out var pending).Should().BeTrue();
        using var sync = CreateSync(shared);

        await sync.RecordAsync(pending, CancellationToken.None);

        CreateLimiter(shared).RefusalFor(Source).Should().BeNull();
    }

    [UnitTest]
    public async Task ForAdmission_PendingFailureExpiresDuringWrite_CountsOnlyUnexpiredFailures()
    {
        var shared = new SharedAuthenticationFailures(_ledger, _options, _clock);
        using var sync = CreateSync(shared);
        var persisted = new TaskCompletionSource<LedgerFailureWindow?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ledger.RecordFailureAsync(Arg.Any<AuthenticationFailure>(), Arg.Any<CancellationToken>()).Returns(persisted.Task);
        shared.Record(Source, shared.Now).Should().BeTrue();
        shared.Recorded.TryRead(out var pending).Should().BeTrue();
        var writing = sync.RecordAsync(pending, CancellationToken.None);

        try
        {
            _clock.Elapsed += _options.Value.Window;
            shared.Record(Source, shared.Now).Should().BeTrue();
            shared.ForAdmission(Source, shared.Now).PendingFailures.Should().Be(1);
        }
        finally
        {
            persisted.SetResult(null);
            await writing;
        }

        shared.ForAdmission(Source, shared.Now).PendingFailures.Should().Be(1);
    }

    [UnitTest]
    public async Task TryBeginAttempt_SharedFailuresLeaveOneSlot_AdmitsOnlyOneInFlightAttempt()
    {
        var shared = new SharedAuthenticationFailures(_ledger, _options, _clock);
        _ledger.RecordFailureAsync(Arg.Any<AuthenticationFailure>(), Arg.Any<CancellationToken>())
            .Returns((LedgerFailureWindow?)new LedgerFailureWindow(2, null, new(_clock.GetTimestamp(), TimeSpan.FromMinutes(10))));
        var limiter = CreateLimiter(shared);
        var first = new AuthenticationAttemptKey(Source, 1);

        limiter.TryBeginAttempt(first).Admitted.Should().BeTrue();
        limiter.EndAttempt(first, AuthenticationAttemptOutcome.Failed);
        shared.Recorded.TryRead(out var recorded).Should().BeTrue();
        using var sync = CreateSync(shared);
        await sync.RecordAsync(recorded, CancellationToken.None);

        limiter.TryBeginAttempt(new(Source, 2)).Admitted.Should().BeTrue("two failures in the deployment, one slot left");
        limiter.TryBeginAttempt(new(Source, 3)).Admitted.Should().BeFalse("the in-flight attempt holds the last slot");
    }

    [UnitTest]
    public async Task RefusalFor_LedgerUnavailableAndLocalThresholdReached_ReportsLocalRefusal()
    {
        var shared = new SharedAuthenticationFailures(_ledger, _options, _clock);
        _ledger.RecordFailureAsync(Arg.Any<AuthenticationFailure>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("database unavailable"));
        _ledger.ReadRefusalsAsync(Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("database unavailable"));
        var limiter = CreateLimiter(shared);
        using var sync = CreateSync(shared);

        for (var credential = 0; credential < 3; credential++)
        {
            var key = new AuthenticationAttemptKey(Source, credential);
            limiter.TryBeginAttempt(key).Admitted.Should().BeTrue();
            limiter.EndAttempt(key, AuthenticationAttemptOutcome.Failed);
            shared.Recorded.TryRead(out var recorded).Should().BeTrue();
            await sync.RecordAsync(recorded, CancellationToken.None);
        }

        await sync.PollAsync(CancellationToken.None);
        limiter.RefusalFor(Source).Should().Be(TimeSpan.FromMinutes(5));
    }

    [UnitTest]
    public void Apply_OlderSnapshotArrivesAfterCurrentRefusal_PreservesCurrentRefusal()
    {
        var shared = new SharedAuthenticationFailures(_ledger, _options, _clock);
        var now = _clock.GetUtcNow();
        var digest = SharedAuthenticationFailures.Digest(Source);
        var current = new SharedFailureWindow(3, now.AddMinutes(4), now.AddMinutes(4));
        var earlier = new SharedFailureWindow(3, now.AddMinutes(1), now.AddMinutes(1));

        shared.Apply(digest, current, now);
        shared.Apply(digest, earlier, now);

        shared.For(Source, now).Should().Be(current);
    }

    [UnitTest]
    public void Validate_PollIntervalAboveOneMinute_RejectsTheInterval()
    {
        var options = new AuthenticationFailureLimiterOptions { SharedPollInterval = TimeSpan.FromMinutes(2) };

        new AuthenticationFailureLimiterOptionsValidator().Validate(null, options).Failures
            .Should().ContainSingle(failure => failure.Contains("SharedPollInterval", StringComparison.Ordinal));
    }

    [UnitTest]
    public async Task StopAsync_QueuedFailureWriteStillPending_DrainsTheQueueAndStopsAcceptingFailures()
    {
        var shared = new SharedAuthenticationFailures(_ledger, _options, _clock);
        var recordedAt = _clock.GetUtcNow().AddSeconds(-10);
        shared.Record(Source, recordedAt).Should().BeTrue();

        using var sync = CreateSync(shared);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var persisted = new TaskCompletionSource<LedgerFailureWindow?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ledger.RecordFailureAsync(Arg.Any<AuthenticationFailure>(), Arg.Any<CancellationToken>()).Returns(_ =>
        {
            started.TrySetResult();
            return persisted.Task;
        });

        await sync.StartAsync(deadline.Token);
        await started.Task.WaitAsync(deadline.Token);
        var stopping = sync.StopAsync(deadline.Token);
        stopping.IsCompleted.Should().BeFalse();
        persisted.SetResult(null);
        await stopping;

        await _ledger.Received(1).RecordFailureAsync(
            Arg.Is<AuthenticationFailure>(failure => failure.SourceDigest.SequenceEqual(SharedAuthenticationFailures.Digest(Source))),
            Arg.Any<CancellationToken>());
        shared.Record(Source, recordedAt).Should().BeFalse();
    }

    [UnitTest]
    public async Task PollAsync_LedgerCancelsFirstRead_AppliesRefusalOnNextPoll()
    {
        var shared = new SharedAuthenticationFailures(_ledger, _options, _clock);
        var now = _clock.GetUtcNow();
        var refusal = KeyValuePair.Create(
            SharedAuthenticationFailures.Digest(Source), new LedgerFailureWindow(3, TimeSpan.FromMinutes(2), new(_clock.GetTimestamp(), TimeSpan.FromMinutes(2))));
        _ledger.ReadRefusalsAsync(Arg.Any<CancellationToken>())
            .Returns(
                _ => Task.FromException<IReadOnlyList<KeyValuePair<byte[], LedgerFailureWindow>>>(new OperationCanceledException()),
                _ => Task.FromResult<IReadOnlyList<KeyValuePair<byte[], LedgerFailureWindow>>>([refusal]));

        using var sync = CreateSync(shared);
        await sync.PollAsync(CancellationToken.None);
        await sync.PollAsync(CancellationToken.None);

        shared.For(Source, now).Should().Be(refusal.Value.At(now));
    }

    [UnitTest]
    public void Validate_PollIntervalBelowOneMillisecond_RejectsTheInterval()
    {
        var options = new AuthenticationFailureLimiterOptions { SharedPollInterval = TimeSpan.FromTicks(1) };

        new AuthenticationFailureLimiterOptionsValidator().Validate(null, options).Failures
            .Should().ContainSingle(failure => failure.Contains("SharedPollInterval", StringComparison.Ordinal));
    }

    [UnitTest]
    public async Task TryBeginAttempt_SharedRefusalExpiresByElapsedTime_AdmitsWithoutEarlierFailures()
    {
        var shared = new SharedAuthenticationFailures(_ledger, _options, _clock);
        var duration = TimeSpan.FromMinutes(1);
        var refusal = KeyValuePair.Create(
            SharedAuthenticationFailures.Digest(Source), new LedgerFailureWindow(3, duration, new(_clock.GetTimestamp(), duration)));
        _ledger.ReadRefusalsAsync(Arg.Any<CancellationToken>()).Returns(new[] { refusal });
        var limiter = CreateLimiter(shared);
        using var sync = CreateSync(shared);

        for (var credential = 0; credential < 2; credential++)
        {
            var local = new AuthenticationAttemptKey(Source, credential);
            limiter.TryBeginAttempt(local).Admitted.Should().BeTrue();
            limiter.EndAttempt(local, AuthenticationAttemptOutcome.Failed);
            shared.Acknowledge(Source, null, shared.Now);
        }

        _clock.UtcNow += TimeSpan.FromHours(1);
        await sync.PollAsync(CancellationToken.None);
        _clock.UtcNow -= TimeSpan.FromHours(2);
        limiter.RefusalFor(Source).Should().Be(duration);

        _clock.Elapsed = duration;
        var next = new AuthenticationAttemptKey(Source, 3);
        limiter.TryBeginAttempt(next).Admitted.Should().BeTrue();
        limiter.EndAttempt(next, AuthenticationAttemptOutcome.Failed);
        limiter.RefusalFor(Source).Should().BeNull();
    }

    [UnitTest]
    public void Apply_AllSlotsActivelyRefused_DoesNotGrowBeyondMaxTrackedSources()
    {
        var boundedOptions = Options.Create(new AuthenticationFailureLimiterOptions
        {
            Threshold = 3,
            Window = TimeSpan.FromMinutes(10),
            BlockDuration = TimeSpan.FromMinutes(5),
            MaxTrackedSources = 2,
        });
        var shared = new SharedAuthenticationFailures(_ledger, boundedOptions, _clock);
        var now = _clock.GetUtcNow();
        var activeRefusal = new SharedFailureWindow(3, now.AddMinutes(5), now.AddMinutes(5));

        shared.Apply(SharedAuthenticationFailures.Digest("198.51.100.1"), activeRefusal, now);
        shared.Apply(SharedAuthenticationFailures.Digest("198.51.100.2"), activeRefusal, now);
        shared.Apply(SharedAuthenticationFailures.Digest("198.51.100.3"), activeRefusal, now);

        shared.For("198.51.100.1", now).Should().Be(activeRefusal);
        shared.For("198.51.100.2", now).Should().Be(activeRefusal);
        shared.For("198.51.100.3", now).Should().BeNull(
            "when every tracked slot is actively refused, new entries must not grow _windows past MaxTrackedSources");
    }

    private sealed class TestClock : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = DateTimeOffset.UtcNow;
        public TimeSpan Elapsed { get; set; }
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Elapsed.Ticks;
        public override DateTimeOffset GetUtcNow() => UtcNow;
    }

    private AuthenticationFailureLimiter CreateLimiter(SharedAuthenticationFailures shared)
        => new(_options, _clock, NullLogger<AuthenticationFailureLimiter>.Instance) { Shared = shared };

    private SharedAuthenticationFailureSync CreateSync(SharedAuthenticationFailures shared)
        => new(shared, _clock, NullLogger<SharedAuthenticationFailureSync>.Instance);
}
