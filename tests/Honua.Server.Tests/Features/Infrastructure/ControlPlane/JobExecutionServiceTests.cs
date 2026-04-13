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
    /// transitions to Running, this worker must bail out.
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
