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
    public async Task ReconcileExecutionJob_LocalQueuedJob_StartsViaBaselineBackend()
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
        stored!.Status.Should().Be(ExecutionJobStatus.Running);
        stored.ProviderOperationId.Should().Be("job-local");
        stored.CurrentPhase.Should().Be("Local in-process execution via baseline workers");
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
