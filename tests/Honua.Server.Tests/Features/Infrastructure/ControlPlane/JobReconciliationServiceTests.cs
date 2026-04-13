// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Server.Features.Infrastructure.ControlPlane;
using Honua.TestKit.Attributes;
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

        var jobStore = Substitute.For<IExecutionJobStore>();
        jobStore.ListActiveAsync(kind: null, cancellationToken: Arg.Any<CancellationToken>())
            .Returns([snapshot]);
        jobStore.GetAsync(snapshot.OperationId, Arg.Any<CancellationToken>())
            .Returns(succeeded);

        var jobQueue = Substitute.For<IJobQueue>();
        var claimReconciler = Substitute.For<IQueueClaimReconciler>();

        var service = new JobReconciliationService(
            jobStore, jobQueue, claimReconciler,
            NullLogger<JobReconciliationService>.Instance);

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

        var jobStore = Substitute.For<IExecutionJobStore>();
        jobStore.ListActiveAsync(kind: null, cancellationToken: Arg.Any<CancellationToken>())
            .Returns([succeeded.Status == ExecutionJobStatus.Succeeded ? snapshot : snapshot]);
        jobStore.GetAsync(snapshot.OperationId, Arg.Any<CancellationToken>())
            .Returns(succeeded);

        var jobQueue = Substitute.For<IJobQueue>();
        var claimReconciler = Substitute.For<IQueueClaimReconciler>();

        var service = new JobReconciliationService(
            jobStore, jobQueue, claimReconciler,
            NullLogger<JobReconciliationService>.Instance);

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

        var jobStore = Substitute.For<IExecutionJobStore>();
        jobStore.ListActiveAsync(kind: null, cancellationToken: Arg.Any<CancellationToken>())
            .Returns([snapshot]);
        jobStore.GetAsync(snapshot.OperationId, Arg.Any<CancellationToken>())
            .Returns(reclaimedByOther);

        var jobQueue = Substitute.For<IJobQueue>();
        var claimReconciler = Substitute.For<IQueueClaimReconciler>();

        var service = new JobReconciliationService(
            jobStore, jobQueue, claimReconciler,
            NullLogger<JobReconciliationService>.Instance);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await RunSingleSweepAsync(service, cts.Token);

        await jobStore.DidNotReceive().SetAsync(
            Arg.Is<ExecutionJobRecord>(j =>
                j.Status == ExecutionJobStatus.Failed || j.Status == ExecutionJobStatus.Queued),
            Arg.Any<TimeSpan?>(),
            Arg.Any<CancellationToken>());
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

        var jobStore = Substitute.For<IExecutionJobStore>();
        jobStore.ListActiveAsync(kind: null, cancellationToken: Arg.Any<CancellationToken>())
            .Returns([snapshot]);
        jobStore.GetAsync(snapshot.OperationId, Arg.Any<CancellationToken>())
            .Returns((ExecutionJobRecord?)null);

        var jobQueue = Substitute.For<IJobQueue>();
        var claimReconciler = Substitute.For<IQueueClaimReconciler>();

        var service = new JobReconciliationService(
            jobStore, jobQueue, claimReconciler,
            NullLogger<JobReconciliationService>.Instance);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await RunSingleSweepAsync(service, cts.Token);

        await jobStore.DidNotReceive().SetAsync(
            Arg.Any<ExecutionJobRecord>(),
            Arg.Any<TimeSpan?>(),
            Arg.Any<CancellationToken>());
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

        var jobStore = Substitute.For<IExecutionJobStore>();
        jobStore.ListActiveAsync(kind: null, cancellationToken: Arg.Any<CancellationToken>())
            .Returns([snapshot]);
        // Re-read returns the same active record — no intervening completion.
        jobStore.GetAsync(snapshot.OperationId, Arg.Any<CancellationToken>())
            .Returns(snapshot);

        var jobQueue = Substitute.For<IJobQueue>();
        var claimReconciler = Substitute.For<IQueueClaimReconciler>();

        var service = new JobReconciliationService(
            jobStore, jobQueue, claimReconciler,
            NullLogger<JobReconciliationService>.Instance);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await RunSingleSweepAsync(service, cts.Token);

        // The reconciler should proceed with the retry transition.
        await jobStore.Received(1).SetAsync(
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
}
