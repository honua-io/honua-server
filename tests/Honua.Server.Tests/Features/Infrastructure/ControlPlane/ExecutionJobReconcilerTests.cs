// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Server.Features.Infrastructure.ControlPlane;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Server.Tests.Features.Infrastructure.ControlPlane;

public sealed class ExecutionJobReconcilerTests
{
    [Fact]
    public async Task ReconcileAsync_PersistsStatusTransitionFromBackendObservation()
    {
        var job = CreateJob(ExecutionJobStatus.Running);
        var store = new StubExecutionJobStore(job);
        var backend = new StubBackend
        {
            Observation = new BatchComputeObservation
            {
                Status = ExecutionJobStatus.Succeeded,
                ProviderOperationId = "honua-test",
                PercentComplete = 100,
                Message = "Task completed with exit 0"
            }
        };
        var reconciler = new ExecutionJobReconciler(store, [backend], NullLogger<ExecutionJobReconciler>.Instance);

        await reconciler.ReconcileAsync(job.OperationId);

        store.LastSaved.Should().NotBeNull();
        store.LastSaved!.Status.Should().Be(ExecutionJobStatus.Succeeded);
        store.LastSaved.CompletedAt.Should().NotBeNull();
        store.LastSaved.ProviderOperationId.Should().Be("honua-test");
        store.LastSaved.CurrentPhase.Should().Contain("exit 0");
    }

    [Fact]
    public async Task ReconcileAsync_SkipsWhenLeaseCannotBeAcquired()
    {
        var job = CreateJob(ExecutionJobStatus.Running);
        var store = new StubExecutionJobStore(job) { AcquireLease = false };
        var backend = new StubBackend();
        var reconciler = new ExecutionJobReconciler(store, [backend], NullLogger<ExecutionJobReconciler>.Instance);

        await reconciler.ReconcileAsync(job.OperationId);

        backend.ObserveCount.Should().Be(0);
        store.LastSaved.Should().BeNull();
    }

    [Fact]
    public async Task ReconcileAsync_SkipsTerminalJobs()
    {
        var job = CreateJob(ExecutionJobStatus.Succeeded);
        var store = new StubExecutionJobStore(job);
        var backend = new StubBackend();
        var reconciler = new ExecutionJobReconciler(store, [backend], NullLogger<ExecutionJobReconciler>.Instance);

        await reconciler.ReconcileAsync(job.OperationId);

        backend.ObserveCount.Should().Be(0);
        store.LastSaved.Should().BeNull();
    }

    [Fact]
    public async Task ReconcileAsync_NoOpWhenNoBackendMatchesJob()
    {
        var job = CreateJob(ExecutionJobStatus.Running) with
        {
            Spec = CreateJob(ExecutionJobStatus.Running).Spec with { Backend = "nonexistent-backend" }
        };
        var store = new StubExecutionJobStore(job);
        var backend = new StubBackend();
        var reconciler = new ExecutionJobReconciler(store, [backend], NullLogger<ExecutionJobReconciler>.Instance);

        await reconciler.ReconcileAsync(job.OperationId);

        backend.ObserveCount.Should().Be(0);
        store.LastSaved.Should().BeNull();
    }

    [Fact]
    public async Task ReconcileAsync_DoesNotPersistWhenObservationMatchesCurrentState()
    {
        var job = CreateJob(ExecutionJobStatus.Running) with
        {
            PercentComplete = null,
            CurrentPhase = null
        };
        var store = new StubExecutionJobStore(job);
        var backend = new StubBackend
        {
            Observation = new BatchComputeObservation
            {
                Status = ExecutionJobStatus.Running,
                ProviderOperationId = job.ProviderOperationId,
                PercentComplete = null,
                Message = null
            }
        };
        var reconciler = new ExecutionJobReconciler(store, [backend], NullLogger<ExecutionJobReconciler>.Instance);

        await reconciler.ReconcileAsync(job.OperationId);

        store.LastSaved.Should().BeNull();
    }

    [Fact]
    public async Task ReconcileAsync_ReleasesLeaseEvenWhenBackendThrows()
    {
        var job = CreateJob(ExecutionJobStatus.Running);
        var store = new StubExecutionJobStore(job);
        var backend = new StubBackend { ThrowOnObserve = new InvalidOperationException("boom") };
        var reconciler = new ExecutionJobReconciler(store, [backend], NullLogger<ExecutionJobReconciler>.Instance);

        Func<Task> act = () => reconciler.ReconcileAsync(job.OperationId);

        await act.Should().ThrowAsync<InvalidOperationException>();
        store.LeaseReleased.Should().BeTrue();
    }

    private static ExecutionJobRecord CreateJob(ExecutionJobStatus status)
        => new()
        {
            OperationId = $"job-{Guid.NewGuid():N}",
            Status = status,
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-2),
            UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            ProviderOperationId = "honua-test",
            Spec = new ExecutionJobSpec
            {
                TargetKind = BatchComputeTargetKind.AzureBatch,
                Backend = AzureBatchComputeBackend.BackendIdentifier,
                Kind = ExecutionJobKind.Geoprocessing,
                WorkloadName = "sample",
                WorkloadId = "wl-1",
                Parameters = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["azure.batch.account_url"] = "https://acct.eastus.batch.azure.com",
                    ["azure.batch.pool_id"] = "default-pool"
                }
            }
        };

    private sealed class StubExecutionJobStore(ExecutionJobRecord job) : IExecutionJobStore
    {
        private ExecutionJobRecord _job = job;

        public bool AcquireLease { get; set; } = true;

        public bool LeaseReleased { get; private set; }

        public ExecutionJobRecord? LastSaved { get; private set; }

        public Task<bool> TryAcquireLeaseAsync(
            string operationId,
            string ownerId,
            TimeSpan leaseDuration,
            CancellationToken cancellationToken = default)
            => Task.FromResult(AcquireLease);

        public Task<bool> RenewLeaseAsync(
            string operationId,
            string ownerId,
            TimeSpan leaseDuration,
            CancellationToken cancellationToken = default)
            => Task.FromResult(true);

        public Task ReleaseLeaseAsync(
            string operationId,
            string ownerId,
            CancellationToken cancellationToken = default)
        {
            LeaseReleased = true;
            return Task.CompletedTask;
        }

        public Task<bool> TryCreateAsync(
            ExecutionJobRecord job,
            TimeSpan? ttl = null,
            CancellationToken cancellationToken = default)
        {
            _job = job;
            return Task.FromResult(true);
        }

        public Task<ExecutionJobRecord?> GetAsync(string operationId, CancellationToken cancellationToken = default)
            => Task.FromResult<ExecutionJobRecord?>(_job);

        public Task SetAsync(ExecutionJobRecord job, TimeSpan? ttl = null, CancellationToken cancellationToken = default)
        {
            _job = job;
            LastSaved = job;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<ExecutionJobRecord>> ListActiveAsync(
            ExecutionJobKind? kind = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<ExecutionJobRecord>>([_job]);
    }

    private sealed class StubBackend : IBatchComputeBackend
    {
        public string BackendName => AzureBatchComputeBackend.BackendIdentifier;

        public BatchComputeTargetKind TargetKind => BatchComputeTargetKind.AzureBatch;

        public BatchComputeObservation? Observation { get; set; }

        public Exception? ThrowOnObserve { get; set; }

        public int ObserveCount { get; private set; }

        public Task<BatchComputeBackendCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new BatchComputeBackendCapabilities());

        public Task<BatchComputeSubmissionResult> StartAsync(ExecutionJobRecord job, CancellationToken cancellationToken = default)
            => Task.FromResult(new BatchComputeSubmissionResult
            {
                Status = ExecutionJobStatus.Queued,
                ProviderOperationId = job.ProviderOperationId
            });

        public Task<BatchComputeObservation> ObserveAsync(ExecutionJobRecord job, CancellationToken cancellationToken = default)
        {
            ObserveCount++;
            if (ThrowOnObserve != null)
            {
                throw ThrowOnObserve;
            }

            return Task.FromResult(Observation ?? new BatchComputeObservation
            {
                Status = job.Status,
                ProviderOperationId = job.ProviderOperationId,
                PercentComplete = job.PercentComplete,
                Message = job.CurrentPhase
            });
        }

        public Task<BatchComputeObservation> CancelAsync(ExecutionJobRecord job, CancellationToken cancellationToken = default)
            => Task.FromResult(new BatchComputeObservation
            {
                Status = ExecutionJobStatus.Cancelled,
                ProviderOperationId = job.ProviderOperationId,
                PercentComplete = 100
            });
    }
}
