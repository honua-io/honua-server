// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics.Metrics;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Server.Features.Infrastructure.ControlPlane;
using Honua.Server.Tests.Helpers;
using Honua.ServiceDefaults;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Honua.Server.Tests.Features.Infrastructure.ControlPlane;

/// <summary>
/// Verifies that the execution service re-reads job state before promoting
/// a claimed job to Running, preventing resurrection of jobs that were
/// cancelled between claim and worker startup.
/// </summary>
[Collection("ControlPlaneTransitionTelemetry")]
public sealed class JobExecutionServiceTests
{
    private static ExecutionJobRecord CreateProvisioningJob(
        string operationId = "job-1",
        string claimedBy = "worker-test",
        string backend = "local")
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
                Backend = backend,
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

        var jobStore = Substitute.For<IExecutionJobStore>().WithTrySet();
        // First read returns Provisioning (for executor lookup); re-read returns Cancelled.
        jobStore.GetAsync(provisioning.OperationId, Arg.Any<CancellationToken>())
            .Returns(provisioning, cancelled);

        var jobQueue = Substitute.For<IJobQueue>();
        var executor = Substitute.For<IJobExecutor>();
        executor.Kind.Returns(ExecutionJobKind.Geoprocessing);

        var cancellationTokens = new ExecutionJobCancellationTokens();

        var service = new JobExecutionService(
            jobQueue, jobStore, [executor], cancellationTokens, Array.Empty<IJobTerminalCallback>(), null,
            NullLogger<JobExecutionService>.Instance);

        await InvokeProcessJobAsync(service, provisioning.OperationId, provisioning.ClaimedBy!);

        // The worker must NOT write a Running record.
        await jobStore.DidNotReceive().TrySetAsync(
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

        var jobStore = Substitute.For<IExecutionJobStore>().WithTrySet();
        // First read returns our claim; re-read shows a different owner.
        jobStore.GetAsync(provisioning.OperationId, Arg.Any<CancellationToken>())
            .Returns(provisioning, reclaimedByOther);

        var jobQueue = Substitute.For<IJobQueue>();
        var executor = Substitute.For<IJobExecutor>();
        executor.Kind.Returns(ExecutionJobKind.Geoprocessing);

        var cancellationTokens = new ExecutionJobCancellationTokens();

        var service = new JobExecutionService(
            jobQueue, jobStore, [executor], cancellationTokens, Array.Empty<IJobTerminalCallback>(), null,
            NullLogger<JobExecutionService>.Instance);

        await InvokeProcessJobAsync(service, provisioning.OperationId, "worker-test");

        await jobStore.DidNotReceive().TrySetAsync(
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

        var jobStore = Substitute.For<IExecutionJobStore>().WithTrySet();
        jobStore.GetAsync(provisioning.OperationId, Arg.Any<CancellationToken>())
            .Returns(provisioning, requeued);

        var jobQueue = Substitute.For<IJobQueue>();
        var executor = Substitute.For<IJobExecutor>();
        executor.Kind.Returns(ExecutionJobKind.Geoprocessing);

        var cancellationTokens = new ExecutionJobCancellationTokens();

        var service = new JobExecutionService(
            jobQueue, jobStore, [executor], cancellationTokens, Array.Empty<IJobTerminalCallback>(), null,
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

        var jobStore = Substitute.For<IExecutionJobStore>().WithTrySet();
        jobStore.GetAsync(running.OperationId, Arg.Any<CancellationToken>())
            .Returns(reclaimed);

        using var context = new JobExecutionContext(
            running.OperationId, "worker-stale", jobStore, null,
            new JobHeartbeatPolicy { Interval = TimeSpan.FromMilliseconds(10), Timeout = TimeSpan.FromSeconds(30) },
            null, NullLogger.Instance);

        // The pump should detect the ownership change and exit without writing.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await context.RunHeartbeatPumpAsync(cts.Token);

        await jobStore.DidNotReceive().TrySetAsync(
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

        var jobStore = Substitute.For<IExecutionJobStore>().WithTrySet();
        jobStore.GetAsync(running.OperationId, Arg.Any<CancellationToken>())
            .Returns(reclaimed);

        using var context = new JobExecutionContext(
            running.OperationId, "worker-stale", jobStore, null,
            JobHeartbeatPolicy.Default,
            null, NullLogger.Instance);

        await context.ReportProgressAsync(50, "Processing", CancellationToken.None);

        await jobStore.DidNotReceive().TrySetAsync(
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

        var jobStore = Substitute.For<IExecutionJobStore>().WithTrySet();
        jobStore.GetAsync(running.OperationId, Arg.Any<CancellationToken>())
            .Returns(reclaimed);

        using var context = new JobExecutionContext(
            running.OperationId, "worker-stale", jobStore, null,
            JobHeartbeatPolicy.Default,
            null, NullLogger.Instance);

        await context.PublishArtifactAsync("s3://bucket/artifact.zip", CancellationToken.None);

        await jobStore.DidNotReceive().TrySetAsync(
            Arg.Any<ExecutionJobRecord>(),
            Arg.Any<TimeSpan?>(),
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Regression: a progress update racing with a terminal or cancellation write
    /// must skip the stale write instead of clobbering the newer durable state.
    /// </summary>
    [UnitTest]
    public async Task ReportProgress_Skips_WhenTrySetConflicts()
    {
        var running = CreateProvisioningJob() with
        {
            Status = ExecutionJobStatus.Running
        };

        var jobStore = Substitute.For<IExecutionJobStore>().WithTrySet();
        jobStore.GetAsync(running.OperationId, Arg.Any<CancellationToken>())
            .Returns(running);
        jobStore.TrySetAsync(
                Arg.Any<ExecutionJobRecord>(),
                Arg.Any<TimeSpan?>(),
                Arg.Any<CancellationToken>())
            .Returns(false);

        using var context = new JobExecutionContext(
            running.OperationId, running.ClaimedBy!, jobStore, null,
            JobHeartbeatPolicy.Default,
            null, NullLogger.Instance);

        await context.ReportProgressAsync(50, "Processing", CancellationToken.None);

        await jobStore.Received(1).TrySetAsync(
            Arg.Is<ExecutionJobRecord>(job => job.Status == ExecutionJobStatus.Running),
            Arg.Any<TimeSpan?>(),
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Regression: artifact publication racing with a terminal or cancellation
    /// write must skip the stale durable update instead of reviving the old claim.
    /// </summary>
    [UnitTest]
    public async Task PublishArtifact_Skips_WhenTrySetConflicts()
    {
        var running = CreateProvisioningJob() with
        {
            Status = ExecutionJobStatus.Running
        };

        var jobStore = Substitute.For<IExecutionJobStore>().WithTrySet();
        jobStore.GetAsync(running.OperationId, Arg.Any<CancellationToken>())
            .Returns(running);
        jobStore.TrySetAsync(
                Arg.Any<ExecutionJobRecord>(),
                Arg.Any<TimeSpan?>(),
                Arg.Any<CancellationToken>())
            .Returns(false);

        using var context = new JobExecutionContext(
            running.OperationId, running.ClaimedBy!, jobStore, null,
            JobHeartbeatPolicy.Default,
            null, NullLogger.Instance);

        await context.PublishArtifactAsync("s3://bucket/artifact.zip", CancellationToken.None);

        await jobStore.Received(1).TrySetAsync(
            Arg.Is<ExecutionJobRecord>(job =>
                job.Status == ExecutionJobStatus.Running
                && job.ArtifactReferences.Contains("s3://bucket/artifact.zip")),
            Arg.Any<TimeSpan?>(),
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Regression: executor warnings must be persisted on the terminal failed
    /// record when no retries remain, so clients rendering job.Warnings see them.
    /// </summary>
    [UnitTest]
    public async Task ProcessJob_PersistsWarnings_WhenExecutorReturnsFailure_NoRetries()
    {
        var provisioning = CreateProvisioningJob() with
        {
            RetryPolicy = new JobRetryPolicy
            {
                MaxAttempts = 1,
                Strategy = BackoffStrategy.Fixed,
                BaseDelay = TimeSpan.Zero,
                MaxDelay = TimeSpan.Zero
            }
        };

        var jobStore = Substitute.For<IExecutionJobStore>().WithTrySet();
        // First read for executor lookup, second for CTS re-check, third for
        // Running transition read-back, fourth inside AbandonJobAsync re-read.
        jobStore.GetAsync(provisioning.OperationId, Arg.Any<CancellationToken>())
            .Returns(provisioning);

        var jobQueue = Substitute.For<IJobQueue>();
        jobQueue.TryClaimAsync(Arg.Any<string>(), Arg.Any<IReadOnlySet<ExecutionJobKind>>(), Arg.Any<IReadOnlySet<string>?>(), Arg.Any<CancellationToken>())
            .Returns(provisioning.OperationId, (string?)null);

        var executorWarnings = new List<string> { "Projection mismatch", "CRS fallback used" };
        var executor = Substitute.For<IJobExecutor>();
        executor.Kind.Returns(ExecutionJobKind.Geoprocessing);
        executor.ExecuteAsync(
                Arg.Any<ExecutionJobRecord>(),
                Arg.Any<IJobExecutionContext>(),
                Arg.Any<CancellationToken>())
            .Returns(JobExecutionResult.Failed("Transform failed", executorWarnings));

        var cancellationTokens = new ExecutionJobCancellationTokens();

        var service = new JobExecutionService(
            jobQueue, jobStore, [executor], cancellationTokens, Array.Empty<IJobTerminalCallback>(), null,
            NullLogger<JobExecutionService>.Instance);

        await InvokeProcessJobAsync(service, provisioning.OperationId, provisioning.ClaimedBy!);

        // The terminal failed record must carry the executor warnings.
        await jobStore.Received().TrySetAsync(
            Arg.Is<ExecutionJobRecord>(j =>
                j.Status == ExecutionJobStatus.Failed &&
                j.Warnings.Count == 2 &&
                j.Warnings[0] == "Projection mismatch"),
            Arg.Any<TimeSpan?>(),
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Regression: when retries are exhausted and the job transitions to terminal
    /// Failed, the execution log retention must be set so structured logs remain
    /// accessible for post-mortem inspection.
    /// </summary>
    [UnitTest]
    public async Task ProcessJob_SetsLogRetention_WhenRetriesExhausted()
    {
        var provisioning = CreateProvisioningJob() with
        {
            RetryPolicy = new JobRetryPolicy
            {
                MaxAttempts = 1,
                Strategy = BackoffStrategy.Fixed,
                BaseDelay = TimeSpan.Zero,
                MaxDelay = TimeSpan.Zero
            }
        };

        var jobStore = Substitute.For<IExecutionJobStore>().WithTrySet();
        jobStore.GetAsync(provisioning.OperationId, Arg.Any<CancellationToken>())
            .Returns(provisioning);

        var jobQueue = Substitute.For<IJobQueue>();
        jobQueue.TryClaimAsync(Arg.Any<string>(), Arg.Any<IReadOnlySet<ExecutionJobKind>>(), Arg.Any<IReadOnlySet<string>?>(), Arg.Any<CancellationToken>())
            .Returns(provisioning.OperationId, (string?)null);

        var executor = Substitute.For<IJobExecutor>();
        executor.Kind.Returns(ExecutionJobKind.Geoprocessing);
        executor.ExecuteAsync(
                Arg.Any<ExecutionJobRecord>(),
                Arg.Any<IJobExecutionContext>(),
                Arg.Any<CancellationToken>())
            .Returns(JobExecutionResult.Failed("Transform failed"));

        var cancellationTokens = new ExecutionJobCancellationTokens();
        var logStore = Substitute.For<IExecutionLogStore>();

        var service = new JobExecutionService(
            jobQueue, jobStore, [executor], cancellationTokens, Array.Empty<IJobTerminalCallback>(), logStore,
            NullLogger<JobExecutionService>.Instance);

        await InvokeProcessJobAsync(service, provisioning.OperationId, provisioning.ClaimedBy!);

        await logStore.Received(1).SetRetentionAsync(
            provisioning.OperationId,
            Arg.Any<TimeSpan>(),
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Regression: when executor returns failure with warnings and retries
    /// remain, warnings must be cleared from the requeued record to prevent
    /// stale per-attempt warnings from leaking to the next attempt.
    /// </summary>
    [UnitTest]
    public async Task ProcessJob_ClearsWarnings_WhenExecutorReturnsFailure_WithRetries()
    {
        var provisioning = CreateProvisioningJob() with
        {
            RetryPolicy = new JobRetryPolicy
            {
                MaxAttempts = 3,
                Strategy = BackoffStrategy.Fixed,
                BaseDelay = TimeSpan.FromSeconds(1),
                MaxDelay = TimeSpan.FromMinutes(1)
            }
        };

        var jobStore = Substitute.For<IExecutionJobStore>().WithTrySet();
        jobStore.GetAsync(provisioning.OperationId, Arg.Any<CancellationToken>())
            .Returns(provisioning);

        var jobQueue = Substitute.For<IJobQueue>();
        jobQueue.TryClaimAsync(Arg.Any<string>(), Arg.Any<IReadOnlySet<ExecutionJobKind>>(), Arg.Any<IReadOnlySet<string>?>(), Arg.Any<CancellationToken>())
            .Returns(provisioning.OperationId, (string?)null);

        var executor = Substitute.For<IJobExecutor>();
        executor.Kind.Returns(ExecutionJobKind.Geoprocessing);
        executor.ExecuteAsync(
                Arg.Any<ExecutionJobRecord>(),
                Arg.Any<IJobExecutionContext>(),
                Arg.Any<CancellationToken>())
            .Returns(JobExecutionResult.Failed("Transient failure", ["Warning A"]));

        var cancellationTokens = new ExecutionJobCancellationTokens();

        var service = new JobExecutionService(
            jobQueue, jobStore, [executor], cancellationTokens, Array.Empty<IJobTerminalCallback>(), null,
            NullLogger<JobExecutionService>.Instance);

        await InvokeProcessJobAsync(service, provisioning.OperationId, provisioning.ClaimedBy!);

        // The requeued record must have cleared warnings.
        await jobStore.Received().TrySetAsync(
            Arg.Is<ExecutionJobRecord>(j =>
                j.Status == ExecutionJobStatus.Queued &&
                j.Warnings.Count == 0),
            Arg.Any<TimeSpan?>(),
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Regression: a transient log-store failure during per-attempt warning
    /// persistence must not block the durable requeue transition. The job
    /// must still be requeued with cleared warnings.
    /// </summary>
    [UnitTest]
    public async Task ProcessJob_RequeuesJob_WhenWarningLogAppendThrows()
    {
        var provisioning = CreateProvisioningJob() with
        {
            RetryPolicy = new JobRetryPolicy
            {
                MaxAttempts = 3,
                Strategy = BackoffStrategy.Fixed,
                BaseDelay = TimeSpan.Zero,
                MaxDelay = TimeSpan.Zero
            }
        };

        var jobStore = Substitute.For<IExecutionJobStore>().WithTrySet();
        jobStore.GetAsync(provisioning.OperationId, Arg.Any<CancellationToken>())
            .Returns(provisioning);

        var jobQueue = Substitute.For<IJobQueue>();

        var executor = Substitute.For<IJobExecutor>();
        executor.Kind.Returns(ExecutionJobKind.Geoprocessing);
        executor.ExecuteAsync(
                Arg.Any<ExecutionJobRecord>(),
                Arg.Any<IJobExecutionContext>(),
                Arg.Any<CancellationToken>())
            .Returns(JobExecutionResult.Failed("Transient failure", ["Warning A", "Warning B"]));

        var logStore = Substitute.For<IExecutionLogStore>();
        logStore.AppendAsync(Arg.Any<string>(), Arg.Any<ExecutionLogEntry>(), Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new InvalidOperationException("Simulated log-store outage"));

        var cancellationTokens = new ExecutionJobCancellationTokens();

        var service = new JobExecutionService(
            jobQueue, jobStore, [executor], cancellationTokens, Array.Empty<IJobTerminalCallback>(), logStore,
            NullLogger<JobExecutionService>.Instance);

        await InvokeProcessJobAsync(service, provisioning.OperationId, provisioning.ClaimedBy!);

        // The job must still be requeued despite the log-store failure.
        await jobStore.Received().TrySetAsync(
            Arg.Is<ExecutionJobRecord>(j =>
                j.Status == ExecutionJobStatus.Queued &&
                j.Warnings.Count == 0),
            Arg.Any<TimeSpan?>(),
            Arg.Any<CancellationToken>());

        await jobQueue.Received().RequeueAsync(
            provisioning.OperationId,
            Arg.Any<OperationPriority>(),
            Arg.Any<TimeSpan?>(),
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Regression: a graceful host shutdown must always requeue the job, even
    /// when the retry budget is exhausted (<see cref="JobRetryPolicy.None"/>).
    /// Infrastructure shutdown is not an execution failure and must not
    /// permanently fail jobs that have no retry budget.
    /// </summary>
    [UnitTest]
    public async Task ProcessJob_RequeuesJob_WhenShutdownWithNoRetryPolicy()
    {
        var provisioning = CreateProvisioningJob() with
        {
            RetryPolicy = JobRetryPolicy.None
        };

        var jobStore = Substitute.For<IExecutionJobStore>().WithTrySet();
        jobStore.GetAsync(provisioning.OperationId, Arg.Any<CancellationToken>())
            .Returns(provisioning);

        var jobQueue = Substitute.For<IJobQueue>();
        var stoppingCts = new CancellationTokenSource();

        var executor = Substitute.For<IJobExecutor>();
        executor.Kind.Returns(ExecutionJobKind.Geoprocessing);
        executor.ExecuteAsync(
                Arg.Any<ExecutionJobRecord>(),
                Arg.Any<IJobExecutionContext>(),
                Arg.Any<CancellationToken>())
            .Returns(async callInfo =>
            {
                // Simulate host shutdown during execution.
                await stoppingCts.CancelAsync().ConfigureAwait(false);
                callInfo.Arg<CancellationToken>().ThrowIfCancellationRequested();
                return JobExecutionResult.Succeeded(); // Unreachable.
            });

        var cancellationTokens = new ExecutionJobCancellationTokens();

        var service = new JobExecutionService(
            jobQueue, jobStore, [executor], cancellationTokens, Array.Empty<IJobTerminalCallback>(), null,
            NullLogger<JobExecutionService>.Instance);

        await InvokeProcessJobAsync(service, provisioning.OperationId,
            provisioning.ClaimedBy!, stoppingCts.Token);

        // The job must be requeued (Queued), not permanently failed.
        await jobStore.Received().TrySetAsync(
            Arg.Is<ExecutionJobRecord>(j =>
                j.Status == ExecutionJobStatus.Queued &&
                j.ClaimedBy == null),
            Arg.Any<TimeSpan?>(),
            Arg.Any<CancellationToken>());

        await jobQueue.Received().RequeueAsync(
            provisioning.OperationId,
            Arg.Any<OperationPriority>(),
            Arg.Any<TimeSpan?>(),
            Arg.Any<CancellationToken>());

        // Must NOT have been terminally failed.
        await jobStore.DidNotReceive().TrySetAsync(
            Arg.Is<ExecutionJobRecord>(j => j.Status == ExecutionJobStatus.Failed),
            Arg.Any<TimeSpan?>(),
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Regression: when both shutdown and timeout fire concurrently, the
    /// timed-out job must fail terminally instead of being force-requeued.
    /// ADR-0031 says timed-out jobs are not retried.
    /// </summary>
    [UnitTest]
    public async Task ProcessJob_ShutdownAndTimeout_FailsTerminallyInsteadOfRequeue()
    {
        var provisioning = CreateProvisioningJob() with
        {
            TimeoutPolicy = new JobTimeoutPolicy { MaxDuration = TimeSpan.FromMilliseconds(50) },
            RetryPolicy = JobRetryPolicy.None
        };

        var jobStore = Substitute.For<IExecutionJobStore>().WithTrySet();
        jobStore.GetAsync(provisioning.OperationId, Arg.Any<CancellationToken>())
            .Returns(provisioning);

        var jobQueue = Substitute.For<IJobQueue>();
        var stoppingCts = new CancellationTokenSource();

        var executor = Substitute.For<IJobExecutor>();
        executor.Kind.Returns(ExecutionJobKind.Geoprocessing);
        executor.ExecuteAsync(
                Arg.Any<ExecutionJobRecord>(),
                Arg.Any<IJobExecutionContext>(),
                Arg.Any<CancellationToken>())
            .Returns(async callInfo =>
            {
                // Wait for the timeout to fire, then also trigger shutdown.
                await Task.Delay(TimeSpan.FromMilliseconds(100), CancellationToken.None).ConfigureAwait(false);
                await stoppingCts.CancelAsync().ConfigureAwait(false);
                callInfo.Arg<CancellationToken>().ThrowIfCancellationRequested();
                return JobExecutionResult.Succeeded(); // Unreachable.
            });

        var cancellationTokens = new ExecutionJobCancellationTokens();

        var service = new JobExecutionService(
            jobQueue, jobStore, [executor], cancellationTokens, Array.Empty<IJobTerminalCallback>(), null,
            NullLogger<JobExecutionService>.Instance);

        await InvokeProcessJobAsync(service, provisioning.OperationId,
            provisioning.ClaimedBy!, stoppingCts.Token);

        // The job must be terminally failed (timeout), not requeued.
        await jobStore.Received().TrySetAsync(
            Arg.Is<ExecutionJobRecord>(j => j.Status == ExecutionJobStatus.Failed),
            Arg.Any<TimeSpan?>(),
            Arg.Any<CancellationToken>());

        // Must NOT have been requeued.
        await jobStore.DidNotReceive().TrySetAsync(
            Arg.Is<ExecutionJobRecord>(j =>
                j.Status == ExecutionJobStatus.Queued &&
                j.ClaimedBy == null),
            Arg.Any<TimeSpan?>(),
            Arg.Any<CancellationToken>());

        await jobQueue.DidNotReceive().RequeueAsync(
            Arg.Any<string>(),
            Arg.Any<OperationPriority>(),
            Arg.Any<TimeSpan?>(),
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Regression: a stale worker's log append must be silently dropped
    /// when ownership has moved to another worker, preventing log pollution
    /// into the next attempt's or terminal state's log stream.
    /// </summary>
    [UnitTest]
    public async Task AppendLog_Skips_WhenOwnershipLost()
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

        var jobStore = Substitute.For<IExecutionJobStore>().WithTrySet();
        jobStore.GetAsync(running.OperationId, Arg.Any<CancellationToken>())
            .Returns(reclaimed);

        var logStore = Substitute.For<IExecutionLogStore>();

        using var context = new JobExecutionContext(
            running.OperationId, "worker-stale", jobStore, logStore,
            JobHeartbeatPolicy.Default,
            null, NullLogger.Instance);

        await context.AppendLogAsync(new ExecutionLogEntry
        {
            Timestamp = DateTimeOffset.UtcNow,
            Level = ExecutionLogLevel.Info,
            Message = "Stale log entry that should be dropped"
        }, CancellationToken.None);

        // The log store must NOT have been written to.
        await logStore.DidNotReceive().AppendAsync(
            Arg.Any<string>(),
            Arg.Any<ExecutionLogEntry>(),
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Regression: after a worker requeues a job on executor failure with retries
    /// remaining, the tracked CTS must be removed immediately so that a cancel
    /// request arriving in the window between requeue and ProcessJobAsync's finally
    /// block is not incorrectly delegated to a worker that no longer owns the job.
    /// Without the fix, Cancel() returns true in this window and the API caller
    /// trusts the worker to persist Cancelled — but the worker already dropped
    /// ownership, so the cancel is silently swallowed and the job stays Queued.
    /// </summary>
    [UnitTest]
    public async Task ProcessJob_RemovesCtsAfterRetryRequeue_SoCancelIsNotFalselyDelegated()
    {
        var provisioning = CreateProvisioningJob() with
        {
            RetryPolicy = new JobRetryPolicy
            {
                MaxAttempts = 3,
                Strategy = BackoffStrategy.Fixed,
                BaseDelay = TimeSpan.Zero,
                MaxDelay = TimeSpan.Zero
            }
        };

        var jobStore = Substitute.For<IExecutionJobStore>().WithTrySet();
        jobStore.GetAsync(provisioning.OperationId, Arg.Any<CancellationToken>())
            .Returns(provisioning);

        var jobQueue = Substitute.For<IJobQueue>();

        var executor = Substitute.For<IJobExecutor>();
        executor.Kind.Returns(ExecutionJobKind.Geoprocessing);
        executor.ExecuteAsync(
                Arg.Any<ExecutionJobRecord>(),
                Arg.Any<IJobExecutionContext>(),
                Arg.Any<CancellationToken>())
            .Returns(JobExecutionResult.Failed("Transient failure"));

        var cancellationTokens = new ExecutionJobCancellationTokens();

        var service = new JobExecutionService(
            jobQueue, jobStore, [executor], cancellationTokens, Array.Empty<IJobTerminalCallback>(), null,
            NullLogger<JobExecutionService>.Instance);

        await InvokeProcessJobAsync(service, provisioning.OperationId, provisioning.ClaimedBy!);

        // After ProcessJobAsync completes, Cancel must return false: the CTS
        // was removed during AbandonJobAsync (before the requeue) so API
        // callers will not falsely delegate cancellation to a worker that
        // has already dropped ownership.
        Assert.False(cancellationTokens.Cancel(provisioning.OperationId));
    }

    /// <summary>
    /// Regression: shutdown-triggered requeue must also clear the tracked CTS
    /// immediately to prevent cancel-delegation to a worker that dropped ownership.
    /// Shutdown requeues bypass the retry budget (<see cref="JobRetryPolicy.None"/>)
    /// and must still clean up the CTS before the finally block.
    /// </summary>
    [UnitTest]
    public async Task ProcessJob_RemovesCtsAfterShutdownRequeue_SoCancelIsNotFalselyDelegated()
    {
        var provisioning = CreateProvisioningJob() with
        {
            RetryPolicy = JobRetryPolicy.None
        };

        var jobStore = Substitute.For<IExecutionJobStore>().WithTrySet();
        jobStore.GetAsync(provisioning.OperationId, Arg.Any<CancellationToken>())
            .Returns(provisioning);

        var jobQueue = Substitute.For<IJobQueue>();
        var stoppingCts = new CancellationTokenSource();

        var executor = Substitute.For<IJobExecutor>();
        executor.Kind.Returns(ExecutionJobKind.Geoprocessing);
        executor.ExecuteAsync(
                Arg.Any<ExecutionJobRecord>(),
                Arg.Any<IJobExecutionContext>(),
                Arg.Any<CancellationToken>())
            .Returns(async callInfo =>
            {
                await stoppingCts.CancelAsync().ConfigureAwait(false);
                callInfo.Arg<CancellationToken>().ThrowIfCancellationRequested();
                return JobExecutionResult.Succeeded(); // Unreachable.
            });

        var cancellationTokens = new ExecutionJobCancellationTokens();

        var service = new JobExecutionService(
            jobQueue, jobStore, [executor], cancellationTokens, Array.Empty<IJobTerminalCallback>(), null,
            NullLogger<JobExecutionService>.Instance);

        await InvokeProcessJobAsync(service, provisioning.OperationId,
            provisioning.ClaimedBy!, stoppingCts.Token);

        // After shutdown requeue, Cancel must return false.
        Assert.False(cancellationTokens.Cancel(provisioning.OperationId));
    }

    /// <summary>
    /// Regression: a durable cancellation signal racing with worker shutdown must
    /// be honoured by AbandonJobAsync instead of requeueing the job to an indefinite
    /// Queued state that the reconciler does not sweep.
    /// </summary>
    [UnitTest]
    public async Task ProcessJob_ShutdownRequeue_HonoursDurableCancellationSignal()
    {
        var provisioning = CreateProvisioningJob() with
        {
            RetryPolicy = JobRetryPolicy.None,
            CancellationRequestedAt = DateTimeOffset.UtcNow.AddSeconds(-1)
        };

        var jobStore = Substitute.For<IExecutionJobStore>().WithTrySet();
        jobStore.GetAsync(provisioning.OperationId, Arg.Any<CancellationToken>())
            .Returns(provisioning);

        var jobQueue = Substitute.For<IJobQueue>();
        var stoppingCts = new CancellationTokenSource();

        var executor = Substitute.For<IJobExecutor>();
        executor.Kind.Returns(ExecutionJobKind.Geoprocessing);
        executor.ExecuteAsync(
                Arg.Any<ExecutionJobRecord>(),
                Arg.Any<IJobExecutionContext>(),
                Arg.Any<CancellationToken>())
            .Returns(async callInfo =>
            {
                await stoppingCts.CancelAsync().ConfigureAwait(false);
                callInfo.Arg<CancellationToken>().ThrowIfCancellationRequested();
                return JobExecutionResult.Succeeded();
            });

        var cancellationTokens = new ExecutionJobCancellationTokens();

        var service = new JobExecutionService(
            jobQueue, jobStore, [executor], cancellationTokens, Array.Empty<IJobTerminalCallback>(), null,
            NullLogger<JobExecutionService>.Instance);

        await InvokeProcessJobAsync(service, provisioning.OperationId,
            provisioning.ClaimedBy!, stoppingCts.Token);

        await jobStore.Received().TrySetAsync(
            Arg.Is<ExecutionJobRecord>(j =>
                j.Status == ExecutionJobStatus.Cancelled &&
                j.CompletedAt.HasValue),
            Arg.Any<TimeSpan?>(),
            Arg.Any<CancellationToken>());

        await jobQueue.Received().RemoveAsync(provisioning.OperationId, Arg.Any<CancellationToken>());

        await jobQueue.DidNotReceive().RequeueAsync(
            Arg.Any<string>(),
            Arg.Any<OperationPriority>(),
            Arg.Any<TimeSpan?>(),
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Regression: after the authoritative store write in FinalizeJobAsync, the CTS
    /// must be removed before RemoveAsync so that a Redis hang does not leave a
    /// stale CTS that falsely accepts cancel delegation.
    /// </summary>
    [UnitTest]
    public async Task ProcessJob_Finalize_RemovesCtsBeforeQueueRemove_SoRemoveFailureDoesNotLeakStaleCts()
    {
        var provisioning = CreateProvisioningJob();

        var jobStore = Substitute.For<IExecutionJobStore>().WithTrySet();
        jobStore.GetAsync(provisioning.OperationId, Arg.Any<CancellationToken>())
            .Returns(provisioning);

        var cancellationTokens = new ExecutionJobCancellationTokens();
        bool ctsRemovedBeforeQueueIo = false;

        var jobQueue = Substitute.For<IJobQueue>();
        jobQueue.RemoveAsync(provisioning.OperationId, Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                ctsRemovedBeforeQueueIo = !cancellationTokens.Cancel(provisioning.OperationId);
                return Task.CompletedTask;
            });

        var executor = Substitute.For<IJobExecutor>();
        executor.Kind.Returns(ExecutionJobKind.Geoprocessing);
        executor.ExecuteAsync(
                Arg.Any<ExecutionJobRecord>(),
                Arg.Any<IJobExecutionContext>(),
                Arg.Any<CancellationToken>())
            .Returns(JobExecutionResult.Succeeded());

        var service = new JobExecutionService(
            jobQueue, jobStore, [executor], cancellationTokens, Array.Empty<IJobTerminalCallback>(), null,
            NullLogger<JobExecutionService>.Instance);

        await InvokeProcessJobAsync(service, provisioning.OperationId, provisioning.ClaimedBy!);

        Assert.True(ctsRemovedBeforeQueueIo,
            "CTS must be removed after authoritative store write and before queue RemoveAsync");
    }

    /// <summary>
    /// Regression: after the authoritative store write in TerminateJobAsync, the CTS
    /// must be removed before RemoveAsync so that a Redis hang does not leave a
    /// stale CTS that falsely accepts cancel delegation.
    /// </summary>
    [UnitTest]
    public async Task ProcessJob_Terminate_RemovesCtsBeforeQueueRemove_SoRemoveFailureDoesNotLeakStaleCts()
    {
        var provisioning = CreateProvisioningJob();

        var jobStore = Substitute.For<IExecutionJobStore>().WithTrySet();
        jobStore.GetAsync(provisioning.OperationId, Arg.Any<CancellationToken>())
            .Returns(provisioning);

        var cancellationTokens = new ExecutionJobCancellationTokens();
        bool ctsRemovedBeforeQueueIo = false;

        var jobQueue = Substitute.For<IJobQueue>();
        jobQueue.RemoveAsync(provisioning.OperationId, Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                ctsRemovedBeforeQueueIo = !cancellationTokens.Cancel(provisioning.OperationId);
                return Task.CompletedTask;
            });

        var executor = Substitute.For<IJobExecutor>();
        executor.Kind.Returns(ExecutionJobKind.Geoprocessing);
        executor.ExecuteAsync(
                Arg.Any<ExecutionJobRecord>(),
                Arg.Any<IJobExecutionContext>(),
                Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                cancellationTokens.Cancel(provisioning.OperationId);
                callInfo.Arg<CancellationToken>().ThrowIfCancellationRequested();
                return JobExecutionResult.Succeeded();
            });

        var service = new JobExecutionService(
            jobQueue, jobStore, [executor], cancellationTokens, Array.Empty<IJobTerminalCallback>(), null,
            NullLogger<JobExecutionService>.Instance);

        await InvokeProcessJobAsync(service, provisioning.OperationId, provisioning.ClaimedBy!);

        Assert.True(ctsRemovedBeforeQueueIo,
            "CTS must be removed after authoritative store write and before queue RemoveAsync");
    }

    /// <summary>
    /// Regression: after the authoritative store transition to Queued in
    /// AbandonJobAsync, the CTS must be removed before RequeueAsync so that
    /// a Redis failure does not leave a stale CTS that falsely accepts cancel
    /// delegation while the stale-claim reconciler repairs the queue.
    /// </summary>
    [UnitTest]
    public async Task ProcessJob_AbandonRequeue_RemovesCtsBeforeQueueRequeue_SoRequeueFailureDoesNotLeakStaleCts()
    {
        var provisioning = CreateProvisioningJob() with
        {
            RetryPolicy = new JobRetryPolicy
            {
                MaxAttempts = 3,
                Strategy = BackoffStrategy.Fixed,
                BaseDelay = TimeSpan.Zero,
                MaxDelay = TimeSpan.Zero
            }
        };

        var jobStore = Substitute.For<IExecutionJobStore>().WithTrySet();
        jobStore.GetAsync(provisioning.OperationId, Arg.Any<CancellationToken>())
            .Returns(provisioning);

        var cancellationTokens = new ExecutionJobCancellationTokens();
        bool ctsRemovedBeforeQueueIo = false;

        var jobQueue = Substitute.For<IJobQueue>();
        jobQueue.RequeueAsync(
                Arg.Any<string>(), Arg.Any<OperationPriority>(),
                Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                ctsRemovedBeforeQueueIo = !cancellationTokens.Cancel(provisioning.OperationId);
                return Task.CompletedTask;
            });

        var executor = Substitute.For<IJobExecutor>();
        executor.Kind.Returns(ExecutionJobKind.Geoprocessing);
        executor.ExecuteAsync(
                Arg.Any<ExecutionJobRecord>(),
                Arg.Any<IJobExecutionContext>(),
                Arg.Any<CancellationToken>())
            .Returns(JobExecutionResult.Failed("Transient failure"));

        var service = new JobExecutionService(
            jobQueue, jobStore, [executor], cancellationTokens, Array.Empty<IJobTerminalCallback>(), null,
            NullLogger<JobExecutionService>.Instance);

        await InvokeProcessJobAsync(service, provisioning.OperationId, provisioning.ClaimedBy!);

        Assert.True(ctsRemovedBeforeQueueIo,
            "CTS must be removed after authoritative store write and before queue RequeueAsync");
    }

    /// <summary>
    /// Regression: after the authoritative store write in AbandonJobAsync's terminal
    /// path (retries exhausted), the CTS must be removed before RemoveAsync so that
    /// a Redis hang does not leave a stale CTS that falsely accepts cancel delegation.
    /// </summary>
    [UnitTest]
    public async Task ProcessJob_AbandonTerminal_RemovesCtsBeforeQueueRemove_SoRemoveFailureDoesNotLeakStaleCts()
    {
        var provisioning = CreateProvisioningJob() with
        {
            RetryPolicy = JobRetryPolicy.None
        };

        var jobStore = Substitute.For<IExecutionJobStore>().WithTrySet();
        jobStore.GetAsync(provisioning.OperationId, Arg.Any<CancellationToken>())
            .Returns(provisioning);

        var cancellationTokens = new ExecutionJobCancellationTokens();
        bool ctsRemovedBeforeQueueIo = false;

        var jobQueue = Substitute.For<IJobQueue>();
        jobQueue.RemoveAsync(provisioning.OperationId, Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                ctsRemovedBeforeQueueIo = !cancellationTokens.Cancel(provisioning.OperationId);
                return Task.CompletedTask;
            });

        var executor = Substitute.For<IJobExecutor>();
        executor.Kind.Returns(ExecutionJobKind.Geoprocessing);
        executor.ExecuteAsync(
                Arg.Any<ExecutionJobRecord>(),
                Arg.Any<IJobExecutionContext>(),
                Arg.Any<CancellationToken>())
            .Returns(JobExecutionResult.Failed("Permanent failure"));

        var service = new JobExecutionService(
            jobQueue, jobStore, [executor], cancellationTokens, Array.Empty<IJobTerminalCallback>(), null,
            NullLogger<JobExecutionService>.Instance);

        await InvokeProcessJobAsync(service, provisioning.OperationId, provisioning.ClaimedBy!);

        Assert.True(ctsRemovedBeforeQueueIo,
            "CTS must be removed after authoritative store write and before queue RemoveAsync");
    }

    /// <summary>
    /// Regression: when a new worker reclaims a job, the old worker's CTS must be
    /// cancelled during registration so the stale worker observes cancellation even
    /// if Revoke has not yet run.
    /// </summary>
    [UnitTest]
    public void CreateLinkedTokenSource_CancelsOldCts_WhenJobReclaimedByNewWorker()
    {
        var cancellationTokens = new ExecutionJobCancellationTokens();

        using var ctsA = cancellationTokens.CreateLinkedTokenSource(
            "job-1", "worker-A", CancellationToken.None);

        // Worker B claims and registers — must cancel Worker A's stale CTS.
        using var ctsB = cancellationTokens.CreateLinkedTokenSource(
            "job-1", "worker-B", CancellationToken.None);

        Assert.True(ctsA.IsCancellationRequested);
        Assert.False(ctsB.IsCancellationRequested);
    }

    /// <summary>
    /// Regression: heartbeat pump store failures must not fault the task.
    /// If the pump task faults and StopHeartbeatPumpAsync only catches
    /// OperationCanceledException, the store exception propagates up and
    /// skips finalization. The pump must catch non-cancellation exceptions
    /// and continue pumping on the next interval.
    /// </summary>
    [UnitTest]
    public async Task HeartbeatPump_DoesNotFaultTask_WhenStoreThrowsPersistently()
    {
        var running = CreateProvisioningJob() with { Status = ExecutionJobStatus.Running };

        var jobStore = Substitute.For<IExecutionJobStore>().WithTrySet();
        jobStore.GetAsync(running.OperationId, Arg.Any<CancellationToken>())
            .Returns<ExecutionJobRecord?>(_ => throw new InvalidOperationException("Simulated store failure"));

        using var context = new JobExecutionContext(
            running.OperationId, running.ClaimedBy!, jobStore, null,
            new JobHeartbeatPolicy { Interval = TimeSpan.FromMilliseconds(10), Timeout = TimeSpan.FromSeconds(30) },
            null, NullLogger.Instance);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(80));

        // Must complete without throwing despite persistent store failures.
        await context.RunHeartbeatPumpAsync(cts.Token);
    }

    /// <summary>
    /// Regression: a single transient store failure must not kill the heartbeat
    /// pump. The pump must catch the exception, log it, and continue pumping
    /// on the next interval so that the reconciler does not declare the worker
    /// dead due to a brief Redis blip.
    /// </summary>
    [UnitTest]
    public async Task HeartbeatPump_ContinuesAfterTransientStoreFailure()
    {
        var running = CreateProvisioningJob() with { Status = ExecutionJobStatus.Running };

        var callCount = 0;
        var jobStore = Substitute.For<IExecutionJobStore>().WithTrySet();
        jobStore.GetAsync(running.OperationId, Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var count = Interlocked.Increment(ref callCount);
                if (count == 1)
                    throw new InvalidOperationException("Transient failure");
                return running;
            });

        using var context = new JobExecutionContext(
            running.OperationId, running.ClaimedBy!, jobStore, null,
            new JobHeartbeatPolicy { Interval = TimeSpan.FromMilliseconds(10), Timeout = TimeSpan.FromSeconds(30) },
            null, NullLogger.Instance);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        await context.RunHeartbeatPumpAsync(cts.Token);

        // The pump continued past the transient failure and wrote at least one heartbeat.
        await jobStore.Received().TrySetAsync(
            Arg.Any<ExecutionJobRecord>(),
            Arg.Any<TimeSpan?>(),
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Regression: when a job is requeued and immediately reclaimed by another worker,
    /// the stale worker's finally-block Remove must not delete the new worker's CTS.
    /// </summary>
    [UnitTest]
    public void Remove_PreservesNewWorkerCts_WhenJobReclaimedAfterRequeue()
    {
        var cancellationTokens = new ExecutionJobCancellationTokens();

        // Worker A registers CTS.
        using var ctsA = cancellationTokens.CreateLinkedTokenSource(
            "job-1", "worker-A", CancellationToken.None);

        // Job is requeued; Worker B claims and registers a new CTS.
        using var ctsB = cancellationTokens.CreateLinkedTokenSource(
            "job-1", "worker-B", CancellationToken.None);

        // Worker A's finally block runs — must NOT remove Worker B's CTS.
        cancellationTokens.Remove("job-1", "worker-A");

        // Worker B's CTS must still be cancellable via the operator path.
        Assert.True(cancellationTokens.Cancel("job-1"));
        Assert.True(ctsB.IsCancellationRequested);
    }

    /// <summary>
    /// Regression: when the reconciler requeues a job and a new worker claims it
    /// before Revoke runs, the Revoke must not cancel the new worker's CTS.
    /// </summary>
    [UnitTest]
    public void Revoke_PreservesNewWorkerCts_WhenJobReclaimedBeforeRevoke()
    {
        var cancellationTokens = new ExecutionJobCancellationTokens();

        // Worker A registered CTS (simulated by reconciler tracking).
        using var ctsA = cancellationTokens.CreateLinkedTokenSource(
            "job-1", "worker-A", CancellationToken.None);

        // Job was requeued; Worker B claims and registers before Revoke runs.
        using var ctsB = cancellationTokens.CreateLinkedTokenSource(
            "job-1", "worker-B", CancellationToken.None);

        // Reconciler's Revoke targets worker-A — must NOT cancel worker-B's CTS.
        cancellationTokens.Revoke("job-1", "worker-A");

        Assert.False(ctsB.IsCancellationRequested);

        // Operator cancellation must still reach the new worker.
        Assert.True(cancellationTokens.Cancel("job-1"));
        Assert.True(ctsB.IsCancellationRequested);
    }

    /// <summary>
    /// The generated worker ID must always contain a full 32-hex-char GUID suffix,
    /// even when the machine hostname is very long. Without this, two workers on
    /// long-hostname nodes can share the same truncated ID, breaking ownership guards.
    /// </summary>
    [UnitTest]
    public void GenerateWorkerId_AlwaysPreservesFullGuid()
    {
        var workerId = JobExecutionService.GenerateWorkerId();

        Assert.True(workerId.Length <= 48);

        var lastDash = workerId.LastIndexOf('-');
        Assert.True(lastDash >= 0, "Worker ID must contain a GUID suffix separated by '-'");

        var guidPart = workerId[(lastDash + 1)..];
        Assert.Equal(32, guidPart.Length);
        Assert.True(guidPart.All(c => "0123456789abcdef".Contains(c)),
            "GUID suffix must be 32 lowercase hex characters");
    }

    /// <summary>
    /// Regression: even when the machine hostname exceeds the available prefix
    /// budget, the generated worker ID must keep the full GUID claim token and
    /// only truncate the hostname portion.
    /// </summary>
    [UnitTest]
    public void GenerateWorkerId_TruncatesHostnamePrefix_ButPreservesFullGuidSuffix()
    {
        const string machineName = "averyverylonghostnamethatexceedstheworkerbudget";
        var workerGuid = Guid.ParseExact("0123456789abcdef0123456789abcdef", "N");

        var workerId = JobExecutionService.GenerateWorkerId(machineName, workerGuid);

        Assert.Equal("worker-averyver-0123456789abcdef0123456789abcdef", workerId);
        Assert.Equal(48, workerId.Length);
    }

    /// <summary>
    /// When a worker has no executors registered, it must not enter the claim
    /// loop. Without this guard, the worker performs O(n) Redis scans every
    /// poll interval for no benefit.
    /// </summary>
    [UnitTest]
    public async Task ExecuteAsync_SkipsClaimLoop_WhenNoExecutorsRegistered()
    {
        var jobQueue = Substitute.For<IJobQueue>();
        var jobStore = Substitute.For<IExecutionJobStore>().WithTrySet();
        var cancellationTokens = new ExecutionJobCancellationTokens();

        var service = new JobExecutionService(
            jobQueue, jobStore, Array.Empty<IJobExecutor>(), cancellationTokens, Array.Empty<IJobTerminalCallback>(), null,
            NullLogger<JobExecutionService>.Instance);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        await service.StartAsync(cts.Token);
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(250));
        }
        catch (OperationCanceledException)
        {
        }

        await service.StopAsync(CancellationToken.None);

        await jobQueue.DidNotReceive().TryClaimAsync(
            Arg.Any<string>(),
            Arg.Any<IReadOnlySet<ExecutionJobKind>>(),
            Arg.Any<IReadOnlySet<string>?>(),
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Regression: when the host stops after TryClaimAsync succeeds but before
    /// the executor try/catch is reached, the claimed job must be immediately
    /// requeued by the ExecuteAsync shutdown handler instead of being left in
    /// Provisioning until heartbeat expiry.
    /// </summary>
    [UnitTest]
    public async Task ExecuteAsync_RequeuesClaimedJob_WhenShutdownDuringPreExecution()
    {
        var provisioning = CreateProvisioningJob();

        string? capturedWorkerId = null;
        var stoppingCts = new CancellationTokenSource();

        var jobQueue = Substitute.For<IJobQueue>();
        jobQueue.TryClaimAsync(Arg.Any<string>(), Arg.Any<IReadOnlySet<ExecutionJobKind>>(), Arg.Any<IReadOnlySet<string>?>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                capturedWorkerId = callInfo.ArgAt<string>(0);
                stoppingCts.Cancel();
                return (string?)provisioning.OperationId;
            });

        var jobStore = Substitute.For<IExecutionJobStore>().WithTrySet();
        jobStore.GetAsync(provisioning.OperationId, Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                callInfo.Arg<CancellationToken>().ThrowIfCancellationRequested();
                return (ExecutionJobRecord?)(provisioning with { ClaimedBy = capturedWorkerId });
            });

        var executor = Substitute.For<IJobExecutor>();
        executor.Kind.Returns(ExecutionJobKind.Geoprocessing);

        var cancellationTokens = new ExecutionJobCancellationTokens();

        var service = new JobExecutionService(
            jobQueue, jobStore, [executor], cancellationTokens, Array.Empty<IJobTerminalCallback>(), null,
            NullLogger<JobExecutionService>.Instance);

        await service.StartAsync(stoppingCts.Token);
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(500));
        }
        catch (OperationCanceledException)
        {
        }

        await service.StopAsync(CancellationToken.None);

        // The job must be requeued, not left to heartbeat expiry.
        await jobStore.Received().TrySetAsync(
            Arg.Is<ExecutionJobRecord>(j =>
                j.Status == ExecutionJobStatus.Queued &&
                j.ClaimedBy == null),
            Arg.Any<TimeSpan?>(),
            Arg.Any<CancellationToken>());

        await jobQueue.Received().RequeueAsync(
            provisioning.OperationId,
            Arg.Any<OperationPriority>(),
            Arg.Any<TimeSpan?>(),
            Arg.Any<CancellationToken>());

        // Executor must NOT have been invoked.
        await executor.DidNotReceive().ExecuteAsync(
            Arg.Any<ExecutionJobRecord>(),
            Arg.Any<IJobExecutionContext>(),
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Regression: a durable cancellation signal (CancellationRequestedAt) set by
    /// a remote API host before the worker transitions to Running must be honoured
    /// so the job does not proceed to execution.
    /// </summary>
    [UnitTest]
    public async Task ProcessJob_SkipsExecution_WhenDurableCancellationSignalIsSet()
    {
        var provisioning = CreateProvisioningJob() with
        {
            CancellationRequestedAt = DateTimeOffset.UtcNow.AddSeconds(-2)
        };

        var jobStore = Substitute.For<IExecutionJobStore>().WithTrySet();
        jobStore.GetAsync(provisioning.OperationId, Arg.Any<CancellationToken>())
            .Returns(provisioning);

        var jobQueue = Substitute.For<IJobQueue>();
        var executor = Substitute.For<IJobExecutor>();
        executor.Kind.Returns(ExecutionJobKind.Geoprocessing);

        var cancellationTokens = new ExecutionJobCancellationTokens();

        var service = new JobExecutionService(
            jobQueue, jobStore, [executor], cancellationTokens, Array.Empty<IJobTerminalCallback>(), null,
            NullLogger<JobExecutionService>.Instance);

        await InvokeProcessJobAsync(service, provisioning.OperationId, provisioning.ClaimedBy!);

        await jobStore.DidNotReceive().TrySetAsync(
            Arg.Is<ExecutionJobRecord>(j => j.Status == ExecutionJobStatus.Running),
            Arg.Any<TimeSpan?>(),
            Arg.Any<CancellationToken>());

        await executor.DidNotReceive().ExecuteAsync(
            Arg.Any<ExecutionJobRecord>(),
            Arg.Any<IJobExecutionContext>(),
            Arg.Any<CancellationToken>());

        await jobStore.Received().TrySetAsync(
            Arg.Is<ExecutionJobRecord>(j => j.Status == ExecutionJobStatus.Cancelled),
            Arg.Any<TimeSpan?>(),
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Regression: the heartbeat pump must detect a durable cancellation signal
    /// and cancel the per-job CTS so the executor receives the cancellation
    /// through its CancellationToken.
    /// </summary>
    [UnitTest]
    public async Task HeartbeatPump_CancelsJobCts_WhenDurableCancellationSignalDetected()
    {
        var running = CreateProvisioningJob(claimedBy: "worker-test") with
        {
            Status = ExecutionJobStatus.Running
        };
        var withSignal = running with
        {
            CancellationRequestedAt = DateTimeOffset.UtcNow
        };

        var jobStore = Substitute.For<IExecutionJobStore>().WithTrySet();
        jobStore.GetAsync(running.OperationId, Arg.Any<CancellationToken>())
            .Returns(withSignal);

        using var jobCts = new CancellationTokenSource();
        using var context = new JobExecutionContext(
            running.OperationId, "worker-test", jobStore, null,
            new JobHeartbeatPolicy { Interval = TimeSpan.FromMilliseconds(10), Timeout = TimeSpan.FromSeconds(30) },
            jobCts, NullLogger.Instance);

        using var pumpTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await context.RunHeartbeatPumpAsync(pumpTimeout.Token);

        Assert.True(jobCts.IsCancellationRequested);
    }

    /// <summary>
    /// Regression: after the authoritative store write, a queue removal failure
    /// must not prevent the terminal callback from firing. The admin API reads
    /// from the progress store (synced by the callback), so skipping it leaves
    /// stale in-progress state until TTL expiry.
    /// </summary>
    [UnitTest]
    public async Task ProcessJob_Finalize_NotifiesTerminalCallback_WhenQueueRemovalFails()
    {
        var provisioning = CreateProvisioningJob();

        var jobStore = Substitute.For<IExecutionJobStore>().WithTrySet();
        jobStore.GetAsync(provisioning.OperationId, Arg.Any<CancellationToken>())
            .Returns(provisioning);

        var jobQueue = Substitute.For<IJobQueue>();
        jobQueue.RemoveAsync(provisioning.OperationId, Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new InvalidOperationException("Simulated Redis failure"));

        var executor = Substitute.For<IJobExecutor>();
        executor.Kind.Returns(ExecutionJobKind.Geoprocessing);
        executor.ExecuteAsync(
                Arg.Any<ExecutionJobRecord>(),
                Arg.Any<IJobExecutionContext>(),
                Arg.Any<CancellationToken>())
            .Returns(JobExecutionResult.Succeeded());

        var terminalCallback = Substitute.For<IJobTerminalCallback>();
        var cancellationTokens = new ExecutionJobCancellationTokens();

        var service = new JobExecutionService(
            jobQueue, jobStore, [executor], cancellationTokens, [terminalCallback], null,
            NullLogger<JobExecutionService>.Instance);

        await InvokeProcessJobAsync(service, provisioning.OperationId, provisioning.ClaimedBy!);

        await terminalCallback.Received(1).OnTerminalAsync(
            Arg.Is<ExecutionJobRecord>(j => j.Status == ExecutionJobStatus.Succeeded),
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Regression: terminal callback must fire even when queue removal fails
    /// in the abandon path (retries exhausted).
    /// </summary>
    [UnitTest]
    public async Task ProcessJob_AbandonTerminal_NotifiesTerminalCallback_WhenQueueRemovalFails()
    {
        var provisioning = CreateProvisioningJob() with
        {
            RetryPolicy = JobRetryPolicy.None
        };

        var jobStore = Substitute.For<IExecutionJobStore>().WithTrySet();
        jobStore.GetAsync(provisioning.OperationId, Arg.Any<CancellationToken>())
            .Returns(provisioning);

        var jobQueue = Substitute.For<IJobQueue>();
        jobQueue.RemoveAsync(provisioning.OperationId, Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new InvalidOperationException("Simulated Redis failure"));

        var executor = Substitute.For<IJobExecutor>();
        executor.Kind.Returns(ExecutionJobKind.Geoprocessing);
        executor.ExecuteAsync(
                Arg.Any<ExecutionJobRecord>(),
                Arg.Any<IJobExecutionContext>(),
                Arg.Any<CancellationToken>())
            .Returns(JobExecutionResult.Failed("Permanent failure"));

        var terminalCallback = Substitute.For<IJobTerminalCallback>();
        var cancellationTokens = new ExecutionJobCancellationTokens();

        var service = new JobExecutionService(
            jobQueue, jobStore, [executor], cancellationTokens, [terminalCallback], null,
            NullLogger<JobExecutionService>.Instance);

        await InvokeProcessJobAsync(service, provisioning.OperationId, provisioning.ClaimedBy!);

        await terminalCallback.Received(1).OnTerminalAsync(
            Arg.Is<ExecutionJobRecord>(j => j.Status == ExecutionJobStatus.Failed),
            Arg.Any<CancellationToken>());
    }

    [UnitTest]
    public async Task ProcessJob_Finalize_NotifiesTerminalCallback_WhenLogRetentionFails()
    {
        var provisioning = CreateProvisioningJob();

        var jobStore = Substitute.For<IExecutionJobStore>().WithTrySet();
        jobStore.GetAsync(provisioning.OperationId, Arg.Any<CancellationToken>())
            .Returns(provisioning);

        var jobQueue = Substitute.For<IJobQueue>();
        jobQueue.TryClaimAsync(Arg.Any<string>(), Arg.Any<IReadOnlySet<ExecutionJobKind>>(), Arg.Any<IReadOnlySet<string>?>(), Arg.Any<CancellationToken>())
            .Returns(provisioning.OperationId, (string?)null);

        var executor = Substitute.For<IJobExecutor>();
        executor.Kind.Returns(ExecutionJobKind.Geoprocessing);
        executor.ExecuteAsync(
                Arg.Any<ExecutionJobRecord>(),
                Arg.Any<IJobExecutionContext>(),
                Arg.Any<CancellationToken>())
            .Returns(JobExecutionResult.Succeeded());

        var logStore = Substitute.For<IExecutionLogStore>();
        logStore.SetRetentionAsync(Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new InvalidOperationException("Simulated Redis failure"));

        var terminalCallback = Substitute.For<IJobTerminalCallback>();
        var cancellationTokens = new ExecutionJobCancellationTokens();

        var service = new JobExecutionService(
            jobQueue, jobStore, [executor], cancellationTokens, [terminalCallback], logStore,
            NullLogger<JobExecutionService>.Instance);

        await InvokeProcessJobAsync(service, provisioning.OperationId, provisioning.ClaimedBy!);

        await terminalCallback.Received(1).OnTerminalAsync(
            Arg.Is<ExecutionJobRecord>(j => j.Status == ExecutionJobStatus.Succeeded),
            Arg.Any<CancellationToken>());
    }

    [UnitTest]
    public async Task ProcessJob_AbandonTerminal_NotifiesTerminalCallback_WhenLogRetentionFails()
    {
        var provisioning = CreateProvisioningJob() with
        {
            RetryPolicy = JobRetryPolicy.None
        };

        var jobStore = Substitute.For<IExecutionJobStore>().WithTrySet();
        jobStore.GetAsync(provisioning.OperationId, Arg.Any<CancellationToken>())
            .Returns(provisioning);

        var jobQueue = Substitute.For<IJobQueue>();
        jobQueue.TryClaimAsync(Arg.Any<string>(), Arg.Any<IReadOnlySet<ExecutionJobKind>>(), Arg.Any<IReadOnlySet<string>?>(), Arg.Any<CancellationToken>())
            .Returns(provisioning.OperationId, (string?)null);

        var executor = Substitute.For<IJobExecutor>();
        executor.Kind.Returns(ExecutionJobKind.Geoprocessing);
        executor.ExecuteAsync(
                Arg.Any<ExecutionJobRecord>(),
                Arg.Any<IJobExecutionContext>(),
                Arg.Any<CancellationToken>())
            .Returns(JobExecutionResult.Failed("Permanent failure"));

        var logStore = Substitute.For<IExecutionLogStore>();
        logStore.SetRetentionAsync(Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new InvalidOperationException("Simulated Redis failure"));

        var terminalCallback = Substitute.For<IJobTerminalCallback>();
        var cancellationTokens = new ExecutionJobCancellationTokens();

        var service = new JobExecutionService(
            jobQueue, jobStore, [executor], cancellationTokens, [terminalCallback], logStore,
            NullLogger<JobExecutionService>.Instance);

        await InvokeProcessJobAsync(service, provisioning.OperationId, provisioning.ClaimedBy!);

        await terminalCallback.Received(1).OnTerminalAsync(
            Arg.Is<ExecutionJobRecord>(j => j.Status == ExecutionJobStatus.Failed),
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Regression: a durable cancellation signal that arrives during the retry-
    /// preparation phase (log appends) must be honoured by the pre-requeue re-read
    /// instead of being silently overwritten by the requeue write.
    /// </summary>
    [UnitTest]
    public async Task AbandonJob_HonoursCancellation_WhenSignalArrivesDuringRetryPrep()
    {
        var provisioning = CreateProvisioningJob() with
        {
            RetryPolicy = new JobRetryPolicy
            {
                MaxAttempts = 3,
                Strategy = BackoffStrategy.Fixed,
                BaseDelay = TimeSpan.FromSeconds(1),
                MaxDelay = TimeSpan.FromMinutes(1)
            }
        };

        var withCancel = provisioning with
        {
            CancellationRequestedAt = DateTimeOffset.UtcNow
        };

        var callCount = 0;
        var jobStore = Substitute.For<IExecutionJobStore>().WithTrySet();
        jobStore.GetAsync(provisioning.OperationId, Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                callCount++;
                // Reads 1-3: executor lookup, CTS re-read, AbandonJobAsync re-read — no cancel.
                // Read 4+: pre-requeue re-read and TerminateJobAsync — cancel signal present.
                return callCount <= 3 ? provisioning : withCancel;
            });

        var jobQueue = Substitute.For<IJobQueue>();
        var executor = Substitute.For<IJobExecutor>();
        executor.Kind.Returns(ExecutionJobKind.Geoprocessing);
        executor.ExecuteAsync(
                Arg.Any<ExecutionJobRecord>(),
                Arg.Any<IJobExecutionContext>(),
                Arg.Any<CancellationToken>())
            .Returns(JobExecutionResult.Failed("Transient failure"));

        var cancellationTokens = new ExecutionJobCancellationTokens();

        var service = new JobExecutionService(
            jobQueue, jobStore, [executor], cancellationTokens, Array.Empty<IJobTerminalCallback>(), null,
            NullLogger<JobExecutionService>.Instance);

        await InvokeProcessJobAsync(service, provisioning.OperationId, provisioning.ClaimedBy!);

        await jobStore.Received().TrySetAsync(
            Arg.Is<ExecutionJobRecord>(j => j.Status == ExecutionJobStatus.Cancelled),
            Arg.Any<TimeSpan?>(),
            Arg.Any<CancellationToken>());

        await jobQueue.DidNotReceive().RequeueAsync(
            Arg.Any<string>(), Arg.Any<OperationPriority>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Regression: a durable cancellation signal that arrives before the terminal
    /// fail write (retries exhausted) must be honoured by the pre-fail re-read.
    /// </summary>
    [UnitTest]
    public async Task AbandonJob_HonoursCancellation_WhenSignalArrivesBeforeFailWrite()
    {
        var provisioning = CreateProvisioningJob() with
        {
            RetryPolicy = JobRetryPolicy.None
        };

        var withCancel = provisioning with
        {
            CancellationRequestedAt = DateTimeOffset.UtcNow
        };

        var callCount = 0;
        var jobStore = Substitute.For<IExecutionJobStore>().WithTrySet();
        jobStore.GetAsync(provisioning.OperationId, Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                callCount++;
                // Reads 1-3: executor lookup, CTS re-read, AbandonJobAsync re-read — no cancel.
                // Read 4+: pre-fail re-read and TerminateJobAsync — cancel signal present.
                return callCount <= 3 ? provisioning : withCancel;
            });

        var jobQueue = Substitute.For<IJobQueue>();
        var executor = Substitute.For<IJobExecutor>();
        executor.Kind.Returns(ExecutionJobKind.Geoprocessing);
        executor.ExecuteAsync(
                Arg.Any<ExecutionJobRecord>(),
                Arg.Any<IJobExecutionContext>(),
                Arg.Any<CancellationToken>())
            .Returns(JobExecutionResult.Failed("Permanent failure"));

        var cancellationTokens = new ExecutionJobCancellationTokens();

        var service = new JobExecutionService(
            jobQueue, jobStore, [executor], cancellationTokens, Array.Empty<IJobTerminalCallback>(), null,
            NullLogger<JobExecutionService>.Instance);

        await InvokeProcessJobAsync(service, provisioning.OperationId, provisioning.ClaimedBy!);

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
    public async Task ProcessJob_EmitsTransitionTelemetry_ForRunningAndFinalize()
    {
        const string telemetryBackend = "job-execution-running-finalize-test";
        var provisioning = CreateProvisioningJob(backend: telemetryBackend);
        var running = provisioning with { Status = ExecutionJobStatus.Running };

        var jobStore = Substitute.For<IExecutionJobStore>().WithTrySet();
        // First two reads (initial lookup + pre-Running re-read) return Provisioning;
        // subsequent reads (FinalizeJobAsync) see the authoritative Running record.
        jobStore.GetAsync(provisioning.OperationId, Arg.Any<CancellationToken>())
            .Returns(provisioning, provisioning, running);

        var jobQueue = Substitute.For<IJobQueue>();

        var executor = Substitute.For<IJobExecutor>();
        executor.Kind.Returns(ExecutionJobKind.Geoprocessing);
        executor.ExecuteAsync(
                Arg.Any<ExecutionJobRecord>(),
                Arg.Any<IJobExecutionContext>(),
                Arg.Any<CancellationToken>())
            .Returns(JobExecutionResult.Succeeded());

        var cancellationTokens = new ExecutionJobCancellationTokens();

        var service = new JobExecutionService(
            jobQueue, jobStore, [executor], cancellationTokens, Array.Empty<IJobTerminalCallback>(), null,
            NullLogger<JobExecutionService>.Instance);

        var transitions = new List<MeasurementSample>();
        using var listener = CreateTransitionListener(transitions, telemetryBackend);

        await InvokeProcessJobAsync(service, provisioning.OperationId, provisioning.ClaimedBy!);

        listener.RecordObservableInstruments();

        var snapshot = SnapshotMeasurements(transitions);

        Assert.Contains(snapshot, sample =>
            GetTagString(sample.Tags, "honua.controlplane.execution.previous_status") == "Provisioning" &&
            GetTagString(sample.Tags, "honua.controlplane.execution.status") == "Running");

        Assert.Contains(snapshot, sample =>
            GetTagString(sample.Tags, "honua.controlplane.execution.previous_status") == "Running" &&
            GetTagString(sample.Tags, "honua.controlplane.execution.status") == "Succeeded");
    }

    [UnitTest]
    public async Task ProcessJob_EmitsTransitionTelemetry_ForAbandonRequeue()
    {
        const string telemetryBackend = "job-execution-requeue-test";
        var provisioning = CreateProvisioningJob(backend: telemetryBackend) with
        {
            RetryPolicy = new JobRetryPolicy
            {
                MaxAttempts = 3,
                Strategy = BackoffStrategy.Fixed,
                BaseDelay = TimeSpan.FromSeconds(1),
                MaxDelay = TimeSpan.FromMinutes(1)
            }
        };
        var running = provisioning with { Status = ExecutionJobStatus.Running };

        var jobStore = Substitute.For<IExecutionJobStore>().WithTrySet();
        jobStore.GetAsync(provisioning.OperationId, Arg.Any<CancellationToken>())
            .Returns(provisioning, provisioning, running);

        var jobQueue = Substitute.For<IJobQueue>();

        var executor = Substitute.For<IJobExecutor>();
        executor.Kind.Returns(ExecutionJobKind.Geoprocessing);
        executor.ExecuteAsync(
                Arg.Any<ExecutionJobRecord>(),
                Arg.Any<IJobExecutionContext>(),
                Arg.Any<CancellationToken>())
            .Returns(JobExecutionResult.Failed("Transient failure"));

        var cancellationTokens = new ExecutionJobCancellationTokens();

        var service = new JobExecutionService(
            jobQueue, jobStore, [executor], cancellationTokens, Array.Empty<IJobTerminalCallback>(), null,
            NullLogger<JobExecutionService>.Instance);

        var transitions = new List<MeasurementSample>();
        using var listener = CreateTransitionListener(transitions, telemetryBackend);

        await InvokeProcessJobAsync(service, provisioning.OperationId, provisioning.ClaimedBy!);

        listener.RecordObservableInstruments();

        var snapshot = SnapshotMeasurements(transitions);

        // Retry path: Running -> Queued must emit a transition sample.
        Assert.Contains(snapshot, sample =>
            GetTagString(sample.Tags, "honua.controlplane.execution.previous_status") == "Running" &&
            GetTagString(sample.Tags, "honua.controlplane.execution.status") == "Queued");
    }

    private sealed record MeasurementSample(long Value, KeyValuePair<string, object?>[] Tags);

    private static MeterListener CreateTransitionListener(List<MeasurementSample> samples, string expectedBackend)
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
            var tagArray = tags.ToArray();
            if (!string.Equals(
                    GetTagString(tagArray, ControlPlaneTelemetry.Tags.Backend),
                    expectedBackend,
                    StringComparison.Ordinal))
            {
                return;
            }

            lock (samples)
            {
                samples.Add(new MeasurementSample(measurement, tagArray));
            }
        });
        listener.Start();
        return listener;
    }

    private static MeasurementSample[] SnapshotMeasurements(List<MeasurementSample> samples)
    {
        lock (samples)
        {
            return samples.ToArray();
        }
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
    /// Invokes the private ProcessJobAsync method via reflection.
    /// Follows the same test pattern used in <see cref="JobReconciliationServiceTests"/>.
    /// </summary>
    private static async Task InvokeProcessJobAsync(
        JobExecutionService service,
        string operationId,
        string workerId,
        CancellationToken stoppingToken = default)
    {
        var method = typeof(JobExecutionService).GetMethod(
            "ProcessJobAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

        var task = (Task)method!.Invoke(service, [operationId, workerId, stoppingToken])!;
        await task.ConfigureAwait(false);
    }
}
