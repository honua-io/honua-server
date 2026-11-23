// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Diagnostics;
using FluentAssertions;
using Honua.Infrastructure.Authentication;
using Honua.Server.Tests.Infrastructure;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Features.Infrastructure.ControlPlane;

// Each node is one server instance: its own limiter, shared view and sync over the same tables.
[Protocol(TestProtocols.TestQuality)]
[Operation(Operations.TestInfrastructure)]
public sealed class PostgresAuthenticationFailureLedgerTests(DatabaseFixtureAdapter database) : IClassFixture<DatabaseFixtureAdapter>
{
    private const string Source = "198.51.100.7";

    [IntegrationTest]
    public async Task Threshold_IsReachedByFailuresAcrossTwoInstances()
    {
        await using var harness = await CreateHarnessAsync();
        var taskA = new Node(harness, Policy(threshold: 4));
        var taskB = new Node(harness, Policy(threshold: 4));

        await taskA.FailAsync(Source, 2);
        await taskB.FailAsync(Source, 2);

        taskB.Limiter.RefusalFor(Source).Should().NotBeNull("task B's own write returned the fourth failure in the deployment");
        taskA.Limiter.RefusalFor(Source).Should().BeNull("task A has not polled yet and counted only two");
        await taskA.Sync.PollAsync(CancellationToken.None);
        taskA.Limiter.RefusalFor(Source).Should().NotBeNull();
        taskA.Limiter.TryBeginAttempt(new(Source, 99)).Admitted.Should().BeFalse();
    }

    [IntegrationTest]
    public async Task RefusalWrittenOnTaskA_TakesEffectOnRunningTaskB_WithinThePollInterval()
    {
        await using var harness = await CreateHarnessAsync();
        var pollInterval = TimeSpan.FromMilliseconds(500);
        var taskA = new Node(harness, Policy(threshold: 3, pollInterval));
        var taskB = new Node(harness, Policy(threshold: 3, pollInterval));
        await taskB.Sync.StartAsync(CancellationToken.None);
        try
        {
            await taskA.FailAsync(Source, 3);
            taskA.Limiter.RefusalFor(Source).Should().NotBeNull();

            var elapsed = Stopwatch.StartNew();
            while (taskB.Limiter.RefusalFor(Source) is null && elapsed.Elapsed < TimeSpan.FromSeconds(10))
            {
                await Task.Delay(25);
            }

            taskB.Limiter.RefusalFor(Source).Should().NotBeNull();
            elapsed.Elapsed.Should().BeLessThan(pollInterval * 4, "task B applies refusals on its next poll");
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
        options.Value.MaxTrackedSources = 2;
        var node = new Node(harness, options);
        await node.FailAsync(Source, 1);
        foreach (var other in new[] { "203.0.113.1", "203.0.113.2", "203.0.113.3" })
        {
            var policy = new AuthenticationFailurePolicy(10, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
            await node.Shared.Ledger.RecordFailureAsync(
                new(SharedAuthenticationFailures.Digest(other), DateTimeOffset.UtcNow, policy), CancellationToken.None);
        }

        await harness.CreateAuthenticationFailureLedger(options).DeleteExpiredAsync(100, CancellationToken.None);

        (await harness.ScalarAsync<long>($"SELECT count(*) FROM {harness.Table("authentication_failure_windows")}")).Should().Be(2);
        (await harness.CreateAuthenticationFailureLedger(options).ReadRefusalsAsync(DateTimeOffset.UtcNow, CancellationToken.None))
            .Should().ContainSingle(refusal => refusal.Key.SequenceEqual(SharedAuthenticationFailures.Digest(Source)));
    }

    [IntegrationTest]
    public async Task Sweep_SharesTheBatchBudgetAndPreservesRefusalsAboveTheRetentionTarget()
    {
        await using var harness = await CreateHarnessAsync();
        var options = Policy(threshold: 1);
        options.Value.MaxTrackedSources = 1;
        var ledger = harness.CreateAuthenticationFailureLedger(options);
        var now = DateTimeOffset.UtcNow;
        var refusal = new AuthenticationFailurePolicy(1, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
        var unrefused = refusal with { Threshold = 2 };
        await ledger.RecordFailureAsync(new(SharedAuthenticationFailures.Digest("203.0.113.1"), now, refusal), CancellationToken.None);
        await ledger.RecordFailureAsync(new(SharedAuthenticationFailures.Digest("203.0.113.2"), now, refusal), CancellationToken.None);
        await ledger.RecordFailureAsync(new(SharedAuthenticationFailures.Digest("203.0.113.3"), now, unrefused), CancellationToken.None);
        await ledger.RecordFailureAsync(new(SharedAuthenticationFailures.Digest("203.0.113.4"), now.AddHours(-1), unrefused), CancellationToken.None);

        (await ledger.DeleteExpiredAsync(1, CancellationToken.None)).Should().Be(1);
        (await ledger.DeleteExpiredAsync(1, CancellationToken.None)).Should().Be(1);
        (await ledger.DeleteExpiredAsync(1, CancellationToken.None)).Should().Be(0);
        (await ledger.ReadRefusalsAsync(now, CancellationToken.None)).Should().HaveCount(2);
    }

    [IntegrationTest]
    public async Task Ledger_IgnoresAnEventOlderThanTheStoredWindow()
    {
        await using var harness = await CreateHarnessAsync();
        var ledger = harness.CreateAuthenticationFailureLedger(Policy(threshold: 2));
        var digest = SharedAuthenticationFailures.Digest(Source);
        var now = DateTimeOffset.UtcNow;
        var policy = new AuthenticationFailurePolicy(2, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
        var current = await ledger.RecordFailureAsync(new(digest, now, policy), CancellationToken.None);

        var afterOlderEvent = await ledger.RecordFailureAsync(new(digest, now.AddMinutes(-10), policy), CancellationToken.None);

        afterOlderEvent.Should().Be(current);
    }

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
        public Node(ControlPlaneStateHarness harness, IOptions<AuthenticationFailureLimiterOptions> options)
        {
            Shared = new SharedAuthenticationFailures(harness.CreateAuthenticationFailureLedger(options), options);
            Limiter = new AuthenticationFailureLimiter(options, TimeProvider.System, NullLogger<AuthenticationFailureLimiter>.Instance)
            {
                Shared = Shared,
            };
            Sync = new SharedAuthenticationFailureSync(Shared, TimeProvider.System, NullLogger<SharedAuthenticationFailureSync>.Instance);
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
