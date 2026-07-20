// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.ControlPlane;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Logging.Abstractions;
using StackExchange.Redis;
using Testcontainers.Redis;

namespace Honua.Server.Tests.Features.Infrastructure.ControlPlane;

/// <summary>
/// Proves the Redis durable-job loss contract (#2946): Redis is required for durable
/// jobs/workflows (not a pure cache — <c>docs/guides/deploy/backup-and-restore.md</c>), and a
/// transient Redis outage while a job is actively executing must not silently lose the job or
/// leave it permanently wedged. Unlike <see cref="RedisExecutionSubstrateIntegrationTests"/>
/// (which simulates a stale claim by hand-writing an old timestamp against a live, connected
/// Redis), this test actually stops and restarts a real Redis container mid-job.
/// </summary>
/// <remarks>
/// This test intentionally does not join the shared <c>[Collection("Redis")]</c>
/// (<see cref="Honua.TestKit.RedisFixture"/>): it needs a dedicated container it can stop and
/// restart, and reusing the process-wide shared container for that would break every other
/// concurrently-running Redis test. It builds its own <see cref="ConnectionMultiplexer"/> using
/// the same resilience configuration the server itself uses in production
/// (<c>src/Honua.Server/Program.cs</c>: <c>AbortOnConnectFail = false</c> plus an exponential
/// reconnect policy) so the observed behavior matches what a deployed instance actually does.
/// </remarks>
[Protocol(TestProtocols.Infrastructure)]
[Operation(Operations.TestInfrastructure)]
public sealed class RedisJobExecutionResilienceTests
{
    [IntegrationTest]
    public async Task JobExecutionService_WhenRedisRestartsMidJob_JobSurvivesWithNoSilentLossOrPermanentWedge()
    {
        // Pin an explicit host port rather than Testcontainers' default dynamic allocation:
        // Docker Desktop (this environment) can reassign the host-side port mapping for the same
        // container across a stop/start cycle, which would otherwise leave the multiplexer
        // permanently targeting a dead port after the "restart" and turn this into a false
        // failure unrelated to the job-execution contract under test.
        var hostPort = ReserveLoopbackPort();
        await using var container = new RedisBuilder("redis:7.2-alpine")
            .WithPortBinding(hostPort, 6379)
            .Build();
        await container.StartAsync();

        var options = ConfigurationOptions.Parse(container.GetConnectionString(), ignoreUnknown: true);
        options.AbortOnConnectFail = false;
        options.ConnectRetry = Math.Max(options.ConnectRetry, 3);
        options.ReconnectRetryPolicy ??= new ExponentialRetry(5_000);

        await using var multiplexer = await ConnectionMultiplexer.ConnectAsync(options);

        var jobStore = new RedisExecutionJobStore(multiplexer, NullLogger<RedisExecutionJobStore>.Instance);
        var logStore = new RedisExecutionLogStore(multiplexer, NullLogger<RedisExecutionLogStore>.Instance);
        var queue = new RedisJobQueue(multiplexer, jobStore, NullLogger<RedisJobQueue>.Instance);

        var operationId = $"job-redis-restart-{Guid.NewGuid():N}";
        var callback = new RecordingTerminalCallback();

        // Long enough to comfortably span the container stop/start cycle below, so the
        // executor is provably still "in flight" (mid-Task.Delay) while Redis is unreachable.
        var executor = new DelayedSuccessJobExecutor(ExecutionJobKind.Geoprocessing, TimeSpan.FromSeconds(8));

        var service = new JobExecutionService(
            queue,
            jobStore,
            [executor],
            new ExecutionJobCancellationTokens(),
            [callback],
            logStore,
            NullLogger<JobExecutionService>.Instance);

        await jobStore.TryCreateAsync(CreateQueuedJob(operationId));
        await queue.EnqueueAsync(operationId);

        await service.StartAsync(CancellationToken.None);
        try
        {
            // Confirm the job is genuinely executing before pulling the plug on Redis.
            await WaitForJobAsync(
                jobStore,
                operationId,
                job => job.Status == ExecutionJobStatus.Running,
                TimeSpan.FromSeconds(10));

            // Simulate a Redis restart (pod eviction, upgrade, OOM) while the job is mid-flight.
            // The heartbeat pump's writes during this window fail and are caught/logged
            // internally (JobExecutionContext.RunHeartbeatPumpAsync) — this must not throw out
            // of the worker or fail the test.
            await container.StopAsync();
            await Task.Delay(TimeSpan.FromSeconds(2));
            await container.StartAsync();

            // The executor keeps running (it never touched Redis itself) and completes after
            // its delay. Two outcomes both satisfy "no silent loss, no permanent wedge": either
            // the finalize write lands cleanly once Redis reconnects, or a Redis hiccup exactly
            // at finalize time routes through AbandonJobAsync's retry path, which (with the
            // zero-delay policy above) the same still-running worker immediately reclaims and
            // re-executes to completion. The generous timeout below accounts for that extra
            // reclaim-and-rerun round trip on top of the executor's own delay.
            var terminal = await callback.WhenCompleted.WaitAsync(TimeSpan.FromSeconds(60));
            terminal.OperationId.Should().Be(operationId);
            terminal.Status.Should().Be(ExecutionJobStatus.Succeeded);

            var stored = await WaitForJobAsync(
                jobStore,
                operationId,
                job => job.Status == ExecutionJobStatus.Succeeded,
                TimeSpan.FromSeconds(5));
            stored.CurrentPhase.Should().Be("Completed");
            stored.CompletedAt.Should().NotBeNull();

            var queueDepth = await queue.GetQueueDepthAsync();
            queueDepth.Should().Be(0);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
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
            // A Redis hiccup landing exactly on the finalize write (rather than only on the
            // heartbeat pump) routes through JobExecutionService.AbandonJobAsync, which requeues
            // for retry rather than failing outright while attempts remain. Use a zero-delay
            // retry policy (matching the determinism trick already used by
            // RedisExecutionSubstrateIntegrationTests) so that path — if it fires — resolves via
            // an immediate reclaim by this same still-running worker instead of the real
            // production default's 30s+ backoff, keeping the test's timing deterministic while
            // still exercising the genuine retry/reclaim code path.
            RetryPolicy = new JobRetryPolicy
            {
                MaxAttempts = 5,
                Strategy = BackoffStrategy.Fixed,
                BaseDelay = TimeSpan.Zero,
                MaxDelay = TimeSpan.Zero
            },
            TimeoutPolicy = new JobTimeoutPolicy
            {
                MaxDuration = TimeSpan.FromMinutes(5)
            },
            Spec = new ExecutionJobSpec
            {
                Kind = ExecutionJobKind.Geoprocessing,
                TargetKind = BatchComputeTargetKind.KubernetesJob,
                Backend = "integration-test",
                WorkloadName = "redis-restart-resilience"
            }
        };
    }

    /// <summary>
    /// Binds a loopback TCP listener to an OS-assigned free port and immediately releases it, so
    /// the returned port can be handed to the Redis container builder's <c>WithPortBinding</c> as
    /// a (best-effort, TOCTOU-window) stable host port for the lifetime of this test's container.
    /// </summary>
    private static int ReserveLoopbackPort()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    private static async Task<ExecutionJobRecord> WaitForJobAsync(
        RedisExecutionJobStore jobStore,
        string operationId,
        Func<ExecutionJobRecord, bool> predicate,
        TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow.Add(timeout);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var job = await jobStore.GetAsync(operationId);
            if (job != null && predicate(job))
            {
                return job;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100));
        }

        throw new TimeoutException($"Timed out waiting for job '{operationId}' to reach the expected state.");
    }

    private sealed class DelayedSuccessJobExecutor(ExecutionJobKind kind, TimeSpan delay) : IJobExecutor
    {
        public ExecutionJobKind Kind { get; } = kind;

        public async Task<JobExecutionResult> ExecuteAsync(
            ExecutionJobRecord job,
            IJobExecutionContext context,
            CancellationToken cancellationToken)
        {
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            return JobExecutionResult.Succeeded();
        }
    }

    private sealed class RecordingTerminalCallback : IJobTerminalCallback
    {
        private readonly TaskCompletionSource<ExecutionJobRecord> _whenCompleted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<ExecutionJobRecord> WhenCompleted => _whenCompleted.Task;

        public ValueTask OnTerminalAsync(ExecutionJobRecord job, CancellationToken cancellationToken)
        {
            _whenCompleted.TrySetResult(job);
            return ValueTask.CompletedTask;
        }
    }
}
