// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics.Metrics;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Server.Features.Infrastructure.ControlPlane;
using Honua.Server.Tests.Helpers;
using Honua.ServiceDefaults;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Honua.Server.Tests.Features.Infrastructure.ControlPlane;

/// <summary>
/// Verifies that the reconciliation service re-reads job state before applying
/// heartbeat or timeout transitions, preventing overwrites of jobs that completed
/// between the sweep snapshot and the handler invocation.
/// </summary>
[Collection("Unit")]
public sealed class JobReconciliationServiceTests
{
    private static ExecutionJobRecord CreateRunningJob(
        string operationId = "job-1",
        string claimedBy = "worker-1",
        int attemptCount = 1,
        JobRetryPolicy? retryPolicy = null,
        JobHeartbeatPolicy? heartbeatPolicy = null,
        JobTimeoutPolicy? timeoutPolicy = null)
    {
        var now = DateTimeOffset.UtcNow;
        return new ExecutionJobRecord
        {
            OperationId = operationId,
            Status = ExecutionJobStatus.Running,
            CreatedAt = now.AddMinutes(-10),
            UpdatedAt = now.AddMinutes(-2),
            ClaimedBy = claimedBy,
            ClaimedAt = now.AddMinutes(-5),
            LastHeartbeatAt = now.AddMinutes(-3),
            AttemptCount = attemptCount,
            RetryPolicy = retryPolicy,
            HeartbeatPolicy = heartbeatPolicy ?? new JobHeartbeatPolicy
            {
                Interval = TimeSpan.FromSeconds(10),
                Timeout = TimeSpan.FromSeconds(30)
            },
            TimeoutPolicy = timeoutPolicy ?? new JobTimeoutPolicy
            {
                MaxDuration = TimeSpan.FromMinutes(3)
            },
            Spec = new ExecutionJobSpec
            {
                Kind = ExecutionJobKind.Geoprocessing,
                TargetKind = BatchComputeTargetKind.KubernetesJob,
                Backend = "local",
                WorkloadName = "test"
            }
        };
    }

    [UnitTest]
    public async Task HeartbeatExpiry_SkipsTransition_WhenJobAlreadySucceeded()
    {
        var snapshot = CreateRunningJob(
            heartbeatPolicy: new JobHeartbeatPolicy
            {
                Interval = TimeSpan.FromSeconds(1),
                Timeout = TimeSpan.FromSeconds(1)
            });

        var succeeded = snapshot with
        {
            Status = ExecutionJobStatus.Succeeded,
            CompletedAt = DateTimeOffset.UtcNow
        };

        var jobStore = Substitute.For<IExecutionJobStore>().WithTrySet();
        jobStore.ListActiveAsync(kind: null, cancellationToken: Arg.Any<CancellationToken>())
            .Returns([snapshot]);
        jobStore.GetAsync(snapshot.OperationId, Arg.Any<CancellationToken>())
            .Returns(succeeded);

        var jobQueue = Substitute.For<IJobQueue>();
        var claimReconciler = Substitute.For<IQueueClaimReconciler>();

        var service = new JobReconciliationService(
            jobStore, jobQueue, claimReconciler, new ExecutionJobCancellationTokens(),
            Array.Empty<IJobTerminalCallback>(), null, NullLogger<JobReconciliationService>.Instance);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await RunSingleSweepAsync(service, cts.Token);

        // The store should NOT have been written to with a Failed/Queued update.
        await jobStore.DidNotReceive().SetAsync(
            Arg.Is<ExecutionJobRecord>(j =>
                j.Status == ExecutionJobStatus.Failed || j.Status == ExecutionJobStatus.Queued),
            Arg.Any<TimeSpan?>(),
            Arg.Any<CancellationToken>());

        await jobQueue.DidNotReceive().RemoveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await jobQueue.DidNotReceive().RequeueAsync(
            Arg.Any<string>(), Arg.Any<OperationPriority>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>());
    }

    [UnitTest]
    public async Task TimeoutExpiry_SkipsTransition_WhenJobAlreadySucceeded()
    {
        var snapshot = CreateRunningJob(
            timeoutPolicy: new JobTimeoutPolicy { MaxDuration = TimeSpan.FromSeconds(1) });
        // Ensure ClaimedAt is old enough to trigger timeout.
        snapshot = snapshot with { ClaimedAt = DateTimeOffset.UtcNow.AddMinutes(-10) };

        var succeeded = snapshot with
        {
            Status = ExecutionJobStatus.Succeeded,
            CompletedAt = DateTimeOffset.UtcNow
        };

        var jobStore = Substitute.For<IExecutionJobStore>().WithTrySet();
        jobStore.ListActiveAsync(kind: null, cancellationToken: Arg.Any<CancellationToken>())
            .Returns([succeeded.Status == ExecutionJobStatus.Succeeded ? snapshot : snapshot]);
        jobStore.GetAsync(snapshot.OperationId, Arg.Any<CancellationToken>())
            .Returns(succeeded);

        var jobQueue = Substitute.For<IJobQueue>();
        var claimReconciler = Substitute.For<IQueueClaimReconciler>();

        var service = new JobReconciliationService(
            jobStore, jobQueue, claimReconciler, new ExecutionJobCancellationTokens(),
            Array.Empty<IJobTerminalCallback>(), null, NullLogger<JobReconciliationService>.Instance);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await RunSingleSweepAsync(service, cts.Token);

        await jobStore.DidNotReceive().SetAsync(
            Arg.Is<ExecutionJobRecord>(j => j.Status == ExecutionJobStatus.Failed),
            Arg.Any<TimeSpan?>(),
            Arg.Any<CancellationToken>());

        await jobQueue.DidNotReceive().RemoveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [UnitTest]
    public async Task HeartbeatExpiry_SkipsTransition_WhenClaimChangedToAnotherWorker()
    {
        var snapshot = CreateRunningJob(
            claimedBy: "worker-1",
            heartbeatPolicy: new JobHeartbeatPolicy
            {
                Interval = TimeSpan.FromSeconds(1),
                Timeout = TimeSpan.FromSeconds(1)
            });

        var reclaimedByOther = snapshot with
        {
            ClaimedBy = "worker-2",
            ClaimedAt = DateTimeOffset.UtcNow,
            LastHeartbeatAt = DateTimeOffset.UtcNow
        };

        var jobStore = Substitute.For<IExecutionJobStore>().WithTrySet();
        jobStore.ListActiveAsync(kind: null, cancellationToken: Arg.Any<CancellationToken>())
            .Returns([snapshot]);
        jobStore.GetAsync(snapshot.OperationId, Arg.Any<CancellationToken>())
            .Returns(reclaimedByOther);

        var jobQueue = Substitute.For<IJobQueue>();
        var claimReconciler = Substitute.For<IQueueClaimReconciler>();

        var service = new JobReconciliationService(
            jobStore, jobQueue, claimReconciler, new ExecutionJobCancellationTokens(),
            Array.Empty<IJobTerminalCallback>(), null, NullLogger<JobReconciliationService>.Instance);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await RunSingleSweepAsync(service, cts.Token);

        await jobStore.DidNotReceive().SetAsync(
            Arg.Is<ExecutionJobRecord>(j =>
                j.Status == ExecutionJobStatus.Failed || j.Status == ExecutionJobStatus.Queued),
            Arg.Any<TimeSpan?>(),
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Regression: once the fresh record has moved back to Queued, the
    /// reconciler must classify the snapshot as stale even if stale claim
    /// metadata is still present. Only actively claimed attempts are eligible
    /// for heartbeat expiry handling.
    /// </summary>
    [UnitTest]
    public async Task HeartbeatExpiry_LogsStale_WhenJobMovedBackToQueuedSinceSweep()
    {
        var snapshot = CreateRunningJob(
            heartbeatPolicy: new JobHeartbeatPolicy
            {
                Interval = TimeSpan.FromSeconds(1),
                Timeout = TimeSpan.FromSeconds(1)
            },
            timeoutPolicy: new JobTimeoutPolicy
            {
                MaxDuration = TimeSpan.FromHours(24)
            });

        var requeued = snapshot with
        {
            Status = ExecutionJobStatus.Queued,
            ClaimedAt = null,
            LastHeartbeatAt = null,
            CurrentPhase = "Requeued: Worker shutdown."
        };

        var jobStore = Substitute.For<IExecutionJobStore>().WithTrySet();
        jobStore.ListActiveAsync(kind: null, cancellationToken: Arg.Any<CancellationToken>())
            .Returns([snapshot]);
        jobStore.GetAsync(snapshot.OperationId, Arg.Any<CancellationToken>())
            .Returns(requeued);

        var jobQueue = Substitute.For<IJobQueue>();
        var claimReconciler = Substitute.For<IQueueClaimReconciler>();
        var logger = new ListLogger<JobReconciliationService>();

        var service = new JobReconciliationService(
            jobStore, jobQueue, claimReconciler, new ExecutionJobCancellationTokens(),
            Array.Empty<IJobTerminalCallback>(), null, logger);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await RunSingleSweepAsync(service, cts.Token);

        await jobStore.DidNotReceive().SetAsync(
            Arg.Any<ExecutionJobRecord>(),
            Arg.Any<TimeSpan?>(),
            Arg.Any<CancellationToken>());

        await jobQueue.DidNotReceive().RemoveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await jobQueue.DidNotReceive().RequeueAsync(
            Arg.Any<string>(), Arg.Any<OperationPriority>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>());

        Assert.Contains(logger.Entries, entry =>
            entry.EventId.Id == 9047 &&
            entry.Message.Contains(snapshot.OperationId, StringComparison.Ordinal) &&
            entry.Message.Contains("Queued", StringComparison.Ordinal));
        Assert.DoesNotContain(logger.Entries, entry => entry.EventId.Id == 9048);
    }

    [UnitTest]
    public async Task HeartbeatExpiry_SkipsTransition_WhenJobDeleted()
    {
        var snapshot = CreateRunningJob(
            heartbeatPolicy: new JobHeartbeatPolicy
            {
                Interval = TimeSpan.FromSeconds(1),
                Timeout = TimeSpan.FromSeconds(1)
            });

        var jobStore = Substitute.For<IExecutionJobStore>().WithTrySet();
        jobStore.ListActiveAsync(kind: null, cancellationToken: Arg.Any<CancellationToken>())
            .Returns([snapshot]);
        jobStore.GetAsync(snapshot.OperationId, Arg.Any<CancellationToken>())
            .Returns((ExecutionJobRecord?)null);

        var jobQueue = Substitute.For<IJobQueue>();
        var claimReconciler = Substitute.For<IQueueClaimReconciler>();

        var service = new JobReconciliationService(
            jobStore, jobQueue, claimReconciler, new ExecutionJobCancellationTokens(),
            Array.Empty<IJobTerminalCallback>(), null, NullLogger<JobReconciliationService>.Instance);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await RunSingleSweepAsync(service, cts.Token);

        await jobStore.DidNotReceive().SetAsync(
            Arg.Any<ExecutionJobRecord>(),
            Arg.Any<TimeSpan?>(),
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Regression: a heartbeat arriving between the sweep snapshot and the
    /// reconciler handler must prevent the transition — the worker is alive.
    /// </summary>
    [UnitTest]
    public async Task HeartbeatExpiry_SkipsTransition_WhenHeartbeatRefreshedSinceSweep()
    {
        var snapshot = CreateRunningJob(
            heartbeatPolicy: new JobHeartbeatPolicy
            {
                Interval = TimeSpan.FromSeconds(1),
                Timeout = TimeSpan.FromSeconds(1)
            },
            timeoutPolicy: new JobTimeoutPolicy
            {
                // Large timeout so only the heartbeat check fires.
                MaxDuration = TimeSpan.FromHours(24)
            });

        // The fresh record has a recent heartbeat — worker sent one after the snapshot.
        var refreshed = snapshot with
        {
            LastHeartbeatAt = DateTimeOffset.UtcNow
        };

        var jobStore = Substitute.For<IExecutionJobStore>().WithTrySet();
        jobStore.ListActiveAsync(kind: null, cancellationToken: Arg.Any<CancellationToken>())
            .Returns([snapshot]);
        jobStore.GetAsync(snapshot.OperationId, Arg.Any<CancellationToken>())
            .Returns(refreshed);

        var jobQueue = Substitute.For<IJobQueue>();
        var claimReconciler = Substitute.For<IQueueClaimReconciler>();

        var service = new JobReconciliationService(
            jobStore, jobQueue, claimReconciler, new ExecutionJobCancellationTokens(),
            Array.Empty<IJobTerminalCallback>(), null, NullLogger<JobReconciliationService>.Instance);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await RunSingleSweepAsync(service, cts.Token);

        // The reconciler must NOT requeue or fail the job.
        await jobStore.DidNotReceive().SetAsync(
            Arg.Is<ExecutionJobRecord>(j =>
                j.Status == ExecutionJobStatus.Failed || j.Status == ExecutionJobStatus.Queued),
            Arg.Any<TimeSpan?>(),
            Arg.Any<CancellationToken>());

        await jobQueue.DidNotReceive().RemoveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await jobQueue.DidNotReceive().RequeueAsync(
            Arg.Any<string>(), Arg.Any<OperationPriority>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>());
    }

    [UnitTest]
    public async Task HeartbeatExpiry_StillRetries_WhenJobRemainsActiveAndOwnedBySameWorker()
    {
        var snapshot = CreateRunningJob(
            retryPolicy: new JobRetryPolicy
            {
                MaxAttempts = 3,
                Strategy = BackoffStrategy.Fixed,
                BaseDelay = TimeSpan.FromSeconds(5),
                MaxDelay = TimeSpan.FromMinutes(1)
            },
            heartbeatPolicy: new JobHeartbeatPolicy
            {
                Interval = TimeSpan.FromSeconds(1),
                Timeout = TimeSpan.FromSeconds(1)
            },
            timeoutPolicy: new JobTimeoutPolicy
            {
                // Large timeout so only the heartbeat check fires.
                MaxDuration = TimeSpan.FromHours(24)
            });

        var jobStore = Substitute.For<IExecutionJobStore>().WithTrySet();
        jobStore.ListActiveAsync(kind: null, cancellationToken: Arg.Any<CancellationToken>())
            .Returns([snapshot]);
        // Re-read returns the same active record — no intervening completion.
        jobStore.GetAsync(snapshot.OperationId, Arg.Any<CancellationToken>())
            .Returns(snapshot);

        var jobQueue = Substitute.For<IJobQueue>();
        var claimReconciler = Substitute.For<IQueueClaimReconciler>();

        var service = new JobReconciliationService(
            jobStore, jobQueue, claimReconciler, new ExecutionJobCancellationTokens(),
            Array.Empty<IJobTerminalCallback>(), null, NullLogger<JobReconciliationService>.Instance);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await RunSingleSweepAsync(service, cts.Token);

        // The reconciler should proceed with the retry transition.
        await jobStore.Received(1).TrySetAsync(
            Arg.Is<ExecutionJobRecord>(j => j.Status == ExecutionJobStatus.Queued),
            Arg.Any<TimeSpan?>(),
            Arg.Any<CancellationToken>());

        await jobQueue.Received(1).RequeueAsync(
            snapshot.OperationId,
            Arg.Any<OperationPriority>(),
            Arg.Any<TimeSpan?>(),
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Regression: a job that timed out in the sweep snapshot but was requeued
    /// and reclaimed by the same worker with a fresh ClaimedAt must not be
    /// failed — the new attempt has not timed out.
    /// </summary>
    [UnitTest]
    public async Task TimeoutExpiry_SkipsTransition_WhenReclaimedBySameWorkerWithFreshClaimTime()
    {
        var timeoutPolicy = new JobTimeoutPolicy { MaxDuration = TimeSpan.FromSeconds(1) };

        // Snapshot: old ClaimedAt triggers timeout in the sweep.
        var snapshot = CreateRunningJob(
            claimedBy: "worker-1",
            timeoutPolicy: timeoutPolicy,
            heartbeatPolicy: new JobHeartbeatPolicy
            {
                Interval = TimeSpan.FromSeconds(30),
                Timeout = TimeSpan.FromHours(24)
            });
        snapshot = snapshot with { ClaimedAt = DateTimeOffset.UtcNow.AddMinutes(-10) };

        // Fresh record: same worker reclaimed with a recent ClaimedAt —
        // the new attempt has not timed out.
        var reclaimed = snapshot with
        {
            ClaimedAt = DateTimeOffset.UtcNow,
            AttemptCount = snapshot.AttemptCount + 1,
            LastHeartbeatAt = DateTimeOffset.UtcNow
        };

        var jobStore = Substitute.For<IExecutionJobStore>().WithTrySet();
        jobStore.ListActiveAsync(kind: null, cancellationToken: Arg.Any<CancellationToken>())
            .Returns([snapshot]);
        jobStore.GetAsync(snapshot.OperationId, Arg.Any<CancellationToken>())
            .Returns(reclaimed);

        var jobQueue = Substitute.For<IJobQueue>();
        var claimReconciler = Substitute.For<IQueueClaimReconciler>();

        var service = new JobReconciliationService(
            jobStore, jobQueue, claimReconciler, new ExecutionJobCancellationTokens(),
            Array.Empty<IJobTerminalCallback>(), null, NullLogger<JobReconciliationService>.Instance);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await RunSingleSweepAsync(service, cts.Token);

        // The reconciler must NOT fail the job.
        await jobStore.DidNotReceive().SetAsync(
            Arg.Is<ExecutionJobRecord>(j => j.Status == ExecutionJobStatus.Failed),
            Arg.Any<TimeSpan?>(),
            Arg.Any<CancellationToken>());

        await jobQueue.DidNotReceive().RemoveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Regression: once the fresh record has moved back to Queued, the timeout
    /// handler must classify the snapshot as stale rather than as a refreshed
    /// timeout window. Only actively claimed attempts are eligible for timeout
    /// expiry handling.
    /// </summary>
    [UnitTest]
    public async Task TimeoutExpiry_LogsStale_WhenJobMovedBackToQueuedSinceSweep()
    {
        var snapshot = CreateRunningJob(
            timeoutPolicy: new JobTimeoutPolicy { MaxDuration = TimeSpan.FromSeconds(1) });
        snapshot = snapshot with { ClaimedAt = DateTimeOffset.UtcNow.AddMinutes(-10) };

        var requeued = snapshot with
        {
            Status = ExecutionJobStatus.Queued,
            ClaimedAt = null,
            LastHeartbeatAt = null,
            CurrentPhase = "Requeued: Worker shutdown."
        };

        var jobStore = Substitute.For<IExecutionJobStore>().WithTrySet();
        jobStore.ListActiveAsync(kind: null, cancellationToken: Arg.Any<CancellationToken>())
            .Returns([snapshot]);
        jobStore.GetAsync(snapshot.OperationId, Arg.Any<CancellationToken>())
            .Returns(requeued);

        var jobQueue = Substitute.For<IJobQueue>();
        var claimReconciler = Substitute.For<IQueueClaimReconciler>();
        var logger = new ListLogger<JobReconciliationService>();

        var service = new JobReconciliationService(
            jobStore, jobQueue, claimReconciler, new ExecutionJobCancellationTokens(),
            Array.Empty<IJobTerminalCallback>(), null, logger);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await RunSingleSweepAsync(service, cts.Token);

        await jobStore.DidNotReceive().SetAsync(
            Arg.Any<ExecutionJobRecord>(),
            Arg.Any<TimeSpan?>(),
            Arg.Any<CancellationToken>());

        await jobQueue.DidNotReceive().RemoveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await jobQueue.DidNotReceive().RequeueAsync(
            Arg.Any<string>(), Arg.Any<OperationPriority>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>());

        Assert.Contains(logger.Entries, entry =>
            entry.EventId.Id == 9047 &&
            entry.Message.Contains(snapshot.OperationId, StringComparison.Ordinal) &&
            entry.Message.Contains("Queued", StringComparison.Ordinal));
        Assert.DoesNotContain(logger.Entries, entry => entry.EventId.Id == 9049);
    }

    /// <summary>
    /// Regression: when the reconciler requeues a job after heartbeat expiry,
    /// any stale CTS left by the previous worker must be revoked so that a
    /// subsequent Cancel() call returns false and the API caller writes
    /// Cancelled directly instead of delegating to a stale worker.
    /// </summary>
    [UnitTest]
    public async Task HeartbeatExpiry_Requeue_RevokesStaleWorkerCancellationToken()
    {
        var snapshot = CreateRunningJob(
            retryPolicy: new JobRetryPolicy
            {
                MaxAttempts = 3,
                Strategy = BackoffStrategy.Fixed,
                BaseDelay = TimeSpan.FromSeconds(5),
                MaxDelay = TimeSpan.FromMinutes(1)
            },
            heartbeatPolicy: new JobHeartbeatPolicy
            {
                Interval = TimeSpan.FromSeconds(1),
                Timeout = TimeSpan.FromSeconds(1)
            },
            timeoutPolicy: new JobTimeoutPolicy
            {
                MaxDuration = TimeSpan.FromHours(24)
            });

        var jobStore = Substitute.For<IExecutionJobStore>().WithTrySet();
        jobStore.ListActiveAsync(kind: null, cancellationToken: Arg.Any<CancellationToken>())
            .Returns([snapshot]);
        jobStore.GetAsync(snapshot.OperationId, Arg.Any<CancellationToken>())
            .Returns(snapshot);

        var jobQueue = Substitute.For<IJobQueue>();
        var claimReconciler = Substitute.For<IQueueClaimReconciler>();

        // Simulate a stale worker CTS registered before the sweep.
        var cancellationTokens = new ExecutionJobCancellationTokens();
        using var staleCts = cancellationTokens.CreateLinkedTokenSource(
            snapshot.OperationId, snapshot.ClaimedBy!, CancellationToken.None);

        var service = new JobReconciliationService(
            jobStore, jobQueue, claimReconciler, cancellationTokens,
            Array.Empty<IJobTerminalCallback>(), null, NullLogger<JobReconciliationService>.Instance);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await RunSingleSweepAsync(service, cts.Token);

        // The stale CTS must have been cancelled by Revoke.
        Assert.True(staleCts.IsCancellationRequested);

        // A subsequent Cancel() must return false — the stale token was removed.
        Assert.False(cancellationTokens.Cancel(snapshot.OperationId));
    }

    /// <summary>
    /// Regression: when the reconciler terminally fails a job after heartbeat
    /// expiry (no retries remaining), the stale CTS must also be revoked.
    /// </summary>
    [UnitTest]
    public async Task HeartbeatExpiry_TerminalFailure_RevokesStaleWorkerCancellationToken()
    {
        var snapshot = CreateRunningJob(
            retryPolicy: new JobRetryPolicy
            {
                MaxAttempts = 1,
                Strategy = BackoffStrategy.Fixed,
                BaseDelay = TimeSpan.Zero,
                MaxDelay = TimeSpan.Zero
            },
            heartbeatPolicy: new JobHeartbeatPolicy
            {
                Interval = TimeSpan.FromSeconds(1),
                Timeout = TimeSpan.FromSeconds(1)
            },
            timeoutPolicy: new JobTimeoutPolicy
            {
                MaxDuration = TimeSpan.FromHours(24)
            });

        var jobStore = Substitute.For<IExecutionJobStore>().WithTrySet();
        jobStore.ListActiveAsync(kind: null, cancellationToken: Arg.Any<CancellationToken>())
            .Returns([snapshot]);
        jobStore.GetAsync(snapshot.OperationId, Arg.Any<CancellationToken>())
            .Returns(snapshot);

        var jobQueue = Substitute.For<IJobQueue>();
        var claimReconciler = Substitute.For<IQueueClaimReconciler>();

        var cancellationTokens = new ExecutionJobCancellationTokens();
        using var staleCts = cancellationTokens.CreateLinkedTokenSource(
            snapshot.OperationId, snapshot.ClaimedBy!, CancellationToken.None);

        var logStore = Substitute.For<IExecutionLogStore>();

        var service = new JobReconciliationService(
            jobStore, jobQueue, claimReconciler, cancellationTokens,
            Array.Empty<IJobTerminalCallback>(), logStore, NullLogger<JobReconciliationService>.Instance);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await RunSingleSweepAsync(service, cts.Token);

        Assert.True(staleCts.IsCancellationRequested);
        Assert.False(cancellationTokens.Cancel(snapshot.OperationId));

        // Terminal failure must set log retention for post-mortem access.
        await logStore.Received(1).SetRetentionAsync(
            snapshot.OperationId,
            Arg.Any<TimeSpan>(),
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Regression: when <c>RequeueAsync</c> fails after the store transition to
    /// Queued, the stale CTS must already be revoked so cancel paths do not
    /// delegate to a dead worker. The stale-claim reconciler repairs the queue.
    /// </summary>
    [UnitTest]
    public async Task HeartbeatExpiry_Requeue_RevokesCtBeforeRequeueAsync_SoRequeueFailureDoesNotLeakStaleCts()
    {
        var snapshot = CreateRunningJob(
            retryPolicy: new JobRetryPolicy
            {
                MaxAttempts = 3,
                Strategy = BackoffStrategy.Fixed,
                BaseDelay = TimeSpan.FromSeconds(5),
                MaxDelay = TimeSpan.FromMinutes(1)
            },
            heartbeatPolicy: new JobHeartbeatPolicy
            {
                Interval = TimeSpan.FromSeconds(1),
                Timeout = TimeSpan.FromSeconds(1)
            },
            timeoutPolicy: new JobTimeoutPolicy
            {
                MaxDuration = TimeSpan.FromHours(24)
            });

        var jobStore = Substitute.For<IExecutionJobStore>().WithTrySet();
        jobStore.ListActiveAsync(kind: null, cancellationToken: Arg.Any<CancellationToken>())
            .Returns([snapshot]);
        jobStore.GetAsync(snapshot.OperationId, Arg.Any<CancellationToken>())
            .Returns(snapshot);

        var jobQueue = Substitute.For<IJobQueue>();
        jobQueue.RequeueAsync(
            Arg.Any<string>(), Arg.Any<OperationPriority>(),
            Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new InvalidOperationException("Simulated Redis failure"));

        var claimReconciler = Substitute.For<IQueueClaimReconciler>();

        var cancellationTokens = new ExecutionJobCancellationTokens();
        using var staleCts = cancellationTokens.CreateLinkedTokenSource(
            snapshot.OperationId, snapshot.ClaimedBy!, CancellationToken.None);

        var service = new JobReconciliationService(
            jobStore, jobQueue, claimReconciler, cancellationTokens,
            Array.Empty<IJobTerminalCallback>(), null, NullLogger<JobReconciliationService>.Instance);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        // The sweep propagates the RequeueAsync exception, but the CTS
        // must already be revoked before that exception fires.
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            RunSingleSweepAsync(service, cts.Token));

        Assert.True(staleCts.IsCancellationRequested);
        Assert.False(cancellationTokens.Cancel(snapshot.OperationId));
    }

    /// <summary>
    /// Regression: when <c>RemoveAsync</c> fails after the store transition to
    /// Failed (timeout), the stale CTS must already be revoked and the terminal
    /// callback must still fire despite the queue failure.
    /// </summary>
    [UnitTest]
    public async Task TimeoutExpiry_RevokesCtsAndNotifiesCallback_WhenQueueRemovalFails()
    {
        var snapshot = CreateRunningJob(
            timeoutPolicy: new JobTimeoutPolicy { MaxDuration = TimeSpan.FromSeconds(1) });
        snapshot = snapshot with { ClaimedAt = DateTimeOffset.UtcNow.AddMinutes(-10) };

        var jobStore = Substitute.For<IExecutionJobStore>().WithTrySet();
        jobStore.ListActiveAsync(kind: null, cancellationToken: Arg.Any<CancellationToken>())
            .Returns([snapshot]);
        jobStore.GetAsync(snapshot.OperationId, Arg.Any<CancellationToken>())
            .Returns(snapshot);

        var jobQueue = Substitute.For<IJobQueue>();
        jobQueue.RemoveAsync(
            Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new InvalidOperationException("Simulated Redis failure"));

        var claimReconciler = Substitute.For<IQueueClaimReconciler>();

        var cancellationTokens = new ExecutionJobCancellationTokens();
        using var staleCts = cancellationTokens.CreateLinkedTokenSource(
            snapshot.OperationId, snapshot.ClaimedBy!, CancellationToken.None);

        var terminalCallback = Substitute.For<IJobTerminalCallback>();

        var service = new JobReconciliationService(
            jobStore, jobQueue, claimReconciler, cancellationTokens,
            [terminalCallback], null, NullLogger<JobReconciliationService>.Instance);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await RunSingleSweepAsync(service, cts.Token);

        Assert.True(staleCts.IsCancellationRequested);
        Assert.False(cancellationTokens.Cancel(snapshot.OperationId));

        await terminalCallback.Received(1).OnTerminalAsync(
            Arg.Is<ExecutionJobRecord>(j => j.Status == ExecutionJobStatus.Failed),
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Regression: when a durable cancellation signal (CancellationRequestedAt) is
    /// set on a job whose heartbeat has expired, the reconciler must transition to
    /// Cancelled instead of retrying, honouring the operator's cancellation intent.
    /// </summary>
    [UnitTest]
    public async Task HeartbeatExpiry_HonoursDurableCancellationSignal_TransitionsToCancelled()
    {
        var snapshot = CreateRunningJob(
            retryPolicy: new JobRetryPolicy
            {
                MaxAttempts = 3,
                Strategy = BackoffStrategy.Fixed,
                BaseDelay = TimeSpan.FromSeconds(5),
                MaxDelay = TimeSpan.FromMinutes(1)
            },
            heartbeatPolicy: new JobHeartbeatPolicy
            {
                Interval = TimeSpan.FromSeconds(1),
                Timeout = TimeSpan.FromSeconds(1)
            },
            timeoutPolicy: new JobTimeoutPolicy
            {
                MaxDuration = TimeSpan.FromHours(24)
            }) with
        {
            CancellationRequestedAt = DateTimeOffset.UtcNow.AddSeconds(-5)
        };

        var jobStore = Substitute.For<IExecutionJobStore>().WithTrySet();
        jobStore.ListActiveAsync(kind: null, cancellationToken: Arg.Any<CancellationToken>())
            .Returns([snapshot]);
        jobStore.GetAsync(snapshot.OperationId, Arg.Any<CancellationToken>())
            .Returns(snapshot);

        var jobQueue = Substitute.For<IJobQueue>();
        var claimReconciler = Substitute.For<IQueueClaimReconciler>();

        var service = new JobReconciliationService(
            jobStore, jobQueue, claimReconciler, new ExecutionJobCancellationTokens(),
            Array.Empty<IJobTerminalCallback>(), null, NullLogger<JobReconciliationService>.Instance);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await RunSingleSweepAsync(service, cts.Token);

        await jobStore.Received(1).TrySetAsync(
            Arg.Is<ExecutionJobRecord>(j => j.Status == ExecutionJobStatus.Cancelled),
            Arg.Any<TimeSpan?>(),
            Arg.Any<CancellationToken>());

        await jobQueue.DidNotReceive().RequeueAsync(
            Arg.Any<string>(), Arg.Any<OperationPriority>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>());

        await jobQueue.Received(1).RemoveAsync(snapshot.OperationId, Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Regression: when queue removal fails after a heartbeat-expired terminal
    /// failure, the terminal callback must still fire so the admin progress store
    /// reflects the correct terminal state.
    /// </summary>
    [UnitTest]
    public async Task HeartbeatExpiry_TerminalFailure_NotifiesCallback_WhenQueueRemovalFails()
    {
        var snapshot = CreateRunningJob(
            retryPolicy: new JobRetryPolicy
            {
                MaxAttempts = 1,
                Strategy = BackoffStrategy.Fixed,
                BaseDelay = TimeSpan.Zero,
                MaxDelay = TimeSpan.Zero
            },
            heartbeatPolicy: new JobHeartbeatPolicy
            {
                Interval = TimeSpan.FromSeconds(1),
                Timeout = TimeSpan.FromSeconds(1)
            },
            timeoutPolicy: new JobTimeoutPolicy
            {
                MaxDuration = TimeSpan.FromHours(24)
            });

        var jobStore = Substitute.For<IExecutionJobStore>().WithTrySet();
        jobStore.ListActiveAsync(kind: null, cancellationToken: Arg.Any<CancellationToken>())
            .Returns([snapshot]);
        jobStore.GetAsync(snapshot.OperationId, Arg.Any<CancellationToken>())
            .Returns(snapshot);

        var jobQueue = Substitute.For<IJobQueue>();
        jobQueue.RemoveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new InvalidOperationException("Simulated Redis failure"));

        var claimReconciler = Substitute.For<IQueueClaimReconciler>();
        var terminalCallback = Substitute.For<IJobTerminalCallback>();

        var service = new JobReconciliationService(
            jobStore, jobQueue, claimReconciler, new ExecutionJobCancellationTokens(),
            [terminalCallback], null, NullLogger<JobReconciliationService>.Instance);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await RunSingleSweepAsync(service, cts.Token);

        await terminalCallback.Received(1).OnTerminalAsync(
            Arg.Is<ExecutionJobRecord>(j => j.Status == ExecutionJobStatus.Failed),
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Regression: when queue removal fails for a heartbeat-expired job honouring
    /// a durable cancellation signal, the terminal callback must still fire.
    /// </summary>
    [UnitTest]
    public async Task HeartbeatExpiry_DurableCancellation_NotifiesCallback_WhenQueueRemovalFails()
    {
        var snapshot = CreateRunningJob(
            retryPolicy: new JobRetryPolicy
            {
                MaxAttempts = 3,
                Strategy = BackoffStrategy.Fixed,
                BaseDelay = TimeSpan.FromSeconds(5),
                MaxDelay = TimeSpan.FromMinutes(1)
            },
            heartbeatPolicy: new JobHeartbeatPolicy
            {
                Interval = TimeSpan.FromSeconds(1),
                Timeout = TimeSpan.FromSeconds(1)
            },
            timeoutPolicy: new JobTimeoutPolicy
            {
                MaxDuration = TimeSpan.FromHours(24)
            }) with
        {
            CancellationRequestedAt = DateTimeOffset.UtcNow.AddSeconds(-5)
        };

        var jobStore = Substitute.For<IExecutionJobStore>().WithTrySet();
        jobStore.ListActiveAsync(kind: null, cancellationToken: Arg.Any<CancellationToken>())
            .Returns([snapshot]);
        jobStore.GetAsync(snapshot.OperationId, Arg.Any<CancellationToken>())
            .Returns(snapshot);

        var jobQueue = Substitute.For<IJobQueue>();
        jobQueue.RemoveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new InvalidOperationException("Simulated Redis failure"));

        var claimReconciler = Substitute.For<IQueueClaimReconciler>();
        var terminalCallback = Substitute.For<IJobTerminalCallback>();

        var service = new JobReconciliationService(
            jobStore, jobQueue, claimReconciler, new ExecutionJobCancellationTokens(),
            [terminalCallback], null, NullLogger<JobReconciliationService>.Instance);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await RunSingleSweepAsync(service, cts.Token);

        await terminalCallback.Received(1).OnTerminalAsync(
            Arg.Is<ExecutionJobRecord>(j => j.Status == ExecutionJobStatus.Cancelled),
            Arg.Any<CancellationToken>());
    }

    [UnitTest]
    public async Task HeartbeatExpiry_TerminalFailure_NotifiesCallback_WhenLogRetentionFails()
    {
        var snapshot = CreateRunningJob(
            retryPolicy: new JobRetryPolicy
            {
                MaxAttempts = 1,
                Strategy = BackoffStrategy.Fixed,
                BaseDelay = TimeSpan.Zero,
                MaxDelay = TimeSpan.Zero
            },
            heartbeatPolicy: new JobHeartbeatPolicy
            {
                Interval = TimeSpan.FromSeconds(1),
                Timeout = TimeSpan.FromSeconds(1)
            },
            timeoutPolicy: new JobTimeoutPolicy
            {
                MaxDuration = TimeSpan.FromHours(24)
            });

        var jobStore = Substitute.For<IExecutionJobStore>().WithTrySet();
        jobStore.ListActiveAsync(kind: null, cancellationToken: Arg.Any<CancellationToken>())
            .Returns([snapshot]);
        jobStore.GetAsync(snapshot.OperationId, Arg.Any<CancellationToken>())
            .Returns(snapshot);

        var jobQueue = Substitute.For<IJobQueue>();
        var claimReconciler = Substitute.For<IQueueClaimReconciler>();
        var terminalCallback = Substitute.For<IJobTerminalCallback>();

        var logStore = Substitute.For<IExecutionLogStore>();
        logStore.SetRetentionAsync(Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new InvalidOperationException("Simulated Redis failure"));

        var service = new JobReconciliationService(
            jobStore, jobQueue, claimReconciler, new ExecutionJobCancellationTokens(),
            [terminalCallback], logStore, NullLogger<JobReconciliationService>.Instance);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await RunSingleSweepAsync(service, cts.Token);

        await terminalCallback.Received(1).OnTerminalAsync(
            Arg.Is<ExecutionJobRecord>(j => j.Status == ExecutionJobStatus.Failed),
            Arg.Any<CancellationToken>());
    }

    [UnitTest]
    public async Task TimeoutExpiry_NotifiesCallback_WhenLogRetentionFails()
    {
        var snapshot = CreateRunningJob(
            timeoutPolicy: new JobTimeoutPolicy { MaxDuration = TimeSpan.FromSeconds(1) });
        snapshot = snapshot with { ClaimedAt = DateTimeOffset.UtcNow.AddMinutes(-10) };

        var jobStore = Substitute.For<IExecutionJobStore>().WithTrySet();
        jobStore.ListActiveAsync(kind: null, cancellationToken: Arg.Any<CancellationToken>())
            .Returns([snapshot]);
        jobStore.GetAsync(snapshot.OperationId, Arg.Any<CancellationToken>())
            .Returns(snapshot);

        var jobQueue = Substitute.For<IJobQueue>();
        var claimReconciler = Substitute.For<IQueueClaimReconciler>();
        var terminalCallback = Substitute.For<IJobTerminalCallback>();

        var logStore = Substitute.For<IExecutionLogStore>();
        logStore.SetRetentionAsync(Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new InvalidOperationException("Simulated Redis failure"));

        var service = new JobReconciliationService(
            jobStore, jobQueue, claimReconciler, new ExecutionJobCancellationTokens(),
            [terminalCallback], logStore, NullLogger<JobReconciliationService>.Instance);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await RunSingleSweepAsync(service, cts.Token);

        await terminalCallback.Received(1).OnTerminalAsync(
            Arg.Is<ExecutionJobRecord>(j => j.Status == ExecutionJobStatus.Failed),
            Arg.Any<CancellationToken>());
    }

    [UnitTest]
    public async Task HeartbeatExpiry_DurableCancellation_NotifiesCallback_WhenLogRetentionFails()
    {
        var snapshot = CreateRunningJob(
            heartbeatPolicy: new JobHeartbeatPolicy
            {
                Interval = TimeSpan.FromSeconds(1),
                Timeout = TimeSpan.FromSeconds(1)
            },
            timeoutPolicy: new JobTimeoutPolicy
            {
                MaxDuration = TimeSpan.FromHours(24)
            }) with
        {
            CancellationRequestedAt = DateTimeOffset.UtcNow.AddSeconds(-5)
        };

        var jobStore = Substitute.For<IExecutionJobStore>().WithTrySet();
        jobStore.ListActiveAsync(kind: null, cancellationToken: Arg.Any<CancellationToken>())
            .Returns([snapshot]);
        jobStore.GetAsync(snapshot.OperationId, Arg.Any<CancellationToken>())
            .Returns(snapshot);

        var jobQueue = Substitute.For<IJobQueue>();
        var claimReconciler = Substitute.For<IQueueClaimReconciler>();
        var terminalCallback = Substitute.For<IJobTerminalCallback>();

        var logStore = Substitute.For<IExecutionLogStore>();
        logStore.SetRetentionAsync(Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new InvalidOperationException("Simulated Redis failure"));

        var service = new JobReconciliationService(
            jobStore, jobQueue, claimReconciler, new ExecutionJobCancellationTokens(),
            [terminalCallback], logStore, NullLogger<JobReconciliationService>.Instance);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await RunSingleSweepAsync(service, cts.Token);

        await terminalCallback.Received(1).OnTerminalAsync(
            Arg.Is<ExecutionJobRecord>(j => j.Status == ExecutionJobStatus.Cancelled),
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Regression: a durable cancellation signal that arrives between the initial
    /// cancellation check and the retry write must be caught by the pre-retry
    /// re-read so the reconciler cancels instead of requeueing.
    /// </summary>
    [UnitTest]
    public async Task HeartbeatExpiry_HonoursCancellation_WhenSignalArrivesBeforeRetryWrite()
    {
        var snapshot = CreateRunningJob(
            heartbeatPolicy: new JobHeartbeatPolicy
            {
                Interval = TimeSpan.FromSeconds(1),
                Timeout = TimeSpan.FromSeconds(1)
            },
            timeoutPolicy: new JobTimeoutPolicy
            {
                MaxDuration = TimeSpan.FromHours(24)
            },
            retryPolicy: new JobRetryPolicy
            {
                MaxAttempts = 3,
                Strategy = BackoffStrategy.Fixed,
                BaseDelay = TimeSpan.FromSeconds(1),
                MaxDelay = TimeSpan.FromMinutes(1)
            });

        var withCancel = snapshot with
        {
            CancellationRequestedAt = DateTimeOffset.UtcNow
        };

        var callCount = 0;
        var jobStore = Substitute.For<IExecutionJobStore>().WithTrySet();
        jobStore.ListActiveAsync(kind: null, cancellationToken: Arg.Any<CancellationToken>())
            .Returns([snapshot]);
        jobStore.GetAsync(snapshot.OperationId, Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                callCount++;
                // Read 1: initial re-read (no cancel). Read 2+: pre-retry re-read (cancel present).
                return callCount <= 1 ? snapshot : withCancel;
            });

        var jobQueue = Substitute.For<IJobQueue>();
        var claimReconciler = Substitute.For<IQueueClaimReconciler>();

        var service = new JobReconciliationService(
            jobStore, jobQueue, claimReconciler, new ExecutionJobCancellationTokens(),
            Array.Empty<IJobTerminalCallback>(), null, NullLogger<JobReconciliationService>.Instance);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await RunSingleSweepAsync(service, cts.Token);

        await jobStore.Received().TrySetAsync(
            Arg.Is<ExecutionJobRecord>(j => j.Status == ExecutionJobStatus.Cancelled),
            Arg.Any<TimeSpan?>(),
            Arg.Any<CancellationToken>());

        await jobQueue.DidNotReceive().RequeueAsync(
            Arg.Any<string>(), Arg.Any<OperationPriority>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Regression: a durable cancellation signal that arrives between the initial
    /// cancellation check and the terminal fail write (retries exhausted) must be
    /// caught by the pre-fail re-read so the reconciler cancels instead of failing.
    /// </summary>
    [UnitTest]
    public async Task HeartbeatExpiry_HonoursCancellation_WhenSignalArrivesBeforeFailWrite()
    {
        var snapshot = CreateRunningJob(
            heartbeatPolicy: new JobHeartbeatPolicy
            {
                Interval = TimeSpan.FromSeconds(1),
                Timeout = TimeSpan.FromSeconds(1)
            },
            timeoutPolicy: new JobTimeoutPolicy
            {
                MaxDuration = TimeSpan.FromHours(24)
            },
            retryPolicy: new JobRetryPolicy
            {
                MaxAttempts = 1,
                Strategy = BackoffStrategy.Fixed,
                BaseDelay = TimeSpan.Zero,
                MaxDelay = TimeSpan.Zero
            });

        var withCancel = snapshot with
        {
            CancellationRequestedAt = DateTimeOffset.UtcNow
        };

        var callCount = 0;
        var jobStore = Substitute.For<IExecutionJobStore>().WithTrySet();
        jobStore.ListActiveAsync(kind: null, cancellationToken: Arg.Any<CancellationToken>())
            .Returns([snapshot]);
        jobStore.GetAsync(snapshot.OperationId, Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                callCount++;
                // Read 1: initial re-read (no cancel). Read 2+: pre-fail re-read (cancel present).
                return callCount <= 1 ? snapshot : withCancel;
            });

        var jobQueue = Substitute.For<IJobQueue>();
        var claimReconciler = Substitute.For<IQueueClaimReconciler>();

        var service = new JobReconciliationService(
            jobStore, jobQueue, claimReconciler, new ExecutionJobCancellationTokens(),
            Array.Empty<IJobTerminalCallback>(), null, NullLogger<JobReconciliationService>.Instance);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await RunSingleSweepAsync(service, cts.Token);

        await jobStore.Received().TrySetAsync(
            Arg.Is<ExecutionJobRecord>(j => j.Status == ExecutionJobStatus.Cancelled),
            Arg.Any<TimeSpan?>(),
            Arg.Any<CancellationToken>());

        await jobStore.DidNotReceive().TrySetAsync(
            Arg.Is<ExecutionJobRecord>(j => j.Status == ExecutionJobStatus.Failed),
            Arg.Any<TimeSpan?>(),
            Arg.Any<CancellationToken>());
    }

    [UnitTest]
    public async Task HeartbeatExpiry_Retry_EmitsTransitionTelemetry()
    {
        var snapshot = CreateRunningJob(
            retryPolicy: new JobRetryPolicy
            {
                MaxAttempts = 3,
                Strategy = BackoffStrategy.Fixed,
                BaseDelay = TimeSpan.FromSeconds(5),
                MaxDelay = TimeSpan.FromMinutes(1)
            },
            heartbeatPolicy: new JobHeartbeatPolicy
            {
                Interval = TimeSpan.FromSeconds(1),
                Timeout = TimeSpan.FromSeconds(1)
            },
            timeoutPolicy: new JobTimeoutPolicy
            {
                MaxDuration = TimeSpan.FromHours(24)
            });

        var jobStore = Substitute.For<IExecutionJobStore>().WithTrySet();
        jobStore.ListActiveAsync(kind: null, cancellationToken: Arg.Any<CancellationToken>())
            .Returns([snapshot]);
        jobStore.GetAsync(snapshot.OperationId, Arg.Any<CancellationToken>())
            .Returns(snapshot);

        var jobQueue = Substitute.For<IJobQueue>();
        var claimReconciler = Substitute.For<IQueueClaimReconciler>();

        var service = new JobReconciliationService(
            jobStore, jobQueue, claimReconciler, new ExecutionJobCancellationTokens(),
            Array.Empty<IJobTerminalCallback>(), null, NullLogger<JobReconciliationService>.Instance);

        var transitions = new List<MeasurementSample>();
        using var listener = CreateTransitionListener(transitions);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await RunSingleSweepAsync(service, cts.Token);

        listener.RecordObservableInstruments();
        Assert.Contains(transitions, sample =>
            GetTagString(sample.Tags, "honua.controlplane.execution.previous_status") == "Running" &&
            GetTagString(sample.Tags, "honua.controlplane.execution.status") == "Queued");
    }

    [UnitTest]
    public async Task TimeoutExpiry_Fail_EmitsTransitionTelemetry()
    {
        var snapshot = CreateRunningJob(
            timeoutPolicy: new JobTimeoutPolicy { MaxDuration = TimeSpan.FromSeconds(1) },
            heartbeatPolicy: new JobHeartbeatPolicy
            {
                Interval = TimeSpan.FromSeconds(30),
                Timeout = TimeSpan.FromHours(24)
            });
        snapshot = snapshot with { ClaimedAt = DateTimeOffset.UtcNow.AddMinutes(-10) };

        var jobStore = Substitute.For<IExecutionJobStore>().WithTrySet();
        jobStore.ListActiveAsync(kind: null, cancellationToken: Arg.Any<CancellationToken>())
            .Returns([snapshot]);
        jobStore.GetAsync(snapshot.OperationId, Arg.Any<CancellationToken>())
            .Returns(snapshot);

        var jobQueue = Substitute.For<IJobQueue>();
        var claimReconciler = Substitute.For<IQueueClaimReconciler>();

        var service = new JobReconciliationService(
            jobStore, jobQueue, claimReconciler, new ExecutionJobCancellationTokens(),
            Array.Empty<IJobTerminalCallback>(), null, NullLogger<JobReconciliationService>.Instance);

        var transitions = new List<MeasurementSample>();
        using var listener = CreateTransitionListener(transitions);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await RunSingleSweepAsync(service, cts.Token);

        listener.RecordObservableInstruments();
        Assert.Contains(transitions, sample =>
            GetTagString(sample.Tags, "honua.controlplane.execution.previous_status") == "Running" &&
            GetTagString(sample.Tags, "honua.controlplane.execution.status") == "Failed");
    }

    private sealed record MeasurementSample(long Value, KeyValuePair<string, object?>[] Tags);

    private static MeterListener CreateTransitionListener(List<MeasurementSample> samples)
    {
        // Bind to the production counter instance so this listener cannot drift from the
        // instrument name registered by ControlPlaneTelemetry.
        var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == HonuaTelemetry.ServiceName
                    && instrument.Name == ControlPlaneTelemetry.Metrics.ExecutionJobTransitions)
                {
                    l.EnableMeasurementEvents(instrument);
                }
            }
        };
        listener.SetMeasurementEventCallback<long>((_, measurement, tags, _) =>
        {
            lock (samples)
            {
                samples.Add(new MeasurementSample(measurement, tags.ToArray()));
            }
        });
        listener.Start();
        return listener;
    }

    private static string? GetTagString(KeyValuePair<string, object?>[] tags, string name)
    {
        foreach (var tag in tags)
        {
            if (tag.Key == name)
            {
                return tag.Value?.ToString();
            }
        }

        return null;
    }

    /// <summary>
    /// Invokes the reconciler's sweep logic once without running the full
    /// BackgroundService loop. Uses reflection to call the private method
    /// since the service is internal and sealed.
    /// </summary>
    private static async Task RunSingleSweepAsync(
        JobReconciliationService service,
        CancellationToken cancellationToken)
    {
        var method = typeof(JobReconciliationService).GetMethod(
            "SweepActiveJobsAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

        var task = (Task)method!.Invoke(service, [cancellationToken])!;
        await task.ConfigureAwait(false);
    }

    private sealed class ListLogger<T> : ILogger<T>
    {
        public List<LogEntry> Entries { get; } = [];

        public IDisposable BeginScope<TState>(TState state)
            where TState : notnull
            => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Entries.Add(new LogEntry(logLevel, eventId, formatter(state, exception), exception));
        }
    }

    private sealed record LogEntry(LogLevel LogLevel, EventId EventId, string Message, Exception? Exception);

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();

        public void Dispose()
        {
        }
    }
}
