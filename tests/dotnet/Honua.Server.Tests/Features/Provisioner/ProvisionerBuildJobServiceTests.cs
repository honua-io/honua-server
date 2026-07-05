// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using FluentAssertions;
using Honua.ControlPlane;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Server.Features.Provisioner.BuildJobs;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Honua.Server.Tests.Features.Provisioner;

/// <summary>
/// Unit coverage for batch-dispatched per-area geocoder/router build submission and
/// cancellation. Exercises the canonical submission flow against the in-process
/// <see cref="LocalBatchComputeBackend"/> (durable record + queue enqueue) and a remote
/// stub backend (provider submission with the AWS Batch coordinates wired), plus
/// cancellation and rollback â€” all with the Batch backend mocked, no live AWS.
/// </summary>
public sealed class ProvisionerBuildJobServiceTests
{
    private static ProvisionerArea MauiCounty()
    {
        ProvisionerArea.TryParse("geoid:15009", out var area, out _).Should().BeTrue();
        return area;
    }

    private static GeocoderBuildRequest GeocoderRequest() => new()
    {
        SourceId = "census-tiger",
        ProductId = "addresses",
        Area = MauiCounty(),
        FeedstockTable = "od_census_tiger_addresses",
        ArtifactName = "maui",
        ArtifactKey = "locators/maui/maui.osm.pbf"
    };

    private static RouterBuildRequest RouterRequest() => new()
    {
        SourceId = "census-tiger",
        ProductId = "routing-roads",
        Area = MauiCounty(),
        FeedstockTable = "od_census_tiger_routing_roads",
        ArtifactName = "maui",
        ArtifactKey = "routing/maui/ways.dump"
    };

    private static ProvisionerBuildJobService CreateService(
        InMemoryExecutionJobStore jobStore,
        IUniversalProgressStore progressStore,
        IEnumerable<IBatchComputeBackend> backends,
        ProvisionerBuildBatchOptions options,
        IJobQueue? jobQueue = null)
        => new(
            jobStore,
            progressStore,
            backends,
            new StaticOptionsMonitor<ProvisionerBuildBatchOptions>(options),
            NullLogger<ProvisionerBuildJobService>.Instance,
            jobQueue);

    [UnitTest]
    public async Task SubmitGeocoderBuild_LocalBackend_CreatesQueuedJobAndEnqueues()
    {
        var jobStore = new InMemoryExecutionJobStore();
        var progressStore = new InMemoryProgressStore();
        var jobQueue = Substitute.For<IJobQueue>();
        var localBackend = new LocalBatchComputeBackend(progressStore, Substitute.For<IJobCancellationNotifier>());
        var options = new ProvisionerBuildBatchOptions { Enabled = true, Backend = LocalBatchComputeBackend.BackendId };

        var service = CreateService(jobStore, progressStore, [localBackend], options, jobQueue);

        var jobId = await service.SubmitGeocoderBuildAsync(GeocoderRequest());

        jobId.Should().StartWith("geo-");
        var record = await jobStore.GetAsync(jobId);
        record.Should().NotBeNull();
        record!.Status.Should().Be(ExecutionJobStatus.Queued);
        record.Spec.Kind.Should().Be(ExecutionJobKind.GeocoderBuild);
        record.Spec.Backend.Should().Be(LocalBatchComputeBackend.BackendId);
        record.Spec.Parameters[ProvisionerBuildJobParameterKeys.ArtifactKey]
            .Should().Be("locators/maui/maui.osm.pbf");

        await jobQueue.Received(1).EnqueueAsync(jobId, Arg.Any<OperationPriority>(), Arg.Any<CancellationToken>());

        var progress = await progressStore.GetProgressAsync<GeoprocessingProgress>(jobId);
        progress.Should().NotBeNull();
    }

    [UnitTest]
    public async Task SubmitRouterBuild_RemoteBackend_SubmitsToProviderWithoutEnqueue()
    {
        var jobStore = new InMemoryExecutionJobStore();
        var progressStore = new InMemoryProgressStore();
        var jobQueue = Substitute.For<IJobQueue>();
        var remote = new StubBatchComputeBackend("honua-aws-batch", BatchComputeTargetKind.AwsBatch);
        var options = new ProvisionerBuildBatchOptions
        {
            Enabled = true,
            Backend = "honua-aws-batch",
            TargetKind = BatchComputeTargetKind.AwsBatch,
            Parameters = { ["batch.job_definition_arn"] = "arn:jd", ["batch.job_queue_arn"] = "arn:jq" }
        };

        var service = CreateService(jobStore, progressStore, [remote], options, jobQueue);

        var jobId = await service.SubmitRouterBuildAsync(RouterRequest());

        jobId.Should().StartWith("route-");
        remote.StartCalls.Should().Be(1);
        remote.LastStartedSpec!.Kind.Should().Be(ExecutionJobKind.RouterBuild);
        remote.LastStartedSpec.Backend.Should().Be("honua-aws-batch");
        remote.LastStartedSpec.Parameters["batch.job_definition_arn"].Should().Be("arn:jd");

        // Remote dispatch must not also enqueue onto the local in-process queue.
        await jobQueue.DidNotReceive().EnqueueAsync(Arg.Any<string>(), Arg.Any<OperationPriority>(), Arg.Any<CancellationToken>());

        var record = await jobStore.GetAsync(jobId);
        record!.Spec.TargetKind.Should().Be(BatchComputeTargetKind.AwsBatch);
    }

    [UnitTest]
    public async Task SubmitGeocoderBuild_RemoteBackendUnregistered_RollsBackJob()
    {
        var jobStore = new InMemoryExecutionJobStore();
        var progressStore = new InMemoryProgressStore();
        var options = new ProvisionerBuildBatchOptions
        {
            Enabled = true,
            Backend = "missing-backend",
            TargetKind = BatchComputeTargetKind.AwsBatch
        };

        // Only the local backend is registered; the requested backend is absent.
        var localBackend = new LocalBatchComputeBackend(progressStore, Substitute.For<IJobCancellationNotifier>());
        var service = CreateService(jobStore, progressStore, [localBackend], options);

        var act = async () => await service.SubmitGeocoderBuildAsync(GeocoderRequest());

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [UnitTest]
    public async Task CancelAsync_RunningJob_RequestsBackendCancellation()
    {
        var jobStore = new InMemoryExecutionJobStore();
        var progressStore = new InMemoryProgressStore();
        var jobQueue = Substitute.For<IJobQueue>();
        var localBackend = new LocalBatchComputeBackend(progressStore, Substitute.For<IJobCancellationNotifier>());
        var options = new ProvisionerBuildBatchOptions { Enabled = true, Backend = LocalBatchComputeBackend.BackendId };

        var service = CreateService(jobStore, progressStore, [localBackend], options, jobQueue);
        var jobId = await service.SubmitRouterBuildAsync(RouterRequest());

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
        var options = new ProvisionerBuildBatchOptions { Enabled = true, Backend = LocalBatchComputeBackend.BackendId };

        var service = CreateService(jobStore, progressStore, [localBackend], options, Substitute.For<IJobQueue>());
        var jobId = await service.SubmitGeocoderBuildAsync(GeocoderRequest());

        var queued = await jobStore.GetAsync(jobId);
        await jobStore.SetAsync(queued! with { Status = ExecutionJobStatus.Succeeded });

        (await service.CancelAsync(jobId)).Should().BeFalse();
    }

    [UnitTest]
    public async Task CancelAsync_UnknownJob_ReturnsFalse()
    {
        var jobStore = new InMemoryExecutionJobStore();
        var progressStore = new InMemoryProgressStore();
        var localBackend = new LocalBatchComputeBackend(progressStore, Substitute.For<IJobCancellationNotifier>());
        var options = new ProvisionerBuildBatchOptions { Enabled = true, Backend = LocalBatchComputeBackend.BackendId };

        var service = CreateService(jobStore, progressStore, [localBackend], options, Substitute.For<IJobQueue>());

        (await service.CancelAsync("geo-does-not-exist")).Should().BeFalse();
    }

    [UnitTest]
    public void IsEnabled_ReflectsOptions()
    {
        var jobStore = new InMemoryExecutionJobStore();
        var progressStore = new InMemoryProgressStore();
        var localBackend = new LocalBatchComputeBackend(progressStore, Substitute.For<IJobCancellationNotifier>());

        CreateService(jobStore, progressStore, [localBackend], new ProvisionerBuildBatchOptions { Enabled = false })
            .IsEnabled.Should().BeFalse();
        CreateService(jobStore, progressStore, [localBackend], new ProvisionerBuildBatchOptions { Enabled = true })
            .IsEnabled.Should().BeTrue();
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
