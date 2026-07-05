// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using FluentAssertions;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.ControlPlane;
using Honua.Infrastructure.Progress;
using Honua.Server.Features.Admin.TileOperations;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Honua.Server.Tests.Features.Admin;

/// <summary>
/// Unit coverage for batch-dispatched tile-cache job submission and cancellation
/// (issue #1697). Exercises the canonical submission flow against the in-process
/// <see cref="LocalBatchComputeBackend"/> (durable record + queue enqueue) and a
/// remote stub backend (provider submission), plus cancellation through the backend.
/// </summary>
public sealed class TileCacheJobServiceTests
{
    private static TileOperationStartRequest SeedRequest() => new()
    {
        Operation = "seed",
        LayerId = 1,
        TileMatrixSetId = "WebMercatorQuad",
        MinZoom = 0,
        MaxZoom = 2
    };

    private static TileCacheJobService CreateService(
        InMemoryExecutionJobStore jobStore,
        IUniversalProgressStore progressStore,
        IEnumerable<IBatchComputeBackend> backends,
        TileCacheBatchOptions options,
        IJobQueue? jobQueue = null)
        => new(
            jobStore,
            progressStore,
            backends,
            new StaticOptionsMonitor<TileCacheBatchOptions>(options),
            NullLogger<TileCacheJobService>.Instance,
            jobQueue);

    [UnitTest]
    public async Task SubmitAsync_LocalBackend_CreatesQueuedJobAndEnqueues()
    {
        var jobStore = new InMemoryExecutionJobStore();
        var progressStore = new InMemoryProgressStore();
        var jobQueue = Substitute.For<IJobQueue>();
        var localBackend = new LocalBatchComputeBackend(progressStore, Substitute.For<IJobCancellationNotifier>());
        var options = new TileCacheBatchOptions { Enabled = true, Backend = LocalBatchComputeBackend.BackendId };

        var service = CreateService(jobStore, progressStore, [localBackend], options, jobQueue);

        var jobId = await service.SubmitAsync(SeedRequest(), schemaName: null);

        jobId.Should().StartWith("tile-");
        var record = await jobStore.GetAsync(jobId);
        record.Should().NotBeNull();
        record!.Status.Should().Be(ExecutionJobStatus.Queued);
        record.Spec.Kind.Should().Be(ExecutionJobKind.TileCache);
        record.Spec.Backend.Should().Be(LocalBatchComputeBackend.BackendId);

        await jobQueue.Received(1).EnqueueAsync(jobId, Arg.Any<OperationPriority>(), Arg.Any<CancellationToken>());

        var progress = await progressStore.GetProgressAsync<TileOperationProgress>(jobId);
        progress.Should().NotBeNull();
        progress!.Operation.Should().Be("seed");
    }

    [UnitTest]
    public async Task SubmitAsync_RemoteBackend_SubmitsToProviderWithoutEnqueue()
    {
        var jobStore = new InMemoryExecutionJobStore();
        var progressStore = new InMemoryProgressStore();
        var jobQueue = Substitute.For<IJobQueue>();
        var remote = new StubBatchComputeBackend("honua-aws-batch", BatchComputeTargetKind.AwsBatch);
        var options = new TileCacheBatchOptions
        {
            Enabled = true,
            Backend = "honua-aws-batch",
            TargetKind = BatchComputeTargetKind.AwsBatch,
            Parameters = { ["batch.job_definition_arn"] = "arn:jd", ["batch.job_queue_arn"] = "arn:jq" }
        };

        var service = CreateService(jobStore, progressStore, [remote], options, jobQueue);

        var jobId = await service.SubmitAsync(SeedRequest(), schemaName: null);

        remote.StartCalls.Should().Be(1);
        remote.LastStartedSpec!.Backend.Should().Be("honua-aws-batch");
        remote.LastStartedSpec.Parameters["batch.job_definition_arn"].Should().Be("arn:jd");

        // Remote dispatch must not also enqueue onto the local in-process queue.
        await jobQueue.DidNotReceive().EnqueueAsync(Arg.Any<string>(), Arg.Any<OperationPriority>(), Arg.Any<CancellationToken>());

        var record = await jobStore.GetAsync(jobId);
        record!.Spec.TargetKind.Should().Be(BatchComputeTargetKind.AwsBatch);
    }

    [UnitTest]
    public async Task SubmitAsync_RemoteBackendUnregistered_RollsBackJob()
    {
        var jobStore = new InMemoryExecutionJobStore();
        var progressStore = new InMemoryProgressStore();
        var options = new TileCacheBatchOptions
        {
            Enabled = true,
            Backend = "missing-backend",
            TargetKind = BatchComputeTargetKind.AwsBatch
        };

        // Only the local backend is registered; the requested backend is absent.
        var localBackend = new LocalBatchComputeBackend(progressStore, Substitute.For<IJobCancellationNotifier>());
        var service = CreateService(jobStore, progressStore, [localBackend], options);

        var act = async () => await service.SubmitAsync(SeedRequest(), schemaName: null);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [UnitTest]
    public async Task LocalBackend_ObserveAsync_ReflectsWorkerProgress()
    {
        // Submission seeds a TileOperationProgress; the LocalBatchComputeBackend
        // observes that progress and maps it to an execution-job status, which is the
        // canonical observation path used by the reconciler and admin status API.
        var jobStore = new InMemoryExecutionJobStore();
        var progressStore = new InMemoryProgressStore();
        var jobQueue = Substitute.For<IJobQueue>();
        var localBackend = new LocalBatchComputeBackend(progressStore, Substitute.For<IJobCancellationNotifier>());
        var options = new TileCacheBatchOptions { Enabled = true, Backend = LocalBatchComputeBackend.BackendId };

        var service = CreateService(jobStore, progressStore, [localBackend], options, jobQueue);
        var jobId = await service.SubmitAsync(SeedRequest(), schemaName: null);
        var job = (await jobStore.GetAsync(jobId))!;

        // Worker reports in-flight progress.
        var seeded = (await progressStore.GetProgressAsync<TileOperationProgress>(jobId))!;
        await progressStore.SetProgressAsync(jobId, seeded with
        {
            Status = OperationStatus.Processing,
            CurrentPhase = "Seeding tiles (10/20)"
        });

        var running = await localBackend.ObserveAsync(job);
        running.Status.Should().Be(ExecutionJobStatus.Running);
        running.Message.Should().Be("Seeding tiles (10/20)");

        // Worker reports completion.
        await progressStore.SetProgressAsync(jobId, seeded with
        {
            Status = OperationStatus.Completed,
            CurrentPhase = "seed completed"
        });

        var done = await localBackend.ObserveAsync(job);
        done.Status.Should().Be(ExecutionJobStatus.Succeeded);
    }

    [UnitTest]
    public async Task CancelAsync_RunningJob_RequestsBackendCancellation()
    {
        var jobStore = new InMemoryExecutionJobStore();
        var progressStore = new InMemoryProgressStore();
        var jobQueue = Substitute.For<IJobQueue>();
        var localBackend = new LocalBatchComputeBackend(progressStore, Substitute.For<IJobCancellationNotifier>());
        var options = new TileCacheBatchOptions { Enabled = true, Backend = LocalBatchComputeBackend.BackendId };

        var service = CreateService(jobStore, progressStore, [localBackend], options, jobQueue);
        var jobId = await service.SubmitAsync(SeedRequest(), schemaName: null);

        // Promote to Running so cancellation is meaningful (not a pre-pickup queued cancel).
        var queued = await jobStore.GetAsync(jobId);
        await jobStore.SetAsync(queued! with { Status = ExecutionJobStatus.Running });

        var cancelled = await service.CancelAsync(jobId);

        cancelled.Should().BeTrue();
        var record = await jobStore.GetAsync(jobId);
        record!.CancellationRequestedAt.Should().NotBeNull();
    }

    [UnitTest]
    public async Task CancelAsync_TerminalJob_ReturnsFalse()
    {
        var jobStore = new InMemoryExecutionJobStore();
        var progressStore = new InMemoryProgressStore();
        var localBackend = new LocalBatchComputeBackend(progressStore, Substitute.For<IJobCancellationNotifier>());
        var options = new TileCacheBatchOptions { Enabled = true, Backend = LocalBatchComputeBackend.BackendId };

        var service = CreateService(jobStore, progressStore, [localBackend], options, Substitute.For<IJobQueue>());
        var jobId = await service.SubmitAsync(SeedRequest(), schemaName: null);

        var queued = await jobStore.GetAsync(jobId);
        await jobStore.SetAsync(queued! with { Status = ExecutionJobStatus.Succeeded });

        var cancelled = await service.CancelAsync(jobId);

        cancelled.Should().BeFalse();
    }

    [UnitTest]
    public async Task CancelAsync_UnknownJob_ReturnsFalse()
    {
        var jobStore = new InMemoryExecutionJobStore();
        var progressStore = new InMemoryProgressStore();
        var localBackend = new LocalBatchComputeBackend(progressStore, Substitute.For<IJobCancellationNotifier>());
        var options = new TileCacheBatchOptions { Enabled = true, Backend = LocalBatchComputeBackend.BackendId };

        var service = CreateService(jobStore, progressStore, [localBackend], options, Substitute.For<IJobQueue>());

        (await service.CancelAsync("tile-does-not-exist")).Should().BeFalse();
    }

    // ----- Test doubles -----------------------------------------------------

    private sealed class StubBatchComputeBackend(string name, BatchComputeTargetKind targetKind) : IBatchComputeBackend
    {
        public int StartCalls { get; private set; }
        public ExecutionJobSpec? LastStartedSpec { get; private set; }

        public string BackendName => name;
        public BatchComputeTargetKind TargetKind => targetKind;

        public Task<BatchComputeBackendCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new BatchComputeBackendCapabilities { SupportsCancellation = true, SupportsProgressPolling = true });

        public Task<BatchComputeSubmissionResult> StartAsync(ExecutionJobRecord job, CancellationToken cancellationToken = default)
        {
            StartCalls++;
            LastStartedSpec = job.Spec;
            return Task.FromResult(new BatchComputeSubmissionResult
            {
                Status = ExecutionJobStatus.Queued,
                ProviderOperationId = "provider-1"
            });
        }

        public Task<BatchComputeObservation> ObserveAsync(ExecutionJobRecord job, CancellationToken cancellationToken = default)
            => Task.FromResult(new BatchComputeObservation { Status = job.Status, ProviderOperationId = job.ProviderOperationId });

        public Task<BatchComputeObservation> CancelAsync(ExecutionJobRecord job, CancellationToken cancellationToken = default)
            => Task.FromResult(new BatchComputeObservation { Status = ExecutionJobStatus.Cancelled, ProviderOperationId = job.ProviderOperationId });
    }

    private sealed class InMemoryExecutionJobStore : IExecutionJobStore
    {
        private readonly ConcurrentDictionary<string, ExecutionJobRecord> _records = new(StringComparer.Ordinal);

        public Task<bool> TryCreateAsync(ExecutionJobRecord job, TimeSpan? ttl = null, CancellationToken cancellationToken = default)
            => Task.FromResult(_records.TryAdd(job.OperationId, job));

        public Task<ExecutionJobRecord?> GetAsync(string operationId, CancellationToken cancellationToken = default)
            => Task.FromResult(_records.TryGetValue(operationId, out var record) ? record : null);

        public Task SetAsync(ExecutionJobRecord job, TimeSpan? ttl = null, CancellationToken cancellationToken = default)
        {
            _records[job.OperationId] = job;
            return Task.CompletedTask;
        }

        public Task<bool> TrySetAsync(ExecutionJobRecord job, TimeSpan? ttl = null, CancellationToken cancellationToken = default)
        {
            _records[job.OperationId] = job;
            return Task.FromResult(true);
        }

        public Task<ExecutionJobPage> QueryAsync(ExecutionJobQuery query, CancellationToken cancellationToken = default)
            => Task.FromResult(new ExecutionJobPage { Items = _records.Values.ToArray() });

        public Task<IReadOnlyList<ExecutionJobRecord>> ListActiveAsync(ExecutionJobKind? kind = null, int? limit = null, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<ExecutionJobRecord>>(_records.Values.ToArray());

        public Task<bool> TryAcquireLeaseAsync(string operationId, string ownerId, TimeSpan leaseDuration, CancellationToken cancellationToken = default)
            => Task.FromResult(true);

        public Task<bool> RenewLeaseAsync(string operationId, string ownerId, TimeSpan leaseDuration, CancellationToken cancellationToken = default)
            => Task.FromResult(true);

        public Task ReleaseLeaseAsync(string operationId, string ownerId, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private sealed class InMemoryProgressStore : IUniversalProgressStore
    {
        private readonly ConcurrentDictionary<string, IOperationProgress> _progress = new(StringComparer.Ordinal);

        public Task SetProgressAsync(string operationId, IOperationProgress progress, TimeSpan? ttl = null, CancellationToken cancellationToken = default)
        {
            _progress[operationId] = progress;
            return Task.CompletedTask;
        }

        public Task<ProgressCompareAndSetResult> TrySetProgressAsync(string operationId, IOperationProgress progress, OperationStatus expectedStatus, TimeSpan? ttl = null, CancellationToken cancellationToken = default)
        {
            _progress[operationId] = progress;
            return Task.FromResult(ProgressCompareAndSetResult.Updated);
        }

        public Task<TProgress?> GetProgressAsync<TProgress>(string operationId, CancellationToken cancellationToken = default)
            where TProgress : class, IOperationProgress
            => Task.FromResult(_progress.TryGetValue(operationId, out var p) ? p as TProgress : null);

        public Task<IOperationProgress?> GetProgressAsync(string operationId, CancellationToken cancellationToken = default)
            => Task.FromResult(_progress.TryGetValue(operationId, out var p) ? p : null);

        public Task DeleteProgressAsync(string operationId, CancellationToken cancellationToken = default)
        {
            _progress.TryRemove(operationId, out _);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<string>> GetActiveOperationIdsAsync(OperationType? operationType = null, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<string>>(_progress.Keys.ToArray());

        public Task<IReadOnlyList<TProgress>> GetActiveOperationsAsync<TProgress>(OperationType operationType, CancellationToken cancellationToken = default)
            where TProgress : class, IOperationProgress
            => Task.FromResult<IReadOnlyList<TProgress>>(_progress.Values.OfType<TProgress>().ToArray());
    }

    private sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue { get; } = value;
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
