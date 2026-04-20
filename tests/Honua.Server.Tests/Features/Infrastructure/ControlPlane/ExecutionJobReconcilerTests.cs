// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics.Metrics;
using FluentAssertions;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Server.Features.Infrastructure.ControlPlane;
using Honua.ServiceDefaults;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Honua.Server.Tests.Features.Infrastructure.ControlPlane;

public sealed class ExecutionJobReconcilerTests
{
    [Fact]
    public async Task ReconcileExecutionJob_BackendMissing_FailsJobRecordAndBridgesProgress()
    {
        var job = CreateJobRecord(
            operationId: "job-missing",
            status: ExecutionJobStatus.Queued,
            backend: "missing-backend",
            targetKind: BatchComputeTargetKind.AwsBatch);
        var jobStore = new InMemoryExecutionJobStore(job);
        var progressStore = new InMemoryProgressStore();
        await progressStore.SetProgressAsync(
            "job-missing",
            GeoprocessingProgress.CreateForSubmittedJob("job-missing", "plan-missing"));
        var sut = new ExecutionJobReconciler(
            jobStore,
            Array.Empty<IBatchComputeBackend>(),
            progressStore,
            NullLogger<ExecutionJobReconciler>.Instance);

        await sut.ReconcileExecutionJobAsync("job-missing");

        var stored = await jobStore.GetAsync("job-missing");
        stored.Should().NotBeNull();
        stored!.Status.Should().Be(ExecutionJobStatus.Failed);
        stored.CompletedAt.Should().NotBeNull();
        stored.ErrorMessage.Should().Contain("No batch compute backend registered");

        var progress = await progressStore.GetProgressAsync<GeoprocessingProgress>("job-missing");
        progress.Should().NotBeNull();
        progress!.WorkflowStatus.Should().Be(GeoprocessingWorkflowStatus.Failed);
        progress.CurrentStageStatus.Should().Be(GeoprocessingStageStatus.Failed);
    }

    [Fact]
    public async Task ReconcileExecutionJob_LocalQueuedJob_ObservesInsteadOfStarting()
    {
        var job = CreateJobRecord(
            operationId: "job-local",
            status: ExecutionJobStatus.Queued,
            backend: LocalBatchComputeBackend.BackendId,
            targetKind: BatchComputeTargetKind.KubernetesJob);
        var jobStore = new InMemoryExecutionJobStore(job);
        var progressStore = new InMemoryProgressStore();
        await progressStore.SetProgressAsync(
            "job-local",
            GeoprocessingProgress.CreateForSubmittedJob("job-local", "plan-local"));
        var backend = new LocalBatchComputeBackend(progressStore, Substitute.For<IJobCancellationNotifier>());
        var sut = new ExecutionJobReconciler(
            jobStore,
            [backend],
            progressStore,
            NullLogger<ExecutionJobReconciler>.Instance);

        await sut.ReconcileExecutionJobAsync("job-local");

        var stored = await jobStore.GetAsync("job-local");
        stored.Should().NotBeNull();
        stored!.Status.Should().Be(ExecutionJobStatus.Queued);
    }

    [Fact]
    public async Task ReconcileExecutionJob_LocalRunningWorkerProgress_BridgesToRunning()
    {
        var job = CreateJobRecord(
            operationId: "job-local-running",
            status: ExecutionJobStatus.Queued,
            backend: LocalBatchComputeBackend.BackendId,
            targetKind: BatchComputeTargetKind.KubernetesJob);
        var jobStore = new InMemoryExecutionJobStore(job);
        var progressStore = new InMemoryProgressStore();
        var workerProgress = GeoprocessingProgress.CreateForSubmittedJob("job-local-running", "plan-local") with
        {
            WorkflowStatus = GeoprocessingWorkflowStatus.Running,
            CurrentStageStatus = GeoprocessingStageStatus.Pending,
            CurrentPhase = "Executing buffer analysis"
        };
        await progressStore.SetProgressAsync("job-local-running", workerProgress);
        var backend = new LocalBatchComputeBackend(progressStore, Substitute.For<IJobCancellationNotifier>());
        var sut = new ExecutionJobReconciler(
            jobStore,
            [backend],
            progressStore,
            NullLogger<ExecutionJobReconciler>.Instance);

        await sut.ReconcileExecutionJobAsync("job-local-running");

        var stored = await jobStore.GetAsync("job-local-running");
        stored.Should().NotBeNull();
        stored!.Status.Should().Be(ExecutionJobStatus.Running);
        stored.CurrentPhase.Should().Be("Executing buffer analysis");
    }

    [Fact]
    public async Task ReconcileExecutionJob_UnclaimedLocalRetry_StaleRunningProgress_StaysQueuedAndResetsProgress()
    {
        var job = CreateJobRecord(
            operationId: "job-local-retry",
            status: ExecutionJobStatus.Queued,
            backend: LocalBatchComputeBackend.BackendId,
            targetKind: BatchComputeTargetKind.KubernetesJob) with
        {
            AttemptCount = 1
        };
        var jobStore = new InMemoryExecutionJobStore(job);
        var progressStore = new InMemoryProgressStore();
        var staleProgress = GeoprocessingProgress.CreateForSubmittedJob("job-local-retry", "plan-local") with
        {
            WorkflowStatus = GeoprocessingWorkflowStatus.Running,
            CurrentStageStatus = GeoprocessingStageStatus.Pending,
            CurrentPhase = "Prior attempt progress"
        };
        await progressStore.SetProgressAsync("job-local-retry", staleProgress);
        var backend = new LocalBatchComputeBackend(progressStore, Substitute.For<IJobCancellationNotifier>());
        var sut = new ExecutionJobReconciler(
            jobStore,
            [backend],
            progressStore,
            NullLogger<ExecutionJobReconciler>.Instance);

        await sut.ReconcileExecutionJobAsync("job-local-retry");

        var stored = await jobStore.GetAsync("job-local-retry");
        stored.Should().NotBeNull();
        stored!.Status.Should().Be(ExecutionJobStatus.Queued,
            "unclaimed local retries must be queue-authoritative and not promote from stale progress");
        stored.AttemptCount.Should().Be(1,
            "AttemptCount must not change when the retry is waiting for worker claim");

        var progress = await progressStore.GetProgressAsync<GeoprocessingProgress>("job-local-retry");
        progress.Should().NotBeNull();
        progress!.WorkflowStatus.Should().Be(GeoprocessingWorkflowStatus.AwaitingExecution,
            "stale Running progress must be reset to AwaitingExecution for admin endpoint consistency");
        progress.CurrentStageStatus.Should().Be(GeoprocessingStageStatus.Pending);
    }

    [Fact]
    public async Task ReconcileExecutionJob_UnclaimedLocalRetry_StaleTerminalProgress_StaysQueuedAndResetsProgress()
    {
        var job = CreateJobRecord(
            operationId: "job-local-retry-terminal",
            status: ExecutionJobStatus.Queued,
            backend: LocalBatchComputeBackend.BackendId,
            targetKind: BatchComputeTargetKind.KubernetesJob) with
        {
            AttemptCount = 1
        };
        var jobStore = new InMemoryExecutionJobStore(job);
        var progressStore = new InMemoryProgressStore();
        var staleProgress = GeoprocessingProgress.CreateForSubmittedJob("job-local-retry-terminal", "plan-local") with
        {
            WorkflowStatus = GeoprocessingWorkflowStatus.Failed,
            CurrentStageStatus = GeoprocessingStageStatus.Failed,
            ErrorMessage = "Prior attempt failed",
            StepsCompleted = 8,
            TotalSteps = 10
        };
        await progressStore.SetProgressAsync("job-local-retry-terminal", staleProgress);
        var backend = new LocalBatchComputeBackend(progressStore, Substitute.For<IJobCancellationNotifier>());
        var sut = new ExecutionJobReconciler(
            jobStore,
            [backend],
            progressStore,
            NullLogger<ExecutionJobReconciler>.Instance);

        await sut.ReconcileExecutionJobAsync("job-local-retry-terminal");

        var stored = await jobStore.GetAsync("job-local-retry-terminal");
        stored.Should().NotBeNull();
        stored!.Status.Should().Be(ExecutionJobStatus.Queued,
            "stale terminal progress from the prior attempt must not re-terminate a retried job");
        stored.AttemptCount.Should().Be(1,
            "AttemptCount must not change when the retry is waiting for worker claim");

        var progress = await progressStore.GetProgressAsync<GeoprocessingProgress>("job-local-retry-terminal");
        progress.Should().NotBeNull();
        progress!.WorkflowStatus.Should().Be(GeoprocessingWorkflowStatus.AwaitingExecution,
            "stale Failed progress must be reset to AwaitingExecution so admin endpoints do not report terminal state");
        progress.ErrorMessage.Should().BeNull(
            "prior-attempt error message must be cleared on progress reset");
        progress.StepsCompleted.Should().Be(0,
            "prior-attempt completion counter must be reset so PercentComplete does not report a stale percentage during requeue");
    }

    [Fact]
    public async Task ReconcileExecutionJob_UnclaimedLocalRetry_AlreadyAwaitingExecution_NoRedundantWrite()
    {
        var job = CreateJobRecord(
            operationId: "job-local-retry-idempotent",
            status: ExecutionJobStatus.Queued,
            backend: LocalBatchComputeBackend.BackendId,
            targetKind: BatchComputeTargetKind.KubernetesJob) with
        {
            AttemptCount = 1
        };
        var jobStore = new InMemoryExecutionJobStore(job);
        var progressStore = new InMemoryProgressStore();
        var freshProgress = GeoprocessingProgress.CreateForSubmittedJob("job-local-retry-idempotent", "plan-local");
        await progressStore.SetProgressAsync("job-local-retry-idempotent", freshProgress);
        var backend = new LocalBatchComputeBackend(progressStore, Substitute.For<IJobCancellationNotifier>());
        var sut = new ExecutionJobReconciler(
            jobStore,
            [backend],
            progressStore,
            NullLogger<ExecutionJobReconciler>.Instance);

        await sut.ReconcileExecutionJobAsync("job-local-retry-idempotent");

        var stored = await jobStore.GetAsync("job-local-retry-idempotent");
        stored.Should().NotBeNull();
        stored!.Status.Should().Be(ExecutionJobStatus.Queued);

        var progress = await progressStore.GetProgressAsync<GeoprocessingProgress>("job-local-retry-idempotent");
        progress.Should().NotBeNull();
        progress!.WorkflowStatus.Should().Be(GeoprocessingWorkflowStatus.AwaitingExecution,
            "already-correct progress must not be overwritten");
    }

    [Fact]
    public async Task ReconcileExecutionJob_UnclaimedLocalRetryWithCancellation_CancelsDirectly()
    {
        var notifier = Substitute.For<IJobCancellationNotifier>();
        var progressStore = new InMemoryProgressStore();
        var staleProgress = GeoprocessingProgress.CreateForSubmittedJob("job-retry-cancel", "plan-local") with
        {
            WorkflowStatus = GeoprocessingWorkflowStatus.Running,
            CurrentStageStatus = GeoprocessingStageStatus.Pending
        };
        await progressStore.SetProgressAsync("job-retry-cancel", staleProgress);
        var backend = new LocalBatchComputeBackend(progressStore, notifier);

        var job = CreateJobRecord(
            operationId: "job-retry-cancel",
            status: ExecutionJobStatus.Queued,
            backend: LocalBatchComputeBackend.BackendId,
            targetKind: BatchComputeTargetKind.KubernetesJob) with
        {
            AttemptCount = 1,
            CancellationRequestedAt = DateTimeOffset.UtcNow
        };
        var jobStore = new InMemoryExecutionJobStore(job);
        var sut = new ExecutionJobReconciler(
            jobStore,
            [backend],
            progressStore,
            NullLogger<ExecutionJobReconciler>.Instance);

        await sut.ReconcileExecutionJobAsync("job-retry-cancel");

        var stored = await jobStore.GetAsync("job-retry-cancel");
        stored.Should().NotBeNull();
        stored!.Status.Should().Be(ExecutionJobStatus.Cancelled,
            "cancellation must take precedence over unclaimed-retry logic");
        stored.CompletedAt.Should().NotBeNull();
        notifier.Received(1).Cancel("job-retry-cancel");
    }

    [Fact]
    public async Task ReconcileExecutionJob_LocalQueuedWithCancellation_CancelsDirectly()
    {
        var notifier = Substitute.For<IJobCancellationNotifier>();
        var progressStore = new InMemoryProgressStore();
        var staleProgress = GeoprocessingProgress.CreateForSubmittedJob("job-local-q-cancel", "plan-local") with
        {
            WorkflowStatus = GeoprocessingWorkflowStatus.Running,
            CurrentStageStatus = GeoprocessingStageStatus.Pending
        };
        await progressStore.SetProgressAsync("job-local-q-cancel", staleProgress);
        var backend = new LocalBatchComputeBackend(progressStore, notifier);

        var job = CreateJobRecord(
            operationId: "job-local-q-cancel",
            status: ExecutionJobStatus.Queued,
            backend: LocalBatchComputeBackend.BackendId,
            targetKind: BatchComputeTargetKind.KubernetesJob) with
        {
            CancellationRequestedAt = DateTimeOffset.UtcNow
        };
        var jobStore = new InMemoryExecutionJobStore(job);
        var sut = new ExecutionJobReconciler(
            jobStore,
            [backend],
            progressStore,
            NullLogger<ExecutionJobReconciler>.Instance);

        await sut.ReconcileExecutionJobAsync("job-local-q-cancel");

        var stored = await jobStore.GetAsync("job-local-q-cancel");
        stored.Should().NotBeNull();
        stored!.Status.Should().Be(ExecutionJobStatus.Cancelled,
            "Queued local jobs with cancellation must transition to Cancelled directly");
        stored.CompletedAt.Should().NotBeNull();
        notifier.Received(1).Cancel("job-local-q-cancel");
    }

    [Fact]
    public async Task ReconcileExecutionJob_NoOpObservation_SkipsPersistence()
    {
        var backend = Substitute.For<IBatchComputeBackend>();
        backend.BackendName.Returns("aws-batch");
        backend.TargetKind.Returns(BatchComputeTargetKind.AwsBatch);
        backend.ObserveAsync(Arg.Any<ExecutionJobRecord>(), Arg.Any<CancellationToken>())
            .Returns(new BatchComputeObservation
            {
                Status = ExecutionJobStatus.Running,
                ProviderOperationId = "provider-noop",
                PercentComplete = 25,
                Message = "Processing"
            });

        var originalUpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        var job = CreateJobRecord(
            operationId: "job-noop",
            status: ExecutionJobStatus.Running,
            backend: "aws-batch",
            targetKind: BatchComputeTargetKind.AwsBatch,
            percentComplete: 25,
            currentPhase: "Processing") with
        {
            ProviderOperationId = "provider-noop",
            UpdatedAt = originalUpdatedAt
        };
        var jobStore = new InMemoryExecutionJobStore(job);
        var progressStore = new InMemoryProgressStore();
        var sut = new ExecutionJobReconciler(
            jobStore,
            [backend],
            progressStore,
            NullLogger<ExecutionJobReconciler>.Instance);

        await sut.ReconcileExecutionJobAsync("job-noop");

        var stored = await jobStore.GetAsync("job-noop");
        stored.Should().NotBeNull();
        stored!.UpdatedAt.Should().Be(originalUpdatedAt,
            "no-op observations must not update the record");
    }

    [Fact]
    public async Task ReconcileExecutionJob_RemoteObservation_BridgesProgressAndCompletesJob()
    {
        var backend = Substitute.For<IBatchComputeBackend>();
        backend.BackendName.Returns("aws-batch");
        backend.TargetKind.Returns(BatchComputeTargetKind.AwsBatch);
        backend.ObserveAsync(Arg.Any<ExecutionJobRecord>(), Arg.Any<CancellationToken>())
            .Returns(new BatchComputeObservation
            {
                Status = ExecutionJobStatus.Succeeded,
                ProviderOperationId = "provider-123",
                PercentComplete = 100,
                Message = "Execution complete"
            });

        var job = CreateJobRecord(
            operationId: "job-remote",
            status: ExecutionJobStatus.Running,
            backend: "aws-batch",
            targetKind: BatchComputeTargetKind.AwsBatch,
            percentComplete: 25,
            currentPhase: "Running");
        var jobStore = new InMemoryExecutionJobStore(job);
        var progressStore = new InMemoryProgressStore();
        await progressStore.SetProgressAsync(
            "job-remote",
            GeoprocessingProgress.CreateForSubmittedJob("job-remote", "plan-remote"));
        var sut = new ExecutionJobReconciler(
            jobStore,
            [backend],
            progressStore,
            NullLogger<ExecutionJobReconciler>.Instance);

        await sut.ReconcileExecutionJobAsync("job-remote");

        var stored = await jobStore.GetAsync("job-remote");
        stored.Should().NotBeNull();
        stored!.Status.Should().Be(ExecutionJobStatus.Succeeded);
        stored.PercentComplete.Should().Be(100);
        stored.ProviderOperationId.Should().Be("provider-123");
        stored.CompletedAt.Should().NotBeNull();

        var progress = await progressStore.GetProgressAsync<GeoprocessingProgress>("job-remote");
        progress.Should().NotBeNull();
        progress!.PlanId.Should().Be("plan-remote");
        progress.WorkflowStatus.Should().Be(GeoprocessingWorkflowStatus.Completed);
        progress.CurrentStageStatus.Should().Be(GeoprocessingStageStatus.Completed);
        progress.PercentComplete.Should().Be(100);
        progress.CurrentPhase.Should().Be("Execution complete");
        progress.CompletedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task ReconcileExecutionJob_CancellationRequested_DelegatesToBackendCancel()
    {
        var backend = Substitute.For<IBatchComputeBackend>();
        backend.BackendName.Returns("aws-batch");
        backend.TargetKind.Returns(BatchComputeTargetKind.AwsBatch);
        backend.GetCapabilitiesAsync(Arg.Any<CancellationToken>())
            .Returns(new BatchComputeBackendCapabilities { SupportsCancellation = true });
        backend.CancelAsync(Arg.Any<ExecutionJobRecord>(), Arg.Any<CancellationToken>())
            .Returns(new BatchComputeObservation
            {
                Status = ExecutionJobStatus.Cancelled,
                ProviderOperationId = "provider-cancel-1",
                Message = "Cancelled by provider"
            });

        var job = CreateJobRecord(
            operationId: "job-cancel-remote",
            status: ExecutionJobStatus.Running,
            backend: "aws-batch",
            targetKind: BatchComputeTargetKind.AwsBatch,
            currentPhase: "Running") with
        {
            CancellationRequestedAt = DateTimeOffset.UtcNow
        };
        var jobStore = new InMemoryExecutionJobStore(job);
        var progressStore = new InMemoryProgressStore();
        await progressStore.SetProgressAsync(
            "job-cancel-remote",
            GeoprocessingProgress.CreateForSubmittedJob("job-cancel-remote", "plan-cancel"));
        var sut = new ExecutionJobReconciler(
            jobStore,
            [backend],
            progressStore,
            NullLogger<ExecutionJobReconciler>.Instance);

        await sut.ReconcileExecutionJobAsync("job-cancel-remote");

        var stored = await jobStore.GetAsync("job-cancel-remote");
        stored.Should().NotBeNull();
        stored!.Status.Should().Be(ExecutionJobStatus.Cancelled);
        stored.CompletedAt.Should().NotBeNull();
        stored.ProviderOperationId.Should().Be("provider-cancel-1");

        await backend.Received(1).CancelAsync(
            Arg.Any<ExecutionJobRecord>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReconcileExecutionJob_CancelReturnsFailed_PreservesPercentCompleteAndErrorMessage()
    {
        var backend = Substitute.For<IBatchComputeBackend>();
        backend.BackendName.Returns("aws-batch");
        backend.TargetKind.Returns(BatchComputeTargetKind.AwsBatch);
        backend.GetCapabilitiesAsync(Arg.Any<CancellationToken>())
            .Returns(new BatchComputeBackendCapabilities { SupportsCancellation = true });
        backend.CancelAsync(Arg.Any<ExecutionJobRecord>(), Arg.Any<CancellationToken>())
            .Returns(new BatchComputeObservation
            {
                Status = ExecutionJobStatus.Failed,
                ProviderOperationId = "provider-fail-1",
                PercentComplete = 42.5,
                Message = "Job failed before cancel was applied"
            });

        var job = CreateJobRecord(
            operationId: "job-cancel-failed",
            status: ExecutionJobStatus.Running,
            backend: "aws-batch",
            targetKind: BatchComputeTargetKind.AwsBatch,
            percentComplete: 10.0,
            currentPhase: "Processing") with
        {
            CancellationRequestedAt = DateTimeOffset.UtcNow
        };
        var jobStore = new InMemoryExecutionJobStore(job);
        var progressStore = new InMemoryProgressStore();
        await progressStore.SetProgressAsync(
            "job-cancel-failed",
            GeoprocessingProgress.CreateForSubmittedJob("job-cancel-failed", "plan-cancel-fail"));
        var sut = new ExecutionJobReconciler(
            jobStore,
            [backend],
            progressStore,
            NullLogger<ExecutionJobReconciler>.Instance);

        await sut.ReconcileExecutionJobAsync("job-cancel-failed");

        var stored = await jobStore.GetAsync("job-cancel-failed");
        stored.Should().NotBeNull();
        stored!.Status.Should().Be(ExecutionJobStatus.Failed);
        stored.CompletedAt.Should().NotBeNull();
        stored.PercentComplete.Should().Be(42.5);
        stored.ErrorMessage.Should().Be("Job failed before cancel was applied");
        stored.CurrentPhase.Should().Be("Job failed before cancel was applied");
        stored.ProviderOperationId.Should().Be("provider-fail-1");

        var progress = await progressStore.GetProgressAsync<GeoprocessingProgress>("job-cancel-failed");
        progress.Should().NotBeNull();
        progress!.WorkflowStatus.Should().Be(GeoprocessingWorkflowStatus.Failed);
        progress.ErrorMessage.Should().Be("Job failed before cancel was applied");
    }

    [Fact]
    public async Task ReconcileExecutionJob_CancellationRequestedButUnsupported_FallsBackToObserve()
    {
        var backend = Substitute.For<IBatchComputeBackend>();
        backend.BackendName.Returns("aws-batch");
        backend.TargetKind.Returns(BatchComputeTargetKind.AwsBatch);
        backend.GetCapabilitiesAsync(Arg.Any<CancellationToken>())
            .Returns(new BatchComputeBackendCapabilities { SupportsCancellation = false });
        backend.ObserveAsync(Arg.Any<ExecutionJobRecord>(), Arg.Any<CancellationToken>())
            .Returns(new BatchComputeObservation
            {
                Status = ExecutionJobStatus.Running,
                PercentComplete = 50,
                Message = "Still running"
            });

        var job = CreateJobRecord(
            operationId: "job-cancel-unsupported",
            status: ExecutionJobStatus.Running,
            backend: "aws-batch",
            targetKind: BatchComputeTargetKind.AwsBatch) with
        {
            CancellationRequestedAt = DateTimeOffset.UtcNow
        };
        var jobStore = new InMemoryExecutionJobStore(job);
        var progressStore = new InMemoryProgressStore();
        var sut = new ExecutionJobReconciler(
            jobStore,
            [backend],
            progressStore,
            NullLogger<ExecutionJobReconciler>.Instance);

        await sut.ReconcileExecutionJobAsync("job-cancel-unsupported");

        var stored = await jobStore.GetAsync("job-cancel-unsupported");
        stored.Should().NotBeNull();
        stored!.Status.Should().Be(ExecutionJobStatus.Running);
        stored.PercentComplete.Should().Be(50);

        await backend.DidNotReceive().CancelAsync(
            Arg.Any<ExecutionJobRecord>(),
            Arg.Any<CancellationToken>());
        await backend.Received(1).ObserveAsync(
            Arg.Any<ExecutionJobRecord>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReconcileExecutionJob_RemoteQueuedWithProviderOperationId_ObservesInsteadOfRestarting()
    {
        var backend = Substitute.For<IBatchComputeBackend>();
        backend.BackendName.Returns("aws-batch");
        backend.TargetKind.Returns(BatchComputeTargetKind.AwsBatch);
        backend.ObserveAsync(Arg.Any<ExecutionJobRecord>(), Arg.Any<CancellationToken>())
            .Returns(new BatchComputeObservation
            {
                Status = ExecutionJobStatus.Running,
                ProviderOperationId = "provider-already-submitted",
                PercentComplete = 10,
                Message = "Starting up"
            });

        var job = CreateJobRecord(
            operationId: "job-remote-queued",
            status: ExecutionJobStatus.Queued,
            backend: "aws-batch",
            targetKind: BatchComputeTargetKind.AwsBatch) with
        {
            ProviderOperationId = "provider-already-submitted"
        };
        var jobStore = new InMemoryExecutionJobStore(job);
        var progressStore = new InMemoryProgressStore();
        var sut = new ExecutionJobReconciler(
            jobStore,
            [backend],
            progressStore,
            NullLogger<ExecutionJobReconciler>.Instance);

        await sut.ReconcileExecutionJobAsync("job-remote-queued");

        await backend.Received(1).ObserveAsync(
            Arg.Any<ExecutionJobRecord>(),
            Arg.Any<CancellationToken>());
        await backend.DidNotReceive().StartAsync(
            Arg.Any<ExecutionJobRecord>(),
            Arg.Any<CancellationToken>());

        var stored = await jobStore.GetAsync("job-remote-queued");
        stored.Should().NotBeNull();
        stored!.Status.Should().Be(ExecutionJobStatus.Running);
    }

    [Fact]
    public async Task ReconcileExecutionJob_RemoteQueuedWithAttemptCountButNoProviderId_ObservesInsteadOfRestarting()
    {
        var backend = Substitute.For<IBatchComputeBackend>();
        backend.BackendName.Returns("aws-batch");
        backend.TargetKind.Returns(BatchComputeTargetKind.AwsBatch);
        backend.ObserveAsync(Arg.Any<ExecutionJobRecord>(), Arg.Any<CancellationToken>())
            .Returns(new BatchComputeObservation
            {
                Status = ExecutionJobStatus.Provisioning,
                PercentComplete = 0,
                Message = "Provisioning resources"
            });

        var job = CreateJobRecord(
            operationId: "job-attempt-marker",
            status: ExecutionJobStatus.Queued,
            backend: "aws-batch",
            targetKind: BatchComputeTargetKind.AwsBatch) with
        {
            AttemptCount = 1
        };
        var jobStore = new InMemoryExecutionJobStore(job);
        var progressStore = new InMemoryProgressStore();
        var sut = new ExecutionJobReconciler(
            jobStore,
            [backend],
            progressStore,
            NullLogger<ExecutionJobReconciler>.Instance);

        await sut.ReconcileExecutionJobAsync("job-attempt-marker");

        await backend.Received(1).ObserveAsync(
            Arg.Any<ExecutionJobRecord>(),
            Arg.Any<CancellationToken>());
        await backend.DidNotReceive().StartAsync(
            Arg.Any<ExecutionJobRecord>(),
            Arg.Any<CancellationToken>());

        var stored = await jobStore.GetAsync("job-attempt-marker");
        stored.Should().NotBeNull();
        stored!.Status.Should().Be(ExecutionJobStatus.Provisioning);
    }

    [Fact]
    public async Task ReconcileExecutionJob_RemoteProvisioningJob_ObservesInsteadOfRestarting()
    {
        var backend = Substitute.For<IBatchComputeBackend>();
        backend.BackendName.Returns("aws-batch");
        backend.TargetKind.Returns(BatchComputeTargetKind.AwsBatch);
        backend.ObserveAsync(Arg.Any<ExecutionJobRecord>(), Arg.Any<CancellationToken>())
            .Returns(new BatchComputeObservation
            {
                Status = ExecutionJobStatus.Running,
                ProviderOperationId = "provider-mid-submit",
                PercentComplete = 0,
                Message = "Starting up"
            });

        var job = CreateJobRecord(
            operationId: "job-provisioning",
            status: ExecutionJobStatus.Provisioning,
            backend: "aws-batch",
            targetKind: BatchComputeTargetKind.AwsBatch);
        var jobStore = new InMemoryExecutionJobStore(job);
        var progressStore = new InMemoryProgressStore();
        var sut = new ExecutionJobReconciler(
            jobStore,
            [backend],
            progressStore,
            NullLogger<ExecutionJobReconciler>.Instance);

        await sut.ReconcileExecutionJobAsync("job-provisioning");

        await backend.Received(1).ObserveAsync(
            Arg.Any<ExecutionJobRecord>(),
            Arg.Any<CancellationToken>());
        await backend.DidNotReceive().StartAsync(
            Arg.Any<ExecutionJobRecord>(),
            Arg.Any<CancellationToken>());

        var stored = await jobStore.GetAsync("job-provisioning");
        stored.Should().NotBeNull();
        stored!.Status.Should().Be(ExecutionJobStatus.Running);
    }

    [Fact]
    public async Task ReconcileExecutionJob_StartJobAsync_IncrementsAttemptCount()
    {
        var backend = Substitute.For<IBatchComputeBackend>();
        backend.BackendName.Returns("aws-batch");
        backend.TargetKind.Returns(BatchComputeTargetKind.AwsBatch);
        backend.StartAsync(Arg.Any<ExecutionJobRecord>(), Arg.Any<CancellationToken>())
            .Returns(new BatchComputeSubmissionResult
            {
                Status = ExecutionJobStatus.Queued,
                Message = "Queued on provider"
            });

        var job = CreateJobRecord(
            operationId: "job-start-attempt",
            status: ExecutionJobStatus.Queued,
            backend: "aws-batch",
            targetKind: BatchComputeTargetKind.AwsBatch);
        var jobStore = new InMemoryExecutionJobStore(job);
        var progressStore = new InMemoryProgressStore();
        var sut = new ExecutionJobReconciler(
            jobStore,
            [backend],
            progressStore,
            NullLogger<ExecutionJobReconciler>.Instance);

        await sut.ReconcileExecutionJobAsync("job-start-attempt");

        var stored = await jobStore.GetAsync("job-start-attempt");
        stored.Should().NotBeNull();
        stored!.AttemptCount.Should().Be(1);
        stored.CurrentPhase.Should().Be("Queued on provider");
    }

    [Fact]
    public async Task ReconcileExecutionJob_LocalCancellationRequested_DoesNotSynthesizeTerminalState()
    {
        var notifier = Substitute.For<IJobCancellationNotifier>();
        notifier.Cancel("job-local-cancel").Returns(true);
        var progressStore = new InMemoryProgressStore();
        var workerProgress = GeoprocessingProgress.CreateForSubmittedJob("job-local-cancel", "plan-local") with
        {
            WorkflowStatus = GeoprocessingWorkflowStatus.Running,
            CurrentStageStatus = GeoprocessingStageStatus.Pending
        };
        await progressStore.SetProgressAsync("job-local-cancel", workerProgress);
        var backend = new LocalBatchComputeBackend(progressStore, notifier);

        var job = CreateJobRecord(
            operationId: "job-local-cancel",
            status: ExecutionJobStatus.Running,
            backend: LocalBatchComputeBackend.BackendId,
            targetKind: BatchComputeTargetKind.KubernetesJob,
            currentPhase: "Executing") with
        {
            CancellationRequestedAt = DateTimeOffset.UtcNow
        };
        var jobStore = new InMemoryExecutionJobStore(job);
        var sut = new ExecutionJobReconciler(
            jobStore,
            [backend],
            progressStore,
            NullLogger<ExecutionJobReconciler>.Instance);

        await sut.ReconcileExecutionJobAsync("job-local-cancel");

        var stored = await jobStore.GetAsync("job-local-cancel");
        stored.Should().NotBeNull();
        stored!.Status.Should().Be(ExecutionJobStatus.Running,
            "the worker owns the terminal state transition, not the reconciler");
    }

    [Fact]
    public async Task ReconcileExecutionJob_QueuedWithCancellationRequested_CancelledWithoutStarting()
    {
        var backend = Substitute.For<IBatchComputeBackend>();
        backend.BackendName.Returns("aws-batch");
        backend.TargetKind.Returns(BatchComputeTargetKind.AwsBatch);

        var job = CreateJobRecord(
            operationId: "job-queued-cancel",
            status: ExecutionJobStatus.Queued,
            backend: "aws-batch",
            targetKind: BatchComputeTargetKind.AwsBatch) with
        {
            CancellationRequestedAt = DateTimeOffset.UtcNow
        };
        var jobStore = new InMemoryExecutionJobStore(job);
        var progressStore = new InMemoryProgressStore();
        await progressStore.SetProgressAsync(
            "job-queued-cancel",
            GeoprocessingProgress.CreateForSubmittedJob("job-queued-cancel", "plan-cancel"));
        var sut = new ExecutionJobReconciler(
            jobStore,
            [backend],
            progressStore,
            NullLogger<ExecutionJobReconciler>.Instance);

        await sut.ReconcileExecutionJobAsync("job-queued-cancel");

        var stored = await jobStore.GetAsync("job-queued-cancel");
        stored.Should().NotBeNull();
        stored!.Status.Should().Be(ExecutionJobStatus.Cancelled,
            "queued jobs with CancellationRequestedAt must not be started");
        stored.CompletedAt.Should().NotBeNull();

        await backend.DidNotReceive().StartAsync(
            Arg.Any<ExecutionJobRecord>(),
            Arg.Any<CancellationToken>());

        var progress = await progressStore.GetProgressAsync<GeoprocessingProgress>("job-queued-cancel");
        progress.Should().NotBeNull();
        progress!.WorkflowStatus.Should().Be(GeoprocessingWorkflowStatus.Cancelled);
    }

    [Fact]
    public async Task ReconcileExecutionJob_QueuedRemoteWithCancelAndProviderMarker_CancelsViaBackend()
    {
        var backend = Substitute.For<IBatchComputeBackend>();
        backend.BackendName.Returns("aws-batch");
        backend.TargetKind.Returns(BatchComputeTargetKind.AwsBatch);
        backend.GetCapabilitiesAsync(Arg.Any<CancellationToken>())
            .Returns(new BatchComputeBackendCapabilities { SupportsCancellation = true });
        backend.CancelAsync(Arg.Any<ExecutionJobRecord>(), Arg.Any<CancellationToken>())
            .Returns(new BatchComputeObservation
            {
                Status = ExecutionJobStatus.Cancelled,
                ProviderOperationId = "provider-cancel-queued",
                Message = "Cancelled by backend"
            });

        var job = CreateJobRecord(
            operationId: "job-queued-remote-cancel",
            status: ExecutionJobStatus.Queued,
            backend: "aws-batch",
            targetKind: BatchComputeTargetKind.AwsBatch) with
        {
            ProviderOperationId = "provider-cancel-queued",
            AttemptCount = 1,
            CancellationRequestedAt = DateTimeOffset.UtcNow
        };
        var jobStore = new InMemoryExecutionJobStore(job);
        var progressStore = new InMemoryProgressStore();
        await progressStore.SetProgressAsync(
            "job-queued-remote-cancel",
            GeoprocessingProgress.CreateForSubmittedJob("job-queued-remote-cancel", "plan-cancel"));
        var sut = new ExecutionJobReconciler(
            jobStore,
            [backend],
            progressStore,
            NullLogger<ExecutionJobReconciler>.Instance);

        await sut.ReconcileExecutionJobAsync("job-queued-remote-cancel");

        var stored = await jobStore.GetAsync("job-queued-remote-cancel");
        stored.Should().NotBeNull();
        stored!.Status.Should().Be(ExecutionJobStatus.Cancelled,
            "queued remote jobs with CancellationRequestedAt and a provider marker must cancel via backend, not observe");
        stored.CompletedAt.Should().NotBeNull();

        await backend.Received(1).CancelAsync(
            Arg.Any<ExecutionJobRecord>(),
            Arg.Any<CancellationToken>());
        await backend.DidNotReceive().ObserveAsync(
            Arg.Any<ExecutionJobRecord>(),
            Arg.Any<CancellationToken>());
        await backend.DidNotReceive().StartAsync(
            Arg.Any<ExecutionJobRecord>(),
            Arg.Any<CancellationToken>());

        var progress = await progressStore.GetProgressAsync<GeoprocessingProgress>("job-queued-remote-cancel");
        progress.Should().NotBeNull();
        progress!.WorkflowStatus.Should().Be(GeoprocessingWorkflowStatus.Cancelled);
    }

    [Fact]
    public async Task ReconcileExecutionJob_QueuedRemoteCancelWithoutObservationChange_PreservesUpdatedAt()
    {
        const string providerOperationId = "provider-cancel-queued";
        const string phase = "Azure Batch termination requested for job 'provider-cancel-queued'; Azure Batch job 'provider-cancel-queued' has not yet registered with the scheduler.";
        var originalUpdatedAt = DateTimeOffset.UtcNow - AzureBatchComputeBackend.MissingRegistrationGracePeriod - TimeSpan.FromSeconds(5);

        var backend = Substitute.For<IBatchComputeBackend>();
        backend.BackendName.Returns(AzureBatchComputeBackend.BackendIdentifier);
        backend.TargetKind.Returns(BatchComputeTargetKind.AzureBatch);
        backend.GetCapabilitiesAsync(Arg.Any<CancellationToken>())
            .Returns(new BatchComputeBackendCapabilities { SupportsCancellation = true });
        backend.CancelAsync(Arg.Any<ExecutionJobRecord>(), Arg.Any<CancellationToken>())
            .Returns(new BatchComputeObservation
            {
                Status = ExecutionJobStatus.Queued,
                ProviderOperationId = providerOperationId,
                Message = phase
            });

        var job = CreateJobRecord(
            operationId: "job-queued-azure-cancel",
            status: ExecutionJobStatus.Queued,
            backend: AzureBatchComputeBackend.BackendIdentifier,
            targetKind: BatchComputeTargetKind.AzureBatch,
            currentPhase: phase) with
        {
            ProviderOperationId = providerOperationId,
            AttemptCount = 1,
            CancellationRequestedAt = DateTimeOffset.UtcNow,
            UpdatedAt = originalUpdatedAt
        };
        var jobStore = new InMemoryExecutionJobStore(job);
        var progressStore = new InMemoryProgressStore();
        var sut = new ExecutionJobReconciler(
            jobStore,
            [backend],
            progressStore,
            NullLogger<ExecutionJobReconciler>.Instance);

        await sut.ReconcileExecutionJobAsync("job-queued-azure-cancel");

        var stored = await jobStore.GetAsync("job-queued-azure-cancel");
        stored.Should().NotBeNull();
        stored!.Status.Should().Be(ExecutionJobStatus.Queued);
        stored.UpdatedAt.Should().Be(originalUpdatedAt,
            "identical cancel observations must not refresh the missing-registration grace window");

        await backend.Received(1).CancelAsync(
            Arg.Any<ExecutionJobRecord>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReconcileExecutionJob_ExceptionDuringReconciliation_BridgesProgressOnFailure()
    {
        var backend = Substitute.For<IBatchComputeBackend>();
        backend.BackendName.Returns("aws-batch");
        backend.TargetKind.Returns(BatchComputeTargetKind.AwsBatch);
        backend.ObserveAsync(Arg.Any<ExecutionJobRecord>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("backend exploded"));

        var job = CreateJobRecord(
            operationId: "job-exception",
            status: ExecutionJobStatus.Running,
            backend: "aws-batch",
            targetKind: BatchComputeTargetKind.AwsBatch);
        var jobStore = new InMemoryExecutionJobStore(job);
        var progressStore = new InMemoryProgressStore();
        await progressStore.SetProgressAsync(
            "job-exception",
            GeoprocessingProgress.CreateForSubmittedJob("job-exception", "plan-exception"));
        var sut = new ExecutionJobReconciler(
            jobStore,
            [backend],
            progressStore,
            NullLogger<ExecutionJobReconciler>.Instance);

        var act = () => sut.ReconcileExecutionJobAsync("job-exception");
        await act.Should().ThrowAsync<InvalidOperationException>();

        var stored = await jobStore.GetAsync("job-exception");
        stored.Should().NotBeNull();
        stored!.Status.Should().Be(ExecutionJobStatus.Failed);

        var progress = await progressStore.GetProgressAsync<GeoprocessingProgress>("job-exception");
        progress.Should().NotBeNull();
        progress!.WorkflowStatus.Should().Be(GeoprocessingWorkflowStatus.Failed);
        progress.CurrentStageStatus.Should().Be(GeoprocessingStageStatus.Failed);
    }

    [Fact]
    public async Task ReconcileExecutionJob_CasConflict_DoesNotOverwriteConcurrentWrite()
    {
        var backend = Substitute.For<IBatchComputeBackend>();
        backend.BackendName.Returns("aws-batch");
        backend.TargetKind.Returns(BatchComputeTargetKind.AwsBatch);
        backend.ObserveAsync(Arg.Any<ExecutionJobRecord>(), Arg.Any<CancellationToken>())
            .Returns(new BatchComputeObservation
            {
                Status = ExecutionJobStatus.Running,
                PercentComplete = 50,
                Message = "Processing"
            });

        var job = CreateJobRecord(
            operationId: "job-cas",
            status: ExecutionJobStatus.Running,
            backend: "aws-batch",
            targetKind: BatchComputeTargetKind.AwsBatch);

        var jobStore = Substitute.For<IExecutionJobStore>();
        jobStore.TryAcquireLeaseAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(true);
        jobStore.RenewLeaseAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(true);
        jobStore.GetAsync("job-cas", Arg.Any<CancellationToken>())
            .Returns(job);
        jobStore.TrySetAsync(Arg.Any<ExecutionJobRecord>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>())
            .Returns(false);

        var progressStore = new InMemoryProgressStore();
        var sut = new ExecutionJobReconciler(
            jobStore,
            [backend],
            progressStore,
            NullLogger<ExecutionJobReconciler>.Instance);

        await sut.ReconcileExecutionJobAsync("job-cas");

        await jobStore.DidNotReceive().SetAsync(
            Arg.Any<ExecutionJobRecord>(),
            Arg.Any<TimeSpan?>(),
            Arg.Any<CancellationToken>());

        var progress = await progressStore.GetProgressAsync<GeoprocessingProgress>("job-cas");
        progress.Should().BeNull();
    }

    [Fact]
    public async Task BridgeTerminalSubmissionProgress_TerminalJob_UpdatesGeoprocessingProgress()
    {
        var progressStore = new InMemoryProgressStore();
        var initialProgress = GeoprocessingProgress.CreateForSubmittedJob("job-terminal", "plan-terminal");
        await progressStore.SetProgressAsync("job-terminal", initialProgress);

        var terminalJob = CreateJobRecord(
            operationId: "job-terminal",
            status: ExecutionJobStatus.Failed,
            backend: "aws-batch",
            targetKind: BatchComputeTargetKind.AwsBatch,
            currentPhase: "Backend rejected the request") with
        {
            CompletedAt = DateTimeOffset.UtcNow,
            ErrorMessage = "Backend rejected the request"
        };

        await ExecutionJobSubmissionHelper.BridgeTerminalSubmissionProgressAsync(
            progressStore, terminalJob, TimeSpan.FromDays(7));

        var progress = await progressStore.GetProgressAsync<GeoprocessingProgress>("job-terminal");
        progress.Should().NotBeNull();
        progress!.WorkflowStatus.Should().Be(GeoprocessingWorkflowStatus.Failed);
        progress.CurrentStageStatus.Should().Be(GeoprocessingStageStatus.Failed);
        progress.CompletedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task TryRollbackCreatedJob_ProvisioningJob_RollsBackToFailed()
    {
        var job = CreateJobRecord(
            operationId: "job-provisioning-rollback",
            status: ExecutionJobStatus.Provisioning,
            backend: "aws-batch",
            targetKind: BatchComputeTargetKind.AwsBatch);
        var jobStore = new InMemoryExecutionJobStore(job);

        await ExecutionJobSubmissionHelper.TryRollbackCreatedJobAsync(
            jobStore, "job-provisioning-rollback");

        var stored = await jobStore.GetAsync("job-provisioning-rollback");
        stored.Should().NotBeNull();
        stored!.Status.Should().Be(ExecutionJobStatus.Failed,
            "Provisioning jobs that failed mid-submission must be rolled back");
        stored.ErrorMessage.Should().Be(ExecutionJobSubmissionHelper.SubmissionFailureMessage);
    }

    [Fact]
    public async Task TryRollbackCreatedJob_WithProgressStore_BridgesProgressToFailed()
    {
        var job = CreateJobRecord(
            operationId: "job-rollback-progress",
            status: ExecutionJobStatus.Provisioning,
            backend: "aws-batch",
            targetKind: BatchComputeTargetKind.AwsBatch);
        var jobStore = new InMemoryExecutionJobStore(job);
        var progressStore = new InMemoryProgressStore();
        var initialProgress = GeoprocessingProgress.CreateForSubmittedJob("job-rollback-progress", "plan-rollback");
        await progressStore.SetProgressAsync("job-rollback-progress", initialProgress);

        await ExecutionJobSubmissionHelper.TryRollbackCreatedJobAsync(
            jobStore, "job-rollback-progress",
            progressStore: progressStore,
            progressRetention: TimeSpan.FromDays(7));

        var progress = await progressStore.GetProgressAsync<GeoprocessingProgress>("job-rollback-progress");
        progress.Should().NotBeNull();
        progress!.WorkflowStatus.Should().Be(GeoprocessingWorkflowStatus.Failed,
            "Rollback must bridge progress to Failed to prevent zombie active operations");
        progress.CurrentStageStatus.Should().Be(GeoprocessingStageStatus.Failed);
        progress.CompletedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task TryRollbackCreatedJob_WithCustomMessage_RecordsActualFailure()
    {
        var job = CreateJobRecord(
            operationId: "job-custom-msg",
            status: ExecutionJobStatus.Queued,
            backend: "aws-batch",
            targetKind: BatchComputeTargetKind.AwsBatch);
        var jobStore = new InMemoryExecutionJobStore(job);

        await ExecutionJobSubmissionHelper.TryRollbackCreatedJobAsync(
            jobStore, "job-custom-msg",
            failureMessage: "Submission failed: Backend connection refused");

        var stored = await jobStore.GetAsync("job-custom-msg");
        stored.Should().NotBeNull();
        stored!.Status.Should().Be(ExecutionJobStatus.Failed);
        stored.ErrorMessage.Should().Be("Submission failed: Backend connection refused");
    }

    [Fact]
    public async Task TryRollbackCreatedJob_LostCas_DoesNotBridgeProgress()
    {
        var job = CreateJobRecord(
            operationId: "job-cas-lost",
            status: ExecutionJobStatus.Provisioning,
            backend: "aws-batch",
            targetKind: BatchComputeTargetKind.AwsBatch);
        var jobStore = new CasRejectingJobStore(job);
        var progressStore = new InMemoryProgressStore();
        var initialProgress = GeoprocessingProgress.CreateForSubmittedJob("job-cas-lost", "plan-cas");
        await progressStore.SetProgressAsync("job-cas-lost", initialProgress);

        await ExecutionJobSubmissionHelper.TryRollbackCreatedJobAsync(
            jobStore, "job-cas-lost",
            progressStore: progressStore,
            progressRetention: TimeSpan.FromDays(7));

        var progress = await progressStore.GetProgressAsync<GeoprocessingProgress>("job-cas-lost");
        progress.Should().NotBeNull();
        progress!.WorkflowStatus.Should().Be(GeoprocessingWorkflowStatus.AwaitingExecution,
            "Progress must not flip to Failed when the durable rollback lost the CAS race");
    }

    [Fact]
    public async Task BridgeTerminalSubmissionProgress_NonTerminalJob_DoesNotModifyProgress()
    {
        var progressStore = new InMemoryProgressStore();
        var initialProgress = GeoprocessingProgress.CreateForSubmittedJob("job-running", "plan-running");
        await progressStore.SetProgressAsync("job-running", initialProgress);

        var runningJob = CreateJobRecord(
            operationId: "job-running",
            status: ExecutionJobStatus.Running,
            backend: "aws-batch",
            targetKind: BatchComputeTargetKind.AwsBatch);

        await ExecutionJobSubmissionHelper.BridgeTerminalSubmissionProgressAsync(
            progressStore, runningJob, TimeSpan.FromDays(7));

        var progress = await progressStore.GetProgressAsync<GeoprocessingProgress>("job-running");
        progress.Should().NotBeNull();
        progress!.WorkflowStatus.Should().Be(GeoprocessingWorkflowStatus.AwaitingExecution);
    }

    [Fact]
    public async Task StartOnRemoteBackendAsync_ProvisioningCasConflict_TerminalCurrent_BridgesProgress()
    {
        var terminalJob = CreateJobRecord(
            operationId: "job-cas-prov",
            status: ExecutionJobStatus.Cancelled,
            backend: "aws-batch",
            targetKind: BatchComputeTargetKind.AwsBatch) with
        {
            CompletedAt = DateTimeOffset.UtcNow,
            CurrentPhase = "Cancelled externally"
        };

        var jobStore = Substitute.For<IExecutionJobStore>();
        jobStore.TrySetAsync(Arg.Any<ExecutionJobRecord>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>())
            .Returns(false);
        jobStore.GetAsync("job-cas-prov", Arg.Any<CancellationToken>())
            .Returns(terminalJob);

        var backend = Substitute.For<IBatchComputeBackend>();
        var progressStore = new InMemoryProgressStore();
        var initialProgress = GeoprocessingProgress.CreateForSubmittedJob("job-cas-prov", "plan-cas-prov");
        await progressStore.SetProgressAsync("job-cas-prov", initialProgress);

        var queuedJob = CreateJobRecord(
            operationId: "job-cas-prov",
            status: ExecutionJobStatus.Queued,
            backend: "aws-batch",
            targetKind: BatchComputeTargetKind.AwsBatch);

        await ExecutionJobSubmissionHelper.StartOnRemoteBackendAsync(
            queuedJob, backend, jobStore, progressStore, TimeSpan.FromDays(7), null, CancellationToken.None);

        var progress = await progressStore.GetProgressAsync<GeoprocessingProgress>("job-cas-prov");
        progress.Should().NotBeNull();
        progress!.WorkflowStatus.Should().Be(GeoprocessingWorkflowStatus.Cancelled,
            "CAS conflict with terminal current record must bridge progress");
    }

    [Fact]
    public async Task StartOnRemoteBackendAsync_PostStartCasConflict_TerminalCurrent_BridgesProgress()
    {
        var terminalJob = CreateJobRecord(
            operationId: "job-cas-post",
            status: ExecutionJobStatus.Failed,
            backend: "aws-batch",
            targetKind: BatchComputeTargetKind.AwsBatch) with
        {
            CompletedAt = DateTimeOffset.UtcNow,
            ErrorMessage = "Failed externally"
        };

        var jobStore = Substitute.For<IExecutionJobStore>();
        jobStore.TrySetAsync(Arg.Any<ExecutionJobRecord>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>())
            .Returns(true, false);
        jobStore.GetAsync("job-cas-post", Arg.Any<CancellationToken>())
            .Returns(terminalJob);

        var backend = Substitute.For<IBatchComputeBackend>();
        backend.StartAsync(Arg.Any<ExecutionJobRecord>(), Arg.Any<CancellationToken>())
            .Returns(new BatchComputeSubmissionResult
            {
                Status = ExecutionJobStatus.Running,
                Message = "Started"
            });

        var progressStore = new InMemoryProgressStore();
        var initialProgress = GeoprocessingProgress.CreateForSubmittedJob("job-cas-post", "plan-cas-post");
        await progressStore.SetProgressAsync("job-cas-post", initialProgress);

        var queuedJob = CreateJobRecord(
            operationId: "job-cas-post",
            status: ExecutionJobStatus.Queued,
            backend: "aws-batch",
            targetKind: BatchComputeTargetKind.AwsBatch);

        await ExecutionJobSubmissionHelper.StartOnRemoteBackendAsync(
            queuedJob, backend, jobStore, progressStore, TimeSpan.FromDays(7), null, CancellationToken.None);

        var progress = await progressStore.GetProgressAsync<GeoprocessingProgress>("job-cas-post");
        progress.Should().NotBeNull();
        progress!.WorkflowStatus.Should().Be(GeoprocessingWorkflowStatus.Failed,
            "Post-start CAS conflict with terminal current record must bridge progress");
        progress.CompletedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task StartOnRemoteBackendAsync_CasConflict_NonTerminalCurrent_DoesNotBridgeProgress()
    {
        var runningJob = CreateJobRecord(
            operationId: "job-cas-nt",
            status: ExecutionJobStatus.Running,
            backend: "aws-batch",
            targetKind: BatchComputeTargetKind.AwsBatch);

        var jobStore = Substitute.For<IExecutionJobStore>();
        jobStore.TrySetAsync(Arg.Any<ExecutionJobRecord>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>())
            .Returns(false);
        jobStore.GetAsync("job-cas-nt", Arg.Any<CancellationToken>())
            .Returns(runningJob);

        var backend = Substitute.For<IBatchComputeBackend>();
        var progressStore = new InMemoryProgressStore();
        var initialProgress = GeoprocessingProgress.CreateForSubmittedJob("job-cas-nt", "plan-cas-nt");
        await progressStore.SetProgressAsync("job-cas-nt", initialProgress);

        var queuedJob = CreateJobRecord(
            operationId: "job-cas-nt",
            status: ExecutionJobStatus.Queued,
            backend: "aws-batch",
            targetKind: BatchComputeTargetKind.AwsBatch);

        await ExecutionJobSubmissionHelper.StartOnRemoteBackendAsync(
            queuedJob, backend, jobStore, progressStore, TimeSpan.FromDays(7), null, CancellationToken.None);

        var progress = await progressStore.GetProgressAsync<GeoprocessingProgress>("job-cas-nt");
        progress.Should().NotBeNull();
        progress!.WorkflowStatus.Should().Be(GeoprocessingWorkflowStatus.AwaitingExecution,
            "CAS conflict with non-terminal record must not modify progress");
    }

    [Fact]
    public async Task StartOnRemoteBackendAsync_FailedSubmission_PersistsErrorMessageAndProgress()
    {
        var jobStore = new InMemoryExecutionJobStore(
            CreateJobRecord(
                operationId: "job-submit-fail",
                status: ExecutionJobStatus.Queued,
                backend: "aws-batch",
                targetKind: BatchComputeTargetKind.AwsBatch));
        var progressStore = new InMemoryProgressStore();
        await progressStore.SetProgressAsync(
            "job-submit-fail",
            GeoprocessingProgress.CreateForSubmittedJob("job-submit-fail", "plan-submit-fail"));

        var backend = Substitute.For<IBatchComputeBackend>();
        backend.StartAsync(Arg.Any<ExecutionJobRecord>(), Arg.Any<CancellationToken>())
            .Returns(new BatchComputeSubmissionResult
            {
                Status = ExecutionJobStatus.Failed,
                ProviderOperationId = "provider-submit-fail",
                Message = "Backend rejected submission"
            });

        var queuedJob = await jobStore.GetAsync("job-submit-fail", CancellationToken.None);

        var updated = await ExecutionJobSubmissionHelper.StartOnRemoteBackendAsync(
            queuedJob!, backend, jobStore, progressStore, TimeSpan.FromDays(7), null, CancellationToken.None);

        updated.Status.Should().Be(ExecutionJobStatus.Failed);
        updated.ProviderOperationId.Should().Be("provider-submit-fail");
        updated.ErrorMessage.Should().Be("Backend rejected submission",
            "terminal submission failures must retain the backend error for job and progress projections");

        var stored = await jobStore.GetAsync("job-submit-fail", CancellationToken.None);
        stored.Should().NotBeNull();
        stored!.ErrorMessage.Should().Be("Backend rejected submission");

        var progress = await progressStore.GetProgressAsync<GeoprocessingProgress>("job-submit-fail");
        progress.Should().NotBeNull();
        progress!.WorkflowStatus.Should().Be(GeoprocessingWorkflowStatus.Failed);
        progress.ErrorMessage.Should().Be("Backend rejected submission");
    }

    [Fact]
    public void BuildProgress_NonterminalJobOverStaleTerminalProgress_ClearsTerminalMetadata()
    {
        var now = DateTimeOffset.UtcNow;
        var staleCompletedAt = now.AddMinutes(-2);
        var existing = GeoprocessingProgress.CreateForSubmittedJob("job-stale-terminal", "plan-stale") with
        {
            WorkflowStatus = GeoprocessingWorkflowStatus.Failed,
            CurrentStageStatus = GeoprocessingStageStatus.Failed,
            ErrorMessage = "Prior attempt failed",
            CompletedAt = staleCompletedAt,
            StepsCompleted = 10,
            TotalSteps = 10
        };

        var runningJob = CreateJobRecord(
            operationId: "job-stale-terminal",
            status: ExecutionJobStatus.Running,
            backend: "aws-batch",
            targetKind: BatchComputeTargetKind.AwsBatch,
            currentPhase: "Running") with
        {
            CompletedAt = null,
            ErrorMessage = null
        };

        var bridged = ExecutionJobReconciler.BuildProgress(runningJob, existing);

        bridged.Should().NotBeNull();
        bridged!.WorkflowStatus.Should().Be(GeoprocessingWorkflowStatus.Running);
        bridged.CurrentStageStatus.Should().Be(GeoprocessingStageStatus.Pending);
        bridged.ErrorMessage.Should().BeNull(
            "nonterminal job observation must clear stale terminal error text");
        bridged.CompletedAt.Should().BeNull(
            "nonterminal job observation must clear stale terminal completion timestamp");
        bridged.StepsCompleted.Should().Be(0,
            "nonterminal job observation over a stale Completed projection must reset step counters so PercentComplete does not report 100%");
        bridged.TotalSteps.Should().Be(10,
            "TotalSteps (plan shape) should remain so the caller sees the same denominator");
    }

    [Fact]
    public async Task ReconcileExecutionJob_StartJobAsync_ProvisioningCasConflict_DoesNotCallBackendStart()
    {
        var operationId = "job-provisioning-cas";
        var queuedJob = CreateJobRecord(
            operationId: operationId,
            status: ExecutionJobStatus.Queued,
            backend: "aws-batch",
            targetKind: BatchComputeTargetKind.AwsBatch);

        var concurrentCancelledJob = queuedJob with
        {
            CancellationRequestedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        var jobStore = Substitute.For<IExecutionJobStore>();
        jobStore.TryAcquireLeaseAsync(operationId, Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(true);
        jobStore.RenewLeaseAsync(operationId, Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(true);
        jobStore.GetAsync(operationId, Arg.Any<CancellationToken>())
            .Returns(queuedJob, concurrentCancelledJob);
        jobStore.TrySetAsync(Arg.Any<ExecutionJobRecord>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>())
            .Returns(false);

        var backend = Substitute.For<IBatchComputeBackend>();
        backend.BackendName.Returns("aws-batch");
        backend.TargetKind.Returns(BatchComputeTargetKind.AwsBatch);

        var progressStore = new InMemoryProgressStore();
        var sut = new ExecutionJobReconciler(
            jobStore,
            [backend],
            progressStore,
            NullLogger<ExecutionJobReconciler>.Instance);

        await sut.ReconcileExecutionJobAsync(operationId);

        await backend.DidNotReceive().StartAsync(Arg.Any<ExecutionJobRecord>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReconcileExecutionJob_EmitsReconcileCycleAndTransitionMetrics()
    {
        // Transition/cycle metrics are wired so dashboards and alerts can observe the
        // execution-job lifecycle. Reconcile-cycle counts every accepted lease; transition
        // counts each persisted status change (e.g., Queued -> Failed for missing backend).
        var cycles = new List<long>();
        var transitions = new List<(long Value, string? Status, string? PreviousStatus)>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name != HonuaTelemetry.ServiceName)
                {
                    return;
                }

                if (instrument.Name == "honua.execution.reconcile.cycle"
                    || instrument.Name == "honua.execution.job.transitions_total")
                {
                    l.EnableMeasurementEvents(instrument);
                }
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, measurement, tags, _) =>
        {
            if (instrument.Name == "honua.execution.reconcile.cycle")
            {
                lock (cycles)
                {
                    cycles.Add(measurement);
                }
            }
            else if (instrument.Name == "honua.execution.job.transitions_total")
            {
                string? status = null;
                string? previous = null;
                foreach (var tag in tags)
                {
                    if (tag.Key == "honua.controlplane.execution.status")
                    {
                        status = tag.Value as string;
                    }
                    else if (tag.Key == "honua.controlplane.execution.previous_status")
                    {
                        previous = tag.Value as string;
                    }
                }

                lock (transitions)
                {
                    transitions.Add((measurement, status, previous));
                }
            }
        });
        listener.Start();

        var job = CreateJobRecord(
            operationId: "job-metrics",
            status: ExecutionJobStatus.Queued,
            backend: "missing-backend",
            targetKind: BatchComputeTargetKind.AwsBatch);
        var jobStore = new InMemoryExecutionJobStore(job);
        var progressStore = new InMemoryProgressStore();
        var sut = new ExecutionJobReconciler(
            jobStore,
            Array.Empty<IBatchComputeBackend>(),
            progressStore,
            NullLogger<ExecutionJobReconciler>.Instance);

        await sut.ReconcileExecutionJobAsync("job-metrics");

        cycles.Sum().Should().BeGreaterOrEqualTo(1, "every accepted lease bumps the reconcile-cycle counter");
        transitions.Should().Contain(t =>
            t.Value == 1 && t.PreviousStatus == nameof(ExecutionJobStatus.Queued) && t.Status == nameof(ExecutionJobStatus.Failed),
            "missing-backend path persists Queued -> Failed and must emit a transition sample");
    }

    private static ExecutionJobRecord CreateJobRecord(
        string operationId,
        ExecutionJobStatus status,
        string backend,
        BatchComputeTargetKind targetKind,
        double? percentComplete = null,
        string? currentPhase = null) => new()
        {
            OperationId = operationId,
            Status = status,
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
            UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            PercentComplete = percentComplete,
            CurrentPhase = currentPhase,
            Spec = new ExecutionJobSpec
            {
                Kind = ExecutionJobKind.Geoprocessing,
                TargetKind = targetKind,
                Backend = backend,
                WorkloadId = "plan-remote",
                WorkloadName = "Geoprocessing",
                Parameters = new Dictionary<string, string>
                {
                    [ExecutionJobParameterKeys.GeoprocessingPlanId] = "plan-remote"
                }
            }
        };

    private sealed class InMemoryExecutionJobStore(params ExecutionJobRecord[] jobs) : IExecutionJobStore
    {
        private readonly Dictionary<string, ExecutionJobRecord> _jobs = jobs.ToDictionary(job => job.OperationId, StringComparer.Ordinal);

        public Task<bool> TryAcquireLeaseAsync(string operationId, string ownerId, TimeSpan leaseDuration, CancellationToken cancellationToken = default)
            => Task.FromResult(true);

        public Task<bool> RenewLeaseAsync(string operationId, string ownerId, TimeSpan leaseDuration, CancellationToken cancellationToken = default)
            => Task.FromResult(true);

        public Task ReleaseLeaseAsync(string operationId, string ownerId, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<bool> TryCreateAsync(ExecutionJobRecord operation, TimeSpan? ttl = null, CancellationToken cancellationToken = default)
        {
            if (_jobs.ContainsKey(operation.OperationId))
            {
                return Task.FromResult(false);
            }

            _jobs[operation.OperationId] = operation;
            return Task.FromResult(true);
        }

        public Task<ExecutionJobRecord?> GetAsync(string operationId, CancellationToken cancellationToken = default)
            => Task.FromResult(_jobs.TryGetValue(operationId, out var job) ? job : null);

        public Task SetAsync(ExecutionJobRecord operation, TimeSpan? ttl = null, CancellationToken cancellationToken = default)
        {
            _jobs[operation.OperationId] = operation;
            return Task.CompletedTask;
        }

        public Task<bool> TrySetAsync(ExecutionJobRecord job, TimeSpan? ttl = null, CancellationToken cancellationToken = default)
        {
            _jobs[job.OperationId] = job;
            return Task.FromResult(true);
        }

        public Task<IReadOnlyList<ExecutionJobRecord>> ListActiveAsync(ExecutionJobKind? kind = null, CancellationToken cancellationToken = default)
        {
            var jobs = _jobs.Values
                .Where(job => !kind.HasValue || job.Spec.Kind == kind.Value)
                .ToArray();
            return Task.FromResult<IReadOnlyList<ExecutionJobRecord>>(jobs);
        }
    }

    private sealed class CasRejectingJobStore(params ExecutionJobRecord[] jobs) : IExecutionJobStore
    {
        private readonly Dictionary<string, ExecutionJobRecord> _jobs = jobs.ToDictionary(job => job.OperationId, StringComparer.Ordinal);

        public Task<bool> TryAcquireLeaseAsync(string operationId, string ownerId, TimeSpan leaseDuration, CancellationToken cancellationToken = default)
            => Task.FromResult(true);
        public Task<bool> RenewLeaseAsync(string operationId, string ownerId, TimeSpan leaseDuration, CancellationToken cancellationToken = default)
            => Task.FromResult(true);
        public Task ReleaseLeaseAsync(string operationId, string ownerId, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
        public Task<bool> TryCreateAsync(ExecutionJobRecord operation, TimeSpan? ttl = null, CancellationToken cancellationToken = default)
            => Task.FromResult(false);
        public Task<ExecutionJobRecord?> GetAsync(string operationId, CancellationToken cancellationToken = default)
            => Task.FromResult(_jobs.TryGetValue(operationId, out var job) ? job : null);
        public Task SetAsync(ExecutionJobRecord operation, TimeSpan? ttl = null, CancellationToken cancellationToken = default)
        {
            _jobs[operation.OperationId] = operation;
            return Task.CompletedTask;
        }
        public Task<bool> TrySetAsync(ExecutionJobRecord job, TimeSpan? ttl = null, CancellationToken cancellationToken = default)
            => Task.FromResult(false);
        public Task<IReadOnlyList<ExecutionJobRecord>> ListActiveAsync(ExecutionJobKind? kind = null, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<ExecutionJobRecord>>(_jobs.Values.ToArray());
    }

    private sealed class InMemoryProgressStore : IUniversalProgressStore
    {
        private readonly Dictionary<string, IOperationProgress> _progress = new(StringComparer.Ordinal);

        public Task SetProgressAsync(string operationId, IOperationProgress progress, TimeSpan? ttl = null, CancellationToken cancellationToken = default)
        {
            _progress[operationId] = progress;
            return Task.CompletedTask;
        }

        public Task<TProgress?> GetProgressAsync<TProgress>(string operationId, CancellationToken cancellationToken = default)
            where TProgress : class, IOperationProgress
            => Task.FromResult(_progress.TryGetValue(operationId, out var progress) ? progress as TProgress : null);

        public Task<IOperationProgress?> GetProgressAsync(string operationId, CancellationToken cancellationToken = default)
            => Task.FromResult(_progress.TryGetValue(operationId, out var progress) ? progress : null);

        public Task DeleteProgressAsync(string operationId, CancellationToken cancellationToken = default)
        {
            _progress.Remove(operationId);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<string>> GetActiveOperationIdsAsync(OperationType? operationType = null, CancellationToken cancellationToken = default)
        {
            var ids = _progress
                .Where(pair => !operationType.HasValue || pair.Value.Type == operationType.Value)
                .Select(pair => pair.Key)
                .ToArray();
            return Task.FromResult<IReadOnlyList<string>>(ids);
        }

        public Task<IReadOnlyList<TProgress>> GetActiveOperationsAsync<TProgress>(OperationType operationType, CancellationToken cancellationToken = default)
            where TProgress : class, IOperationProgress
        {
            var operations = _progress.Values
                .Where(progress => progress.Type == operationType)
                .OfType<TProgress>()
                .ToArray();
            return Task.FromResult<IReadOnlyList<TProgress>>(operations);
        }
    }
}
