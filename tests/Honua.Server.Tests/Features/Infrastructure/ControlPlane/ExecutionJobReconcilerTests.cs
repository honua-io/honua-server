// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Server.Features.Infrastructure.ControlPlane;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Honua.Server.Tests.Features.Infrastructure.ControlPlane;

public sealed class ExecutionJobReconcilerTests
{
    [Fact]
    public async Task ReconcileExecutionJob_BackendMissing_FailsJobRecord()
    {
        var job = CreateJobRecord(
            operationId: "job-missing",
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

        await sut.ReconcileExecutionJobAsync("job-missing");

        var stored = await jobStore.GetAsync("job-missing");
        stored.Should().NotBeNull();
        stored!.Status.Should().Be(ExecutionJobStatus.Failed);
        stored.CompletedAt.Should().NotBeNull();
        stored.ErrorMessage.Should().Contain("No batch compute backend registered");
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
