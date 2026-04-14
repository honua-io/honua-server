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
/// Verifies that the execution service re-reads job state before promoting
/// a claimed job to Running, preventing resurrection of jobs that were
/// cancelled between claim and worker startup.
/// </summary>
[Collection("Unit")]
public sealed class JobExecutionServiceTests
{
    private static ExecutionJobRecord CreateProvisioningJob(
        string operationId = "job-1",
        string claimedBy = "worker-test")
    {
        var now = DateTimeOffset.UtcNow;
        return new ExecutionJobRecord
        {
            OperationId = operationId,
            Status = ExecutionJobStatus.Provisioning,
            CreatedAt = now.AddMinutes(-1),
            UpdatedAt = now,
            ClaimedBy = claimedBy,
            ClaimedAt = now,
            LastHeartbeatAt = now,
            AttemptCount = 1,
            Spec = new ExecutionJobSpec
            {
                Kind = ExecutionJobKind.Geoprocessing,
                TargetKind = BatchComputeTargetKind.KubernetesJob,
                Backend = "local",
                WorkloadName = "test"
            }
        };
    }

    /// <summary>
    /// Regression: a cancel arriving between TryClaimAsync and ProcessJobAsync
    /// must not be overwritten by the Running transition.
    /// </summary>
    [UnitTest]
    public async Task ProcessJob_SkipsExecution_WhenJobCancelledBeforeRunningTransition()
    {
        var provisioning = CreateProvisioningJob();
        var cancelled = provisioning with
        {
            Status = ExecutionJobStatus.Cancelled,
            CompletedAt = DateTimeOffset.UtcNow,
            ErrorMessage = "Cancelled by operator."
        };

        var jobStore = Substitute.For<IExecutionJobStore>();
        // First read returns Provisioning (for executor lookup); re-read returns Cancelled.
        jobStore.GetAsync(provisioning.OperationId, Arg.Any<CancellationToken>())
            .Returns(provisioning, cancelled);

        var jobQueue = Substitute.For<IJobQueue>();
        var executor = Substitute.For<IJobExecutor>();
        executor.Kind.Returns(ExecutionJobKind.Geoprocessing);

        var cancellationTokens = new ExecutionJobCancellationTokens();

        var service = new JobExecutionService(
            jobQueue, jobStore, [executor], cancellationTokens, null,
            NullLogger<JobExecutionService>.Instance);

        await InvokeProcessJobAsync(service, provisioning.OperationId, provisioning.ClaimedBy!);

        // The worker must NOT write a Running record.
        await jobStore.DidNotReceive().SetAsync(
            Arg.Is<ExecutionJobRecord>(j => j.Status == ExecutionJobStatus.Running),
            Arg.Any<TimeSpan?>(),
            Arg.Any<CancellationToken>());

        // The executor must NOT have been invoked.
        await executor.DidNotReceive().ExecuteAsync(
            Arg.Any<ExecutionJobRecord>(),
            Arg.Any<IJobExecutionContext>(),
            Arg.Any<CancellationToken>());

        // The stale queue entry should be cleaned up.
        await jobQueue.Received(1).RemoveAsync(provisioning.OperationId, Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Regression: if another worker reclaimed the job before this worker
    /// transitions to Running, this worker must bail out without deleting
    /// the queue entry that now belongs to the new owner.
    /// </summary>
    [UnitTest]
    public async Task ProcessJob_SkipsExecution_WhenClaimOwnerChangedBeforeRunningTransition()
    {
        var provisioning = CreateProvisioningJob(claimedBy: "worker-test");
        var reclaimedByOther = provisioning with
        {
            ClaimedBy = "worker-other",
            ClaimedAt = DateTimeOffset.UtcNow
        };

        var jobStore = Substitute.For<IExecutionJobStore>();
        // First read returns our claim; re-read shows a different owner.
        jobStore.GetAsync(provisioning.OperationId, Arg.Any<CancellationToken>())
            .Returns(provisioning, reclaimedByOther);

        var jobQueue = Substitute.For<IJobQueue>();
        var executor = Substitute.For<IJobExecutor>();
        executor.Kind.Returns(ExecutionJobKind.Geoprocessing);

        var cancellationTokens = new ExecutionJobCancellationTokens();

        var service = new JobExecutionService(
            jobQueue, jobStore, [executor], cancellationTokens, null,
            NullLogger<JobExecutionService>.Instance);

        await InvokeProcessJobAsync(service, provisioning.OperationId, "worker-test");

        await jobStore.DidNotReceive().SetAsync(
            Arg.Is<ExecutionJobRecord>(j => j.Status == ExecutionJobStatus.Running),
            Arg.Any<TimeSpan?>(),
            Arg.Any<CancellationToken>());

        await executor.DidNotReceive().ExecuteAsync(
            Arg.Any<ExecutionJobRecord>(),
            Arg.Any<IJobExecutionContext>(),
            Arg.Any<CancellationToken>());

        // The queue entry belongs to the new owner — must not be removed.
        await jobQueue.DidNotReceive().RemoveAsync(
            provisioning.OperationId, Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Regression: a requeued job (status=Queued, ClaimedBy=null) must not have
    /// its queue entry deleted by the stale worker that lost ownership.
    /// </summary>
    [UnitTest]
    public async Task ProcessJob_DoesNotRemoveQueue_WhenJobRequeuedBeforeRunningTransition()
    {
        var provisioning = CreateProvisioningJob(claimedBy: "worker-test");
        var requeued = provisioning with
        {
            Status = ExecutionJobStatus.Queued,
            ClaimedBy = null,
            ClaimedAt = null,
            LastHeartbeatAt = null,
            CurrentPhase = "Requeued: Worker shutdown."
        };

        var jobStore = Substitute.For<IExecutionJobStore>();
        jobStore.GetAsync(provisioning.OperationId, Arg.Any<CancellationToken>())
            .Returns(provisioning, requeued);

        var jobQueue = Substitute.For<IJobQueue>();
        var executor = Substitute.For<IJobExecutor>();
        executor.Kind.Returns(ExecutionJobKind.Geoprocessing);

        var cancellationTokens = new ExecutionJobCancellationTokens();

        var service = new JobExecutionService(
            jobQueue, jobStore, [executor], cancellationTokens, null,
            NullLogger<JobExecutionService>.Instance);

        await InvokeProcessJobAsync(service, provisioning.OperationId, "worker-test");

        // Execution must be skipped.
        await executor.DidNotReceive().ExecuteAsync(
            Arg.Any<ExecutionJobRecord>(),
            Arg.Any<IJobExecutionContext>(),
            Arg.Any<CancellationToken>());

        // The queue entry is the pending retry — must not be removed.
        await jobQueue.DidNotReceive().RemoveAsync(
            provisioning.OperationId, Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Regression: a stale worker's heartbeat pump must stop when
    /// the job has been reclaimed by another worker.
    /// </summary>
    [UnitTest]
    public async Task HeartbeatPump_Stops_WhenOwnershipChanges()
    {
        var now = DateTimeOffset.UtcNow;
        var running = CreateProvisioningJob(claimedBy: "worker-stale") with
        {
            Status = ExecutionJobStatus.Running
        };
        var reclaimed = running with
        {
            ClaimedBy = "worker-new",
            ClaimedAt = now
        };

        var jobStore = Substitute.For<IExecutionJobStore>();
        jobStore.GetAsync(running.OperationId, Arg.Any<CancellationToken>())
            .Returns(reclaimed);

        using var context = new JobExecutionContext(
            running.OperationId, "worker-stale", jobStore, null,
            new JobHeartbeatPolicy { Interval = TimeSpan.FromMilliseconds(10), Timeout = TimeSpan.FromSeconds(30) });

        // The pump should detect the ownership change and exit without writing.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await context.RunHeartbeatPumpAsync(cts.Token);

        await jobStore.DidNotReceive().SetAsync(
            Arg.Any<ExecutionJobRecord>(),
            Arg.Any<TimeSpan?>(),
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Regression: a stale worker's progress report must be silently
    /// dropped when ownership has moved to another worker.
    /// </summary>
    [UnitTest]
    public async Task ReportProgress_Skips_WhenOwnershipLost()
    {
        var running = CreateProvisioningJob(claimedBy: "worker-stale") with
        {
            Status = ExecutionJobStatus.Running
        };
        var reclaimed = running with
        {
            ClaimedBy = "worker-new",
            ClaimedAt = DateTimeOffset.UtcNow
        };

        var jobStore = Substitute.For<IExecutionJobStore>();
        jobStore.GetAsync(running.OperationId, Arg.Any<CancellationToken>())
            .Returns(reclaimed);

        using var context = new JobExecutionContext(
            running.OperationId, "worker-stale", jobStore, null,
            JobHeartbeatPolicy.Default);

        await context.ReportProgressAsync(50, "Processing", CancellationToken.None);

        await jobStore.DidNotReceive().SetAsync(
            Arg.Any<ExecutionJobRecord>(),
            Arg.Any<TimeSpan?>(),
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Regression: a stale worker's artifact publication must be silently
    /// dropped when ownership has moved to another worker.
    /// </summary>
    [UnitTest]
    public async Task PublishArtifact_Skips_WhenOwnershipLost()
    {
        var running = CreateProvisioningJob(claimedBy: "worker-stale") with
        {
            Status = ExecutionJobStatus.Running
        };
        var reclaimed = running with
        {
            ClaimedBy = "worker-new",
            ClaimedAt = DateTimeOffset.UtcNow
        };

        var jobStore = Substitute.For<IExecutionJobStore>();
        jobStore.GetAsync(running.OperationId, Arg.Any<CancellationToken>())
            .Returns(reclaimed);

        using var context = new JobExecutionContext(
            running.OperationId, "worker-stale", jobStore, null,
            JobHeartbeatPolicy.Default);

        await context.PublishArtifactAsync("s3://bucket/artifact.zip", CancellationToken.None);

        await jobStore.DidNotReceive().SetAsync(
            Arg.Any<ExecutionJobRecord>(),
            Arg.Any<TimeSpan?>(),
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Invokes the private ProcessJobAsync method via reflection.
    /// Follows the same test pattern used in <see cref="JobReconciliationServiceTests"/>.
    /// </summary>
    private static async Task InvokeProcessJobAsync(
        JobExecutionService service,
        string operationId,
        string workerId)
    {
        var method = typeof(JobExecutionService).GetMethod(
            "ProcessJobAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

        var task = (Task)method!.Invoke(service, [operationId, workerId, CancellationToken.None])!;
        await task.ConfigureAwait(false);
    }
}
