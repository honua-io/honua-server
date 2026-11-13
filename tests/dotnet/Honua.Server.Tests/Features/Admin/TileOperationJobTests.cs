// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Concurrent;
using FluentAssertions;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.ControlPlane;
using Honua.Infrastructure.Progress;
using Honua.Server.Features.Admin.TileOperations;
using Honua.TestKit.Attributes;
using Honua.TestKit.Helpers;
using Honua.TestKit.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Honua.Server.Tests.Features.Admin;

// Every tile operation is a TileCache execution job: submission on the local and a remote backend,
// cancellation, retry, and the terminal projection onto the tile progress.
public sealed partial class TileOperationJobTests
{
    private const string Admin = "admin-a";

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
        IJobQueue? jobQueue = null,
        IMetadataV2GraphProvider? graphProvider = null)
        => new(
            jobStore,
            progressStore,
            backends,
            new StaticOptionsMonitor<TileCacheBatchOptions>(options),
            ScopeFactoryFor(graphProvider ?? Substitute.For<IMetadataV2GraphProvider>()),
            NullLogger<TileCacheJobService>.Instance,
            jobQueue ?? Substitute.For<IJobQueue>());

    /// <summary>
    /// Builds a real <see cref="IServiceScopeFactory"/> over a <em>scoped</em>
    /// <see cref="IMetadataV2GraphProvider"/>, matching the production (Postgres) lifetime. The
    /// service under test is a singleton, so it must reach the provider through a scope rather
    /// than capture it — see the container-validation matrix in
    /// <c>ImportExportTileOperationsRegistrationTests</c>.
    /// </summary>
    private static IServiceScopeFactory ScopeFactoryFor(IMetadataV2GraphProvider graphProvider)
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => graphProvider);
        return services
            .BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true })
            .GetRequiredService<IServiceScopeFactory>();
    }

    [UnitTest]
    public async Task Start_AlwaysCreatesAnExecutionJob()
    {
        var jobStore = new InMemoryExecutionJobStore();
        var progressStore = new AcceptingInMemoryProgressStore();
        var jobQueue = Substitute.For<IJobQueue>();
        var backend = new LocalBatchComputeBackend(progressStore, [Substitute.For<IJobCancellationNotifier>()]);
        var service = CreateService(jobStore, progressStore, [backend], new TileCacheBatchOptions(), jobQueue);

        var jobId = await service.SubmitAsync(new TileOperationSubmission(SeedRequest(), Admin));

        var record = await jobStore.GetAsync(jobId);
        record.Should().NotBeNull("an unconfigured host still records the operation as an execution job");
        record!.Status.Should().Be(ExecutionJobStatus.Queued);
        record.Spec.Kind.Should().Be(ExecutionJobKind.TileCache);
        record.Spec.Backend.Should().Be(LocalBatchComputeBackend.BackendId);
        record.Spec.Parameters[TileCacheJobParameterKeys.GenerationId].Should().Be(
            jobId,
            "the first submission names the resumable generation after its own job");
        record.Audit.RequestedBy.Should().Be(Admin);
        record.Audit.SubmitterSecurityContext.Should().BeNull("tile layers are read as the system, not as the caller");
        await jobQueue.Received(1).EnqueueAsync(jobId, Arg.Any<OperationPriority>(), Arg.Any<CancellationToken>());
        (await service.GetAsync(jobId))!.Status.Should().Be(OperationStatus.Queued);
    }

    [UnitTest]
    public async Task Cancel_WhileAHeartbeatMovesTheRecord_StillRecordsTheCancellationRequest()
    {
        var jobStore = new InMemoryExecutionJobStore();
        var progressStore = new AcceptingInMemoryProgressStore();
        var backend = new LocalBatchComputeBackend(progressStore, [Substitute.For<IJobCancellationNotifier>()]);
        var service = CreateService(jobStore, progressStore, [backend], new TileCacheBatchOptions(), Substitute.For<IJobQueue>());
        var jobId = await service.SubmitAsync(new TileOperationSubmission(SeedRequest(), Admin));
        await MarkRunningAsync(jobStore, progressStore, jobId);
        jobStore.MoveRecordOnNextWrite = true;

        var cancelled = await service.CancelAsync(jobId);

        cancelled.Should().BeTrue();
        (await jobStore.GetAsync(jobId))!.CancellationRequestedAt.Should().NotBeNull(
            "a reported cancellation must be durable so the worker running the job stops");
    }

    [UnitTest]
    public async Task Retry_WhileTheJobStillRuns_ReturnsNull()
    {
        var jobStore = new InMemoryExecutionJobStore();
        var progressStore = new AcceptingInMemoryProgressStore();
        var backend = new LocalBatchComputeBackend(progressStore, [Substitute.For<IJobCancellationNotifier>()]);
        var service = CreateService(jobStore, progressStore, [backend], new TileCacheBatchOptions(), Substitute.For<IJobQueue>());
        var jobId = await service.SubmitAsync(new TileOperationSubmission(SeedRequest(), Admin));
        await MarkRunningAsync(jobStore, progressStore, jobId);
        var progress = (await service.GetAsync(jobId))!;
        await progressStore.SetProgressAsync(jobId, progress.WithCancellation(DateTimeOffset.UtcNow, "Cancellation requested"));

        (await service.RetryAsync(jobId, "admin-b")).Should().BeNull("the original job is still running on its generation");
    }

    [UnitTest]
    public async Task Start_WithoutAJobQueue_IsRefused()
    {
        var jobStore = new InMemoryExecutionJobStore();
        var progressStore = new AcceptingInMemoryProgressStore();
        var backend = new LocalBatchComputeBackend(progressStore, [Substitute.For<IJobCancellationNotifier>()]);
        var service = new TileCacheJobService(
            jobStore,
            progressStore,
            [backend],
            new StaticOptionsMonitor<TileCacheBatchOptions>(new TileCacheBatchOptions()),
            ScopeFactoryFor(Substitute.For<IMetadataV2GraphProvider>()),
            NullLogger<TileCacheJobService>.Instance);

        var submit = () => service.SubmitAsync(new TileOperationSubmission(SeedRequest(), Admin));

        await submit.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage(TileCacheJobService.JobStoreUnavailableMessage);
        (await jobStore.QueryAsync(new ExecutionJobQuery())).Items.Should().BeEmpty();
    }

    [UnitTest]
    public async Task Start_WithoutAJobStore_IsRefused()
    {
        var progressStore = new AcceptingInMemoryProgressStore();
        var service = new TileCacheJobService(
            null,
            progressStore,
            [],
            new StaticOptionsMonitor<TileCacheBatchOptions>(new TileCacheBatchOptions()),
            ScopeFactoryFor(Substitute.For<IMetadataV2GraphProvider>()),
            NullLogger<TileCacheJobService>.Instance);

        var submit = () => service.SubmitAsync(new TileOperationSubmission(SeedRequest(), Admin));

        await submit.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage(TileCacheJobService.JobStoreUnavailableMessage);
        (await progressStore.GetActiveOperationIdsAsync()).Should().BeEmpty();
    }

    [UnitTest]
    public async Task Retry_FailedJob_SubmitsANewJobThatResumesTheSameGeneration()
    {
        var jobStore = new InMemoryExecutionJobStore();
        var progressStore = new AcceptingInMemoryProgressStore();
        var backend = new LocalBatchComputeBackend(progressStore, [Substitute.For<IJobCancellationNotifier>()]);
        var service = CreateService(jobStore, progressStore, [backend], new TileCacheBatchOptions(), Substitute.For<IJobQueue>());
        var failedJobId = await service.SubmitAsync(new TileOperationSubmission(SeedRequest(), Admin, "tenant_a", "public"));
        var failed = (await service.GetAsync(failedJobId))! with
        { Status = OperationStatus.Failed };
        await progressStore.SetProgressAsync(failedJobId, failed);
        await jobStore.SetAsync((await jobStore.GetAsync(failedJobId))! with
        { Status = ExecutionJobStatus.Failed });

        var retryJobId = await service.RetryAsync(failedJobId, "admin-b");

        retryJobId.Should().NotBeNullOrWhiteSpace().And.NotBe(failedJobId);
        var retry = (await jobStore.GetAsync(retryJobId!))!;
        retry.Spec.Kind.Should().Be(ExecutionJobKind.TileCache);
        retry.Spec.Parameters[TileCacheJobParameterKeys.GenerationId].Should().Be(
            failedJobId,
            "a retry resumes the original generation checkpoint rather than restarting the grid");
        retry.Spec.Parameters[TileCacheJobParameterKeys.SchemaName].Should().Be("tenant_a");
        retry.Spec.Parameters[TileCacheJobParameterKeys.TenantScope].Should().Be("public");
        retry.Audit.RequestedBy.Should().Be("admin-b", "the retry is attributed to the caller who asked for it");
    }

    [UnitTest]
    public async Task Retry_ActiveJob_ReturnsNull()
    {
        var jobStore = new InMemoryExecutionJobStore();
        var progressStore = new AcceptingInMemoryProgressStore();
        var backend = new LocalBatchComputeBackend(progressStore, [Substitute.For<IJobCancellationNotifier>()]);
        var service = CreateService(jobStore, progressStore, [backend], new TileCacheBatchOptions(), Substitute.For<IJobQueue>());
        var jobId = await service.SubmitAsync(new TileOperationSubmission(SeedRequest(), Admin));

        (await service.RetryAsync(jobId, "admin-b")).Should().BeNull();
    }

    [UnitTheory]
    [InlineData(ExecutionJobStatus.Failed, OperationStatus.Failed)]
    [InlineData(ExecutionJobStatus.Cancelled, OperationStatus.Cancelled)]
    public async Task TerminalCallback_JobEndedByTheWorker_ClosesTheTileProgress(
        ExecutionJobStatus jobStatus,
        OperationStatus expected)
    {
        var progressStore = new ComparingInMemoryProgressStore();
        var running = TileOperationProgress.CreateInitial("tile-1", "seed", null, 1, "WebMercatorQuad")
            with
            { Status = OperationStatus.Processing };
        await progressStore.SetProgressAsync("tile-1", running);
        var callback = new TileCacheJobTerminalCallback();
        var now = DateTimeOffset.UtcNow;

        var terminal = new ExecutionJobRecord
        {
            OperationId = "tile-1",
            Status = jobStatus,
            CreatedAt = now,
            UpdatedAt = now,
            CompletedAt = now,
            ErrorMessage = "license expired",
            Spec = TileCacheExecutionSpecBuilder.Build(SeedRequest(), null, new TileCacheBatchOptions()),
        };
        var jobStore = new InMemoryExecutionJobStore();
        await jobStore.TryCreateAsync(terminal);
        await callback.OnTerminalAsync(terminal, CancellationToken.None);

        var closed = (TileOperationProgress)(await new ExecutionJobProgressReader(progressStore, jobStore)
            .ReadAsync("tile-1", CancellationToken.None))!;
        closed.Status.Should().Be(expected);
        closed.CompletedAt.Should().Be(now);
        if (expected == OperationStatus.Failed)
        {
            closed.ErrorMessage.Should().Be("license expired");
        }
    }

    [UnitTest]
    public async Task SubmitAsync_ServiceAndStorageLayerAliases_SharePartition()
    {
        var graph = new TestMetadataV2GraphBuilder()
            .AddResource("resource", "resource")
            .AddStorageBinding("binding", "resource", "features", storageLayerId: 42)
            .AddService("service", "service")
            .AddPublication("publication", "service", "resource", layerIndex: 7, storageBindingId: "binding")
            .Build();
        var graphProvider = new TestMetadataV2GraphProvider(graph);
        var jobStore = new InMemoryExecutionJobStore();
        var progressStore = new AcceptingInMemoryProgressStore();
        var backend = new LocalBatchComputeBackend(progressStore, [Substitute.For<IJobCancellationNotifier>()]);
        var options = new TileCacheBatchOptions { Backend = LocalBatchComputeBackend.BackendId };
        var service = CreateService(jobStore, progressStore, [backend], options, graphProvider: graphProvider);

        var byService = await service.SubmitAsync(new TileOperationSubmission(new TileOperationStartRequest
        {
            Operation = "seed",
            ServiceId = "service",
            TileMatrixSetId = "Web/MercatorQuad",
            Style = "a/b"
        }, Admin));
        var byLayer = await service.SubmitAsync(new TileOperationSubmission(new TileOperationStartRequest
        {
            Operation = "delete",
            LayerId = 42,
            TileMatrixSetId = "Web_MercatorQuad",
            Style = "a_b"
        }, Admin));

        var servicePartition = (await jobStore.GetAsync(byService))!.Concurrency!.PartitionKey;
        var layerPartition = (await jobStore.GetAsync(byLayer))!.Concurrency!.PartitionKey;
        servicePartition.Should().Be("tilecache:layers:web_mercatorquad:a_b");
        layerPartition.Should().Be(servicePartition);
    }

    [UnitTest]
    public async Task SubmitAsync_LocalBackend_CreatesQueuedJobAndEnqueues()
    {
        var jobStore = new InMemoryExecutionJobStore();
        var progressStore = new AcceptingInMemoryProgressStore();
        var jobQueue = Substitute.For<IJobQueue>();
        var localBackend = new LocalBatchComputeBackend(progressStore, [Substitute.For<IJobCancellationNotifier>()]);
        var options = new TileCacheBatchOptions { Backend = LocalBatchComputeBackend.BackendId };

        var service = CreateService(jobStore, progressStore, [localBackend], options, jobQueue);

        var jobId = await service.SubmitAsync(new TileOperationSubmission(SeedRequest(), Admin, TenantScope: "public"));

        jobId.Should().StartWith("tile-");
        var record = await jobStore.GetAsync(jobId);
        record.Should().NotBeNull();
        record!.Status.Should().Be(ExecutionJobStatus.Queued);
        record.Spec.Kind.Should().Be(ExecutionJobKind.TileCache);
        record.Spec.Backend.Should().Be(LocalBatchComputeBackend.BackendId);
        record.Spec.Parameters[TileCacheJobParameterKeys.TenantScope].Should().Be("public");

        await jobQueue.Received(1).EnqueueAsync(jobId, Arg.Any<OperationPriority>(), Arg.Any<CancellationToken>());

        var progress = await progressStore.GetProgressAsync<TileOperationProgress>(jobId);
        progress.Should().NotBeNull();
        progress!.Operation.Should().Be("seed");
    }

    [UnitTest]
    public async Task SubmitAsync_NormalizesOperationBeforeDurableDispatch()
    {
        var jobStore = new InMemoryExecutionJobStore();
        var progressStore = new AcceptingInMemoryProgressStore();
        var backend = new LocalBatchComputeBackend(progressStore, [Substitute.For<IJobCancellationNotifier>()]);
        var options = new TileCacheBatchOptions { Backend = LocalBatchComputeBackend.BackendId };
        var service = CreateService(jobStore, progressStore, [backend], options);

        var jobId = await service.SubmitAsync(new TileOperationSubmission(SeedRequest() with
        {
            Operation = " DELETE ",
            TileMatrixSetId = " WebMercatorQuad ",
            Style = " default ",
        }, Admin));

        var record = (await jobStore.GetAsync(jobId))!;
        record.Spec.Parameters[TileCacheJobParameterKeys.Operation].Should().Be("delete");
        var progress = (await progressStore.GetProgressAsync<TileOperationProgress>(jobId))!;
        progress.Operation.Should().Be("delete");
        progress.TileMatrixSetId.Should().Be("WebMercatorQuad");
    }

    [UnitTest]
    public async Task SubmitAsync_RemoteBackend_SubmitsToProviderWithoutEnqueue()
    {
        var jobStore = new InMemoryExecutionJobStore();
        var progressStore = new AcceptingInMemoryProgressStore();
        var jobQueue = Substitute.For<IJobQueue>();
        var remote = new StubBatchComputeBackend("honua-aws-batch", BatchComputeTargetKind.AwsBatch);
        var options = new TileCacheBatchOptions
        {
            Backend = "honua-aws-batch",
            TargetKind = BatchComputeTargetKind.AwsBatch,
            Parameters = { ["batch.job_definition_arn"] = "arn:jd", ["batch.job_queue_arn"] = "arn:jq" }
        };

        var service = CreateService(jobStore, progressStore, [remote], options, jobQueue);

        var jobId = await service.SubmitAsync(new TileOperationSubmission(SeedRequest(), Admin));

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
        var progressStore = new AcceptingInMemoryProgressStore();
        var options = new TileCacheBatchOptions
        {
            Backend = "missing-backend",
            TargetKind = BatchComputeTargetKind.AwsBatch
        };

        // Only the local backend is registered; the requested backend is absent.
        var localBackend = new LocalBatchComputeBackend(progressStore, [Substitute.For<IJobCancellationNotifier>()]);
        var service = CreateService(jobStore, progressStore, [localBackend], options);

        var act = async () => await service.SubmitAsync(new TileOperationSubmission(SeedRequest(), Admin));

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [UnitTest]
    public async Task LocalBackend_ObserveAsync_UsesDurableStatusAndWorkerProgress()
    {
        var jobStore = new InMemoryExecutionJobStore();
        var progressStore = new AcceptingInMemoryProgressStore();
        var jobQueue = Substitute.For<IJobQueue>();
        var localBackend = new LocalBatchComputeBackend(progressStore, [Substitute.For<IJobCancellationNotifier>()]);
        var options = new TileCacheBatchOptions { Backend = LocalBatchComputeBackend.BackendId };

        var service = CreateService(jobStore, progressStore, [localBackend], options, jobQueue);
        var jobId = await service.SubmitAsync(new TileOperationSubmission(SeedRequest(), Admin));
        var job = (await jobStore.GetAsync(jobId))!;

        job = job with { Status = ExecutionJobStatus.Running };
        var seeded = (await progressStore.GetProgressAsync<TileOperationProgress>(jobId))!;
        await progressStore.SetProgressAsync(jobId, seeded with
        {
            Status = OperationStatus.Processing,
            CurrentPhase = "Seeding tiles (10/20)"
        });

        var running = await localBackend.ObserveAsync(job);
        running.Status.Should().Be(ExecutionJobStatus.Running);
        running.Message.Should().Be("Seeding tiles (10/20)");

        await progressStore.SetProgressAsync(jobId, seeded with
        {
            Status = OperationStatus.Completed,
            CurrentPhase = "seed completed"
        });

        var beforeWorkerCommit = await localBackend.ObserveAsync(job);
        beforeWorkerCommit.Status.Should().Be(ExecutionJobStatus.Running);

        var done = await localBackend.ObserveAsync(job with { Status = ExecutionJobStatus.Succeeded });
        done.Status.Should().Be(ExecutionJobStatus.Succeeded);
    }

    [UnitTest]
    public async Task CancelAsync_RunningJob_RequestsBackendCancellation()
    {
        var jobStore = new InMemoryExecutionJobStore();
        var progressStore = new AcceptingInMemoryProgressStore();
        var jobQueue = Substitute.For<IJobQueue>();
        var localBackend = new LocalBatchComputeBackend(progressStore, [Substitute.For<IJobCancellationNotifier>()]);
        var options = new TileCacheBatchOptions { Backend = LocalBatchComputeBackend.BackendId };

        var service = CreateService(jobStore, progressStore, [localBackend], options, jobQueue);
        var jobId = await service.SubmitAsync(new TileOperationSubmission(SeedRequest(), Admin));

        // Promote to Running so cancellation is meaningful (not a pre-pickup queued cancel).
        var queued = await jobStore.GetAsync(jobId);
        await jobStore.SetAsync(queued! with
        { Status = ExecutionJobStatus.Running, ClaimedBy = "worker-a" });

        var cancelled = await service.CancelAsync(jobId);

        cancelled.Should().BeTrue();
        var record = await jobStore.GetAsync(jobId);
        record!.CancellationRequestedAt.Should().NotBeNull();
    }

    [UnitTest]
    public async Task CancelAsync_TerminalJob_ReturnsFalse()
    {
        var jobStore = new InMemoryExecutionJobStore();
        var progressStore = new AcceptingInMemoryProgressStore();
        var localBackend = new LocalBatchComputeBackend(progressStore, [Substitute.For<IJobCancellationNotifier>()]);
        var options = new TileCacheBatchOptions { Backend = LocalBatchComputeBackend.BackendId };

        var service = CreateService(jobStore, progressStore, [localBackend], options, Substitute.For<IJobQueue>());
        var jobId = await service.SubmitAsync(new TileOperationSubmission(SeedRequest(), Admin));

        var queued = await jobStore.GetAsync(jobId);
        await jobStore.SetAsync(queued! with
        { Status = ExecutionJobStatus.Succeeded });

        var cancelled = await service.CancelAsync(jobId);

        cancelled.Should().BeFalse();
    }

    [UnitTest]
    public async Task CancelAsync_UnknownJob_ReturnsFalse()
    {
        var jobStore = new InMemoryExecutionJobStore();
        var progressStore = new AcceptingInMemoryProgressStore();
        var localBackend = new LocalBatchComputeBackend(progressStore, [Substitute.For<IJobCancellationNotifier>()]);
        var options = new TileCacheBatchOptions { Backend = LocalBatchComputeBackend.BackendId };

        var service = CreateService(jobStore, progressStore, [localBackend], options, Substitute.For<IJobQueue>());

        (await service.CancelAsync("tile-does-not-exist")).Should().BeFalse();
    }

    private static async Task MarkRunningAsync(
        InMemoryExecutionJobStore jobStore,
        AcceptingInMemoryProgressStore progressStore,
        string jobId)
    {
        var record = (await jobStore.GetAsync(jobId))!;
        await jobStore.SetAsync(record with
        { Status = ExecutionJobStatus.Running, ClaimedBy = "worker-b", AttemptCount = 1 });
        var progress = (await progressStore.GetProgressAsync<TileOperationProgress>(jobId))!;
        await progressStore.SetProgressAsync(jobId, progress with
        { Status = OperationStatus.Processing });
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

        public Func<ExecutionJobRecord, Task>? OnCreated { get; set; }

        public async Task<bool> TryCreateAsync(ExecutionJobRecord job, TimeSpan? ttl = null, CancellationToken cancellationToken = default)
        {
            var created = _records.TryAdd(job.OperationId, job);
            if (created && OnCreated is not null)
            {
                await OnCreated(job);
            }

            return created;
        }

        public Task<ExecutionJobRecord?> GetAsync(string operationId, CancellationToken cancellationToken = default)
            => Task.FromResult(_records.TryGetValue(operationId, out var record) ? record : null);

        public Task SetAsync(ExecutionJobRecord job, TimeSpan? ttl = null, CancellationToken cancellationToken = default)
        {
            _records[job.OperationId] = job;
            return Task.CompletedTask;
        }

        // Simulates a worker heartbeat landing between the caller's read and its write.
        public bool MoveRecordOnNextWrite { get; set; }

        public Task<bool> TrySetAsync(ExecutionJobRecord job, TimeSpan? ttl = null, CancellationToken cancellationToken = default)
        {
            if (MoveRecordOnNextWrite)
            {
                MoveRecordOnNextWrite = false;
                _records[job.OperationId] = _records[job.OperationId] with
                { LastHeartbeatAt = DateTimeOffset.UtcNow };
                return Task.FromResult(false);
            }

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

    private sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue { get; } = value;
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
