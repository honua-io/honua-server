// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using FluentAssertions;
using Honua.ControlPlane;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Logging.Abstractions;
using StackExchange.Redis;

namespace Honua.Server.Tests.Features.Infrastructure.ControlPlane;

/// <summary>
/// At-most-once claim evidence for the control-plane job queue against a real Redis
/// (honua-server#4403).
/// </summary>
/// <remarks>
/// <para>
/// Before this file the repository had no test in which two workers ever contended for
/// the same job: every claim contest was sequential or stubbed, so the losing branch of
/// <c>RedisJobQueue.AtomicClaimScript</c> had no coverage and "at most one worker owns a
/// job" rested on reading the Lua by eye. The mock-level companions in
/// <see cref="RedisJobQueueTests"/> pin the loser's *behaviour* deterministically; this
/// file proves the *atomicity* that behaviour depends on, under genuine
/// <see cref="Task.WhenAll(System.Collections.Generic.IEnumerable{Task})"/> contention
/// against the real server-side script execution.
/// </para>
/// <para>
/// It also carries the crash-recovery receipt asked for by #4403 acceptance criteria 4
/// and 5: an executor with a counted, observable side effect, a heartbeat that lapses
/// naturally (rather than a test hand-writing an old <c>LastHeartbeatAt</c>), and an
/// assertion that the recovered attempt produces exactly one terminal notification and
/// one artifact even though the stale worker later returns a success of its own.
/// </para>
/// </remarks>
[Collection(RedisFixture.CollectionName)]
[Protocol(TestProtocols.Infrastructure)]
[Operation(Operations.TestInfrastructure)]
public sealed class JobClaimContentionRedisTests(RedisFixture redis)
{
    private const string ClaimedSetKey = "controlplane:jobqueue:claimed";
    private const string QueueKey = "controlplane:jobqueue:pending";

    /// <summary>
    /// The invariant the durable queue exists to provide: one queued job, many workers
    /// racing for it, exactly one owner. The winner's record must show exactly one
    /// attempt — a second claim that "wins" and rewrites the record would burn a retry
    /// and duplicate execution.
    /// </summary>
    [IntegrationTest]
    public async Task TryClaimAsync_EightWorkersRaceOneJob_ExactlyOneWinsWithOneAttempt()
    {
        await using var harness = await JobQueueRedisHarness.CreateAsync(redis.ConnectionString);
        var operationId = $"race-single-{Guid.NewGuid():N}";

        (await harness.JobStore.TryCreateAsync(CreateQueuedJob(operationId))).Should().BeTrue();
        await harness.Queue.EnqueueAsync(operationId);

        const int workerCount = 8;
        using var start = new SemaphoreSlim(0, workerCount);
        // Park every racer on the same barrier first, then release them together, so their
        // ZREMs genuinely interleave instead of being serialized by task start-up order.
        var racers = Enumerable.Range(0, workerCount).Select(async index =>
        {
            var workerId = $"worker-{index}";
            await start.WaitAsync();
            return await harness.Queue.TryClaimAsync(workerId).ConfigureAwait(false);
        }).ToArray();
        start.Release(workerCount);
        var claims = await Task.WhenAll(racers);

        var winners = claims.Where(claim => claim != null).ToArray();
        winners.Should().ContainSingle("exactly one worker may win an at-most-once claim");
        winners[0].Should().Be(operationId);

        var stored = await harness.JobStore.GetAsync(operationId);
        stored.Should().NotBeNull();
        stored!.Status.Should().Be(ExecutionJobStatus.Provisioning);
        stored.ClaimedBy.Should().NotBeNullOrEmpty();
        stored.AttemptCount.Should().Be(1, "a losing claim must not bump the winner's attempt count");

        // The job moved from pending to claimed atomically: it must be in exactly one set.
        (await harness.Database.SortedSetScoreAsync(QueueKey, operationId)).Should().BeNull();
        (await harness.Database.SortedSetScoreAsync(ClaimedSetKey, operationId)).Should().NotBeNull();
    }

    /// <summary>
    /// The same invariant at queue scale: many workers draining a contested queue must
    /// partition it, never duplicate it. This is the case that catches a claim script
    /// that is atomic for a single ZREM but loses its guarantee across a batch scan.
    /// </summary>
    [IntegrationTest]
    public async Task TryClaimAsync_WorkersDrainContestedQueue_EveryJobIsClaimedExactlyOnce()
    {
        await using var harness = await JobQueueRedisHarness.CreateAsync(redis.ConnectionString);

        const int jobCount = 24;
        const int workerCount = 6;
        var prefix = $"race-drain-{Guid.NewGuid():N}";
        var expected = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < jobCount; index++)
        {
            var operationId = $"{prefix}-{index}";
            expected.Add(operationId);
            (await harness.JobStore.TryCreateAsync(CreateQueuedJob(operationId))).Should().BeTrue();
            await harness.Queue.EnqueueAsync(operationId);
        }

        var claimsByWorker = new ConcurrentBag<(string WorkerId, string OperationId)>();
        using var start = new SemaphoreSlim(0, workerCount);
        var drainers = Enumerable.Range(0, workerCount).Select(async index =>
        {
            var workerId = $"drain-worker-{index}";
            await start.WaitAsync();
            while (true)
            {
                var claimed = await harness.Queue.TryClaimAsync(workerId).ConfigureAwait(false);
                if (claimed == null)
                {
                    return;
                }

                claimsByWorker.Add((workerId, claimed));
            }
        }).ToArray();
        start.Release(workerCount);
        await Task.WhenAll(drainers);

        var claimed = claimsByWorker.ToArray();
        claimed.Select(entry => entry.OperationId).Should().OnlyHaveUniqueItems(
            "a job claimed twice is a job executed twice");
        claimed.Select(entry => entry.OperationId).Should().BeEquivalentTo(expected,
            "every queued job must be claimable exactly once, with none stranded");

        foreach (var (workerId, operationId) in claimed)
        {
            var record = await harness.JobStore.GetAsync(operationId);
            record.Should().NotBeNull();
            record!.ClaimedBy.Should().Be(workerId, "the durable record must agree with the queue's winner");
            record.AttemptCount.Should().Be(1);
        }

        (await harness.Queue.GetQueueDepthAsync()).Should().Be(0);
    }

    /// <summary>
    /// End-to-end worker-loss recovery with a counted side effect (#4403 criteria 4 and 5).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The heartbeat lapses <em>naturally</em>: the job's <see cref="JobHeartbeatPolicy.Interval"/>
    /// is longer than the whole test, so the running worker's pump never writes a beat,
    /// while its <see cref="JobHeartbeatPolicy.Timeout"/> is one second. No test code
    /// backdates <c>LastHeartbeatAt</c> — the expiry the reconciler acts on is the one the
    /// worker's own silence produced, which is what a killed pod actually looks like.
    /// </para>
    /// <para>
    /// The side effect is counted: the executor appends one ledger entry per invocation.
    /// After recovery the ledger must hold exactly two entries under two distinct owners —
    /// proving the recovered attempt genuinely re-ran an executor rather than being a bare
    /// re-claim — while the terminal notification and the published artifact must each
    /// appear exactly once, even though the stale first worker afterwards returns a success
    /// of its own and tries to publish and finalize it.
    /// </para>
    /// </remarks>
    [IntegrationTest]
    public async Task WorkerLoss_NaturalHeartbeatLapse_ReExecutesOnceAndSuppressesTheStaleWorkersTerminal()
    {
        await using var harness = await JobQueueRedisHarness.CreateAsync(redis.ConnectionString);
        var operationId = $"crash-recovery-{Guid.NewGuid():N}";

        var executor = new LedgerExecutor();
        var terminals = new CountingTerminalCallback();

        var job = CreateQueuedJob(operationId) with
        {
            // Interval far longer than this test: the worker's pump never writes a beat,
            // so the lapse the reconciler observes is genuine worker silence.
            HeartbeatPolicy = new JobHeartbeatPolicy
            {
                Interval = TimeSpan.FromMinutes(30),
                Timeout = TimeSpan.FromSeconds(1)
            },
            RetryPolicy = new JobRetryPolicy
            {
                MaxAttempts = 3,
                Strategy = BackoffStrategy.Fixed,
                BaseDelay = TimeSpan.Zero,
                MaxDelay = TimeSpan.Zero
            },
            // Large enough that only the heartbeat check can fire.
            TimeoutPolicy = new JobTimeoutPolicy { MaxDuration = TimeSpan.FromMinutes(30) }
        };

        (await harness.JobStore.TryCreateAsync(job)).Should().BeTrue();
        await harness.Queue.EnqueueAsync(operationId);

        var lostWorker = harness.CreateWorker(executor, terminals);
        await lostWorker.StartAsync(CancellationToken.None);
        JobExecutionService? recoveredWorker = null;

        try
        {
            var running = await harness.WaitForJobAsync(
                operationId,
                record => record.Status == ExecutionJobStatus.Running,
                TimeSpan.FromSeconds(30));
            var lostOwner = running.ClaimedBy;
            lostOwner.Should().NotBeNullOrEmpty();
            executor.Invocations.Should().ContainSingle("the lost worker's attempt really executed");

            // Let the one-second heartbeat timeout elapse with no beat written.
            await Task.Delay(TimeSpan.FromSeconds(2));
            var beforeSweep = await harness.JobStore.GetAsync(operationId);
            beforeSweep!.LastHeartbeatAt.Should().Be(
                running.LastHeartbeatAt,
                "the worker must have written no heartbeat — the lapse is real, not backdated");

            // The reconciler runs with its own cancellation-token registry, as it does in
            // production when it lives in a different process from the lost worker: it must
            // recover the job without being able to reach into that worker.
            await harness.RunReconciliationSweepAsync(CancellationToken.None);

            var requeued = await harness.WaitForJobAsync(
                operationId,
                record => record.Status == ExecutionJobStatus.Queued,
                TimeSpan.FromSeconds(10));
            requeued.ClaimedBy.Should().BeNull();
            requeued.ArtifactReferences.Should().BeEmpty("a retry starts from a clean artifact set");

            recoveredWorker = harness.CreateWorker(executor, terminals);
            await recoveredWorker.StartAsync(CancellationToken.None);

            var reclaimed = await harness.WaitForJobAsync(
                operationId,
                record => record.Status == ExecutionJobStatus.Running && record.AttemptCount == 2,
                TimeSpan.FromSeconds(30));
            reclaimed.ClaimedBy.Should().NotBe(lostOwner, "the recovered attempt belongs to a different worker");
            executor.Invocations.Should().HaveCount(2, "the recovered attempt re-ran a real executor");

            // Release the lost worker: it now returns a success and tries to publish and
            // finalize it while the recovered attempt still owns the job.
            executor.ReleaseAttempt(1);
            await executor.WhenAttemptCompleted(1).WaitAsync(TimeSpan.FromSeconds(30));
            await Task.Delay(TimeSpan.FromSeconds(2));

            terminals.Notifications.Should().BeEmpty(
                "the stale worker must not finalize a job it no longer owns");
            var stillRunning = await harness.JobStore.GetAsync(operationId);
            stillRunning!.Status.Should().Be(ExecutionJobStatus.Running);
            stillRunning.ArtifactReferences.Should().BeEmpty(
                "the stale worker's artifact publication must be fenced out");

            // Now let the owning attempt finish.
            executor.ReleaseAttempt(2);
            var succeeded = await harness.WaitForJobAsync(
                operationId,
                record => record.Status == ExecutionJobStatus.Succeeded,
                TimeSpan.FromSeconds(30));

            // Give any duplicate terminal notification time to arrive before asserting it did not.
            await Task.Delay(TimeSpan.FromSeconds(2));

            succeeded.AttemptCount.Should().Be(2);
            succeeded.ClaimedBy.Should().Be(reclaimed.ClaimedBy);
            succeeded.ArtifactReferences.Should().ContainSingle(
                "exactly one attempt's artifact may survive a worker-loss recovery")
                .Which.Should().Be(LedgerExecutor.ArtifactFor(2));

            terminals.Notifications.Should().ContainSingle(
                "worker loss must not produce a duplicate terminal notification");
            terminals.Notifications[0].Status.Should().Be(ExecutionJobStatus.Succeeded);
            terminals.Notifications[0].AttemptCount.Should().Be(2);

            executor.Invocations.Should().HaveCount(2, "no third execution may be triggered by the recovery");
            executor.Invocations.Select(invocation => invocation.Owner).Should().OnlyHaveUniqueItems();

            (await harness.Queue.GetQueueDepthAsync()).Should().Be(0);
            (await harness.Database.SortedSetScoreAsync(ClaimedSetKey, operationId)).Should().BeNull();
        }
        finally
        {
            executor.ReleaseAll();
            await lostWorker.StopAsync(CancellationToken.None);
            if (recoveredWorker != null)
            {
                await recoveredWorker.StopAsync(CancellationToken.None);
            }
        }
    }

    private static ExecutionJobRecord CreateQueuedJob(string operationId)
    {
        var now = DateTimeOffset.UtcNow;
        return new ExecutionJobRecord
        {
            OperationId = operationId,
            Status = ExecutionJobStatus.Queued,
            CreatedAt = now,
            UpdatedAt = now,
            RetryPolicy = new JobRetryPolicy
            {
                MaxAttempts = 3,
                Strategy = BackoffStrategy.Fixed,
                BaseDelay = TimeSpan.Zero,
                MaxDelay = TimeSpan.Zero
            },
            TimeoutPolicy = new JobTimeoutPolicy { MaxDuration = TimeSpan.FromMinutes(30) },
            Spec = new ExecutionJobSpec
            {
                Kind = ExecutionJobKind.Geoprocessing,
                TargetKind = BatchComputeTargetKind.KubernetesJob,
                Backend = "integration-test",
                WorkloadName = "job-claim-contention"
            }
        };
    }

    /// <summary>
    /// Records one entry per execution and holds each attempt at a test-controlled gate,
    /// so "did the recovered attempt really run?" and "did the stale attempt's side effects
    /// escape?" are both directly assertable rather than inferred.
    /// </summary>
    private sealed class LedgerExecutor : IJobExecutor
    {
        private readonly ConcurrentDictionary<int, TaskCompletionSource> _gates = new();
        private readonly ConcurrentDictionary<int, TaskCompletionSource> _completions = new();
        private readonly ConcurrentQueue<(int Attempt, string? Owner)> _invocations = new();

        public ExecutionJobKind Kind => ExecutionJobKind.Geoprocessing;

        public IReadOnlyList<(int Attempt, string? Owner)> Invocations => _invocations.ToArray();

        public static string ArtifactFor(int attempt) => $"artifact://attempt-{attempt}";

        public void ReleaseAttempt(int attempt) => Gate(attempt).TrySetResult();

        public Task WhenAttemptCompleted(int attempt) => Completion(attempt).Task;

        public void ReleaseAll()
        {
            foreach (var gate in _gates.Values)
            {
                gate.TrySetResult();
            }
        }

        public async Task<JobExecutionResult> ExecuteAsync(
            ExecutionJobRecord job,
            IJobExecutionContext context,
            CancellationToken cancellationToken)
        {
            var attempt = job.AttemptCount;
            _invocations.Enqueue((attempt, job.ClaimedBy));

            try
            {
                await Gate(attempt).Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                await context.PublishArtifactAsync(ArtifactFor(attempt), cancellationToken).ConfigureAwait(false);
                return JobExecutionResult.Succeeded();
            }
            finally
            {
                Completion(attempt).TrySetResult();
            }
        }

        private TaskCompletionSource Gate(int attempt)
            => _gates.GetOrAdd(attempt, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));

        private TaskCompletionSource Completion(int attempt)
            => _completions.GetOrAdd(attempt, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
    }

    private sealed class CountingTerminalCallback : IJobTerminalCallback
    {
        private readonly ConcurrentQueue<ExecutionJobRecord> _notifications = new();

        public IReadOnlyList<ExecutionJobRecord> Notifications => _notifications.ToArray();

        public ValueTask OnTerminalAsync(ExecutionJobRecord job, CancellationToken cancellationToken)
        {
            _notifications.Enqueue(job);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class JobQueueRedisHarness : IAsyncDisposable
    {
        private readonly ConnectionMultiplexer _multiplexer;
        private readonly IServer _server;

        private JobQueueRedisHarness(ConnectionMultiplexer multiplexer, IServer server)
        {
            _multiplexer = multiplexer;
            _server = server;
            Database = multiplexer.GetDatabase();
            JobStore = new RedisExecutionJobStore(multiplexer, NullLogger<RedisExecutionJobStore>.Instance);
            LogStore = new RedisExecutionLogStore(multiplexer, NullLogger<RedisExecutionLogStore>.Instance);
            Queue = new RedisJobQueue(multiplexer, JobStore, NullLogger<RedisJobQueue>.Instance);
        }

        public IDatabase Database { get; }

        public RedisExecutionJobStore JobStore { get; }

        public RedisExecutionLogStore LogStore { get; }

        public RedisJobQueue Queue { get; }

        public static async Task<JobQueueRedisHarness> CreateAsync(string connectionString)
        {
            var multiplexer = await ConnectionMultiplexer.ConnectAsync(connectionString);
            var endpoints = multiplexer.GetEndPoints();
            if (endpoints.Length == 0)
            {
                throw new InvalidOperationException("Redis connection string did not provide any endpoints.");
            }

            var harness = new JobQueueRedisHarness(multiplexer, multiplexer.GetServer(endpoints[0]));
            await harness.CleanupAsync();
            return harness;
        }

        public JobExecutionService CreateWorker(IJobExecutor executor, IJobTerminalCallback terminalCallback)
            => new(
                Queue,
                JobStore,
                [executor],
                new ExecutionJobCancellationTokens(),
                [terminalCallback],
                LogStore,
                NullLogger<JobExecutionService>.Instance);

        public async Task RunReconciliationSweepAsync(CancellationToken cancellationToken)
        {
            var service = new JobReconciliationService(
                JobStore,
                Queue,
                Queue,
                new ExecutionJobCancellationTokens(),
                Array.Empty<IJobTerminalCallback>(),
                LogStore,
                NullLogger<JobReconciliationService>.Instance);

            var method = typeof(JobReconciliationService).GetMethod(
                "SweepActiveJobsAsync",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                ?? throw new InvalidOperationException("SweepActiveJobsAsync was not found on JobReconciliationService.");

            await (Task)method.Invoke(service, [cancellationToken])!;
        }

        public async Task<ExecutionJobRecord> WaitForJobAsync(
            string operationId,
            Func<ExecutionJobRecord, bool> predicate,
            TimeSpan timeout)
        {
            var deadline = DateTimeOffset.UtcNow.Add(timeout);
            ExecutionJobRecord? last = null;
            while (DateTimeOffset.UtcNow < deadline)
            {
                last = await JobStore.GetAsync(operationId);
                if (last != null && predicate(last))
                {
                    return last;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(100));
            }

            throw new TimeoutException(
                $"Timed out waiting for job '{operationId}'. Last observed: " +
                (last == null ? "<deleted>" : $"{last.Status}/attempt {last.AttemptCount}/owner {last.ClaimedBy}"));
        }

        public async ValueTask DisposeAsync()
        {
            await CleanupAsync();
            await _multiplexer.DisposeAsync();
        }

        private async Task CleanupAsync()
        {
            var keys = _server.Keys(pattern: "controlplane:*").ToArray();
            if (keys.Length == 0)
            {
                return;
            }

            await Database.KeyDeleteAsync(keys);
        }
    }
}
