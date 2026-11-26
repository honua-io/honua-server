// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Infrastructure.Authentication;
using Honua.Server.Tests.Infrastructure;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Helpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Honua.Server.Tests.Features.Infrastructure.ControlPlane;

// Each node is one server instance: its own limiter, shared view and sync over the same tables.
[Protocol(TestProtocols.TestQuality)]
[Operation(Operations.TestInfrastructure)]
public sealed class PostgresAuthenticationFailureLedgerTests(DatabaseFixtureAdapter database) : IClassFixture<DatabaseFixtureAdapter>
{
    private const string Source = "198.51.100.7";

    [IntegrationTest]
    public async Task Ledger_AggregatesRecordedFailuresFromTwoInstances()
    {
        await using var harness = await CreateHarnessAsync();
        var taskA = new Node(harness, Policy(threshold: 4));
        var taskB = new Node(harness, Policy(threshold: 4));

        await taskA.FailAsync(Source, 2);
        await taskB.FailAsync(Source, 2);

        taskB.Limiter.RefusalFor(Source).Should().NotBeNull("task B's own write returned the fourth failure in the deployment");
        await taskA.Sync.PollAsync(CancellationToken.None);
        taskA.Limiter.RefusalFor(Source).Should().NotBeNull();
        taskA.Limiter.TryBeginAttempt(new(Source, 99)).Admitted.Should().BeFalse();
    }

    [IntegrationTest]
    public async Task RefusalWrittenOnTaskA_TakesEffectOnRunningTaskB_AfterPolling()
    {
        await using var harness = await CreateHarnessAsync();
        var pollInterval = TimeSpan.FromMilliseconds(500);
        var clock = new TransitionClock();
        var timerReady = new TaskCompletionSource<TimeSpan>(TaskCreationOptions.RunContinuationsAsynchronously);
        var timerProvider = Substitute.For<TimeProvider>();
        timerProvider.CreateTimer(Arg.Any<TimerCallback>(), Arg.Any<object?>(), Arg.Any<TimeSpan>(), Arg.Any<TimeSpan>())
            .Returns(call =>
            {
                var period = call.ArgAt<TimeSpan>(3);
                var timer = clock.CreateTimer(call.ArgAt<TimerCallback>(0), call.ArgAt<object?>(1), call.ArgAt<TimeSpan>(2), period);
                timerReady.TrySetResult(period);
                return timer;
            });
        var taskA = new Node(harness, Policy(threshold: 3, pollInterval));
        var taskB = new Node(harness, Policy(threshold: 3, pollInterval), timerProvider);
        await taskB.Sync.StartAsync(CancellationToken.None);
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            (await timerReady.Task.WaitAsync(deadline.Token)).Should().Be(pollInterval);
            await taskA.FailAsync(Source, 3);
            taskA.Limiter.RefusalFor(Source).Should().NotBeNull();

            clock.Advance(pollInterval);
            while (taskB.Limiter.RefusalFor(Source) is null)
            {
                await Task.Delay(25, deadline.Token);
            }
        }
        finally
        {
            await taskB.Sync.StopAsync(CancellationToken.None);
        }
    }

    [IntegrationTest]
    public async Task Restart_KeepsARefusalThatIsInForce()
    {
        await using var harness = await CreateHarnessAsync();
        await new Node(harness, Policy(threshold: 2)).FailAsync(Source, 2);

        var restarted = new Node(harness, Policy(threshold: 2));
        await restarted.Sync.StartAsync(CancellationToken.None);
        await restarted.Sync.StopAsync(CancellationToken.None);

        restarted.Limiter.RefusalFor(Source).Should().NotBeNull("the first poll runs before the host serves requests");
    }

    [IntegrationTest]
    public async Task SuccessfulAndUncountedAttempts_WriteNothing()
    {
        await using var harness = await CreateHarnessAsync();
        var node = new Node(harness, Policy(threshold: 2));

        for (var credential = 0; credential < 5; credential++)
        {
            var key = new AuthenticationAttemptKey(Source, credential);
            node.Limiter.TryBeginAttempt(key).Admitted.Should().BeTrue();
            node.Limiter.EndAttempt(key, AuthenticationAttemptOutcome.NotCounted);
        }

        node.Shared.Recorded.TryRead(out _).Should().BeFalse();
        (await harness.ScalarAsync<long>($"SELECT count(*) FROM {harness.Table("authentication_failure_windows")}"))
            .Should().Be(0);
    }

    [IntegrationTest]
    public async Task Store_KeepsOnlyADigestOfTheSource()
    {
        await using var harness = await CreateHarnessAsync();
        await new Node(harness, Policy(threshold: 5)).FailAsync(Source, 1);

        var row = await harness.ScalarAsync<string>(
            $"SELECT encode(source_digest, 'hex') || encode(source_digest, 'escape') FROM {harness.Table("authentication_failure_windows")}");

        row.Should().NotContain(Source);
    }

    [IntegrationTest]
    public async Task Sweep_EvictsOldUnrefusedWindowsToReachTheRetentionTarget()
    {
        await using var harness = await CreateHarnessAsync();
        var options = Policy(threshold: 1);
        var node = new Node(harness, options);
        await node.FailAsync(Source, 1);
        foreach (var other in new[] { "203.0.113.1", "203.0.113.2", "203.0.113.3" })
        {
            var policy = new AuthenticationFailurePolicy(10, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
            await node.Shared.Ledger.RecordFailureAsync(
                Failure(SharedAuthenticationFailures.Digest(other), policy), CancellationToken.None);
        }

        options.Value.MaxTrackedSources = 2;
        await harness.CreateAuthenticationFailureLedger(options).DeleteExpiredAsync(100, CancellationToken.None);

        (await harness.ScalarAsync<long>($"SELECT count(*) FROM {harness.Table("authentication_failure_windows")}")).Should().Be(2);
        (await harness.CreateAuthenticationFailureLedger(options).ReadRefusalsAsync(CancellationToken.None))
            .Should().ContainSingle(refusal => refusal.Key.SequenceEqual(SharedAuthenticationFailures.Digest(Source)));
    }

    [IntegrationTest]
    public async Task Ledger_ReclaimsAnExpiredSlotAndBoundsConcurrentAdmissions()
    {
        await using var harness = await CreateHarnessAsync();
        var options = Policy(threshold: 1);
        options.Value.MaxTrackedSources = 2;
        var ledger = harness.CreateAuthenticationFailureLedger(options);
        var table = harness.Table("authentication_failure_windows");
        var policy = new AuthenticationFailurePolicy(1, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
        var first = Failure(SharedAuthenticationFailures.Digest(Source), policy);
        await ledger.RecordFailureAsync(first, CancellationToken.None);
        var other = Failure(SharedAuthenticationFailures.Digest("203.0.113.1"), policy);
        var last = Failure(SharedAuthenticationFailures.Digest("203.0.113.2"), policy);

        var outcomes = await Task.WhenAll(
            ledger.RecordFailureAsync(other, CancellationToken.None),
            harness.CreateAuthenticationFailureLedger(options).RecordFailureAsync(last, CancellationToken.None));
        outcomes.Should().ContainSingle(window => window.HasValue);
        (await harness.ScalarAsync<long>($"SELECT count(*) FROM {table}")).Should().Be(2);
        var refusals = await ledger.ReadRefusalsAsync(CancellationToken.None);
        refusals.Should().HaveCount(2).And.Contain(item => item.Key.SequenceEqual(first.SourceDigest));

        await harness.ExecuteAsync($"UPDATE {table} SET expires_at = now(), refused_until = now()");
        var deferred = outcomes[0].HasValue ? last : other;
        (await ledger.RecordFailureAsync(deferred, CancellationToken.None)).Should().NotBeNull();
        (await harness.ScalarAsync<long>($"SELECT count(*) FROM {table}")).Should().Be(2);
        (await ledger.DeleteExpiredAsync(1, CancellationToken.None)).Should().Be(1);
        (await ledger.ReadRefusalsAsync(CancellationToken.None)).Should().ContainSingle();
    }

    [IntegrationTest]
    public async Task Ledger_KeepsTheSameExpiryForWritesInAnActiveCountingWindow()
    {
        await using var harness = await CreateHarnessAsync();
        var ledger = harness.CreateAuthenticationFailureLedger(Policy(threshold: 2));
        var digest = SharedAuthenticationFailures.Digest(Source);
        var policy = new AuthenticationFailurePolicy(3, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
        var table = harness.Table("authentication_failure_windows");
        await ledger.RecordFailureAsync(Failure(digest, policy), CancellationToken.None);
        var firstExpiry = await harness.ScalarAsync<string>($"SELECT expires_at::text FROM {table}");

        var second = await ledger.RecordFailureAsync(Failure(digest, policy), CancellationToken.None);

        second.Should().NotBeNull();
        second.GetValueOrDefault().Failures.Should().Be(2);
        (await harness.ScalarAsync<string>($"SELECT expires_at::text FROM {table}")).Should().Be(firstExpiry);
    }

    [IntegrationTest]
    public async Task Ledger_CombinesConcurrentWritesInOneCountingWindow()
    {
        await using var harness = await CreateHarnessAsync();
        var options = Policy(threshold: 5);
        var policy = new AuthenticationFailurePolicy(5, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
        var failure = Failure(SharedAuthenticationFailures.Digest(Source), policy);

        var windows = await Task.WhenAll(
            harness.CreateAuthenticationFailureLedger(options).RecordFailureAsync(failure, CancellationToken.None),
            harness.CreateAuthenticationFailureLedger(options).RecordFailureAsync(failure, CancellationToken.None));

        windows.Select(window => window.GetValueOrDefault().Failures).Should().BeEquivalentTo(new[] { 1, 2 });
        (await harness.ScalarAsync<int>($"SELECT failures FROM {harness.Table("authentication_failure_windows")}"))
            .Should().Be(2);
    }

    [IntegrationTest]
    public async Task Ledger_SkipsAFailureThatHasAgedOutBeforeTheWrite()
    {
        await using var harness = await CreateHarnessAsync();
        var options = Policy(threshold: 3);
        var clock = Substitute.For<TimeProvider>();
        clock.TimestampFrequency.Returns(TimeSpan.TicksPerSecond);
        clock.GetTimestamp().Returns(0L);
        var ledger = harness.CreateAuthenticationFailureLedger(options, clock);
        var policy = new AuthenticationFailurePolicy(3, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
        var failure = new AuthenticationFailure(SharedAuthenticationFailures.Digest(Source), policy, clock.GetTimestamp(), policy.Window);

        clock.GetTimestamp().Returns(policy.Window.Ticks);
        (await ledger.RecordFailureAsync(failure, CancellationToken.None)).Should().BeNull();
        (await harness.ScalarAsync<long>($"SELECT count(*) FROM {harness.Table("authentication_failure_windows")}"))
            .Should().Be(0);
    }

    private static AuthenticationFailure Failure(byte[] digest, AuthenticationFailurePolicy policy)
        => new(digest, policy, TimeProvider.System.GetTimestamp(), policy.Window);

    private static IOptions<AuthenticationFailureLimiterOptions> Policy(int threshold, TimeSpan? pollInterval = null)
        => Options.Create(new AuthenticationFailureLimiterOptions
        {
            Threshold = threshold,
            Window = TimeSpan.FromMinutes(10),
            BlockDuration = TimeSpan.FromMinutes(5),
            SharedPollInterval = pollInterval ?? TimeSpan.FromSeconds(5),
        });

    private Task<ControlPlaneStateHarness> CreateHarnessAsync()
        => ControlPlaneStateHarness.CreateAsync(database, nameof(PostgresAuthenticationFailureLedgerTests));

    private sealed class Node
    {
        public Node(ControlPlaneStateHarness harness, IOptions<AuthenticationFailureLimiterOptions> options, TimeProvider? timerProvider = null)
        {
            Shared = new SharedAuthenticationFailures(harness.CreateAuthenticationFailureLedger(options), options, TimeProvider.System);
            Limiter = new AuthenticationFailureLimiter(options, TimeProvider.System, NullLogger<AuthenticationFailureLimiter>.Instance)
            {
                Shared = Shared,
            };
            Sync = new SharedAuthenticationFailureSync(Shared, timerProvider ?? TimeProvider.System, NullLogger<SharedAuthenticationFailureSync>.Instance);
        }

        public SharedAuthenticationFailures Shared { get; }

        public AuthenticationFailureLimiter Limiter { get; }

        public SharedAuthenticationFailureSync Sync { get; }

        // Fails distinct credentials, then writes what the limiter recorded, as the running sync would.
        public async Task FailAsync(string source, int times)
        {
            for (var attempt = 0; attempt < times; attempt++)
            {
                var key = new AuthenticationAttemptKey(source, 1000 + attempt);
                if (Limiter.TryBeginAttempt(key).Admitted)
                {
                    Limiter.EndAttempt(key, AuthenticationAttemptOutcome.Failed);
                }

                while (Shared.Recorded.TryRead(out var recorded))
                {
                    await Sync.RecordAsync(recorded, CancellationToken.None);
                }
            }
        }
    }
}
