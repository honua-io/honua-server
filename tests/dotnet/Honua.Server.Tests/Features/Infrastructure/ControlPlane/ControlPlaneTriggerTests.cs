// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.ControlPlane;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Honua.Server.Tests.Features.Infrastructure.ControlPlane;

/// <summary>
/// Phase 1 tests for the hybrid-trigger seam: the event handler reconciles one operation once and
/// is safe under duplicate/out-of-order invocation (lease/CAS no-op), and the backstop sweep
/// reconciles stale operations while no-opping fresh ones. Both drive the same dispatcher path that
/// the poll loop uses.
/// </summary>
public sealed class ControlPlaneTriggerTests
{
    // ---- Event handler -----------------------------------------------------

    [Fact]
    public async Task HandleExecutionJobEvent_ResolvesProviderIdAndReconcilesOnce()
    {
        var job = CreateJob("op-1", ExecutionJobStatus.Running, providerOperationId: "batch-job-abc");
        var store = new VersionedJobStore(job);
        var dispatcher = Substitute.For<IOperationReconcileDispatcher>();
        var sut = new ControlPlaneEventHandler(store, Substitute.For<IWorkflowOperationStore>(), dispatcher, NullLogger<ControlPlaneEventHandler>.Instance);

        await sut.HandleExecutionJobEventAsync("batch-job-abc");

        await dispatcher.Received(1).ReconcileOnceAsync(
            Arg.Is<OperationRef>(r => r.Kind == OperationKind.ExecutionJob && r.OperationId == "op-1"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleExecutionJobEvent_UnknownProviderId_IsIgnored()
    {
        var job = CreateJob("op-1", ExecutionJobStatus.Running, providerOperationId: "batch-job-abc");
        var store = new VersionedJobStore(job);
        var dispatcher = Substitute.For<IOperationReconcileDispatcher>();
        var sut = new ControlPlaneEventHandler(store, Substitute.For<IWorkflowOperationStore>(), dispatcher, NullLogger<ControlPlaneEventHandler>.Instance);

        await sut.HandleExecutionJobEventAsync("not-a-known-job");

        await dispatcher.DidNotReceiveWithAnyArgs().ReconcileOnceAsync(default, default);
    }

    [Fact]
    public async Task HandleExecutionJobEvent_DuplicateAndOutOfOrderInvocation_AdvancesJobOnce()
    {
        // Real reconciler behind the real dispatcher: prove the lease/CAS + terminal guards make a
        // duplicate / out-of-order event a no-op. The backend reports a terminal Succeeded; the first
        // event advances the durable record, the duplicates re-read a terminal record and bail.
        var job = CreateJob("op-dup", ExecutionJobStatus.Running, providerOperationId: "batch-job-dup");
        var store = new VersionedJobStore(job);
        var progress = new RecordingProgressStore();
        var backend = new CountingBatchBackend(ExecutionJobStatus.Succeeded);
        var dispatcher = BuildDispatcher(store, progress, backend);
        var sut = new ControlPlaneEventHandler(store, Substitute.For<IWorkflowOperationStore>(), dispatcher, NullLogger<ControlPlaneEventHandler>.Instance);

        // Fire the same event three times (duplicate delivery) plus a stale "out-of-order" replay.
        await sut.HandleExecutionJobEventAsync("batch-job-dup");
        await sut.HandleExecutionJobEventAsync("batch-job-dup");
        await sut.HandleExecutionJobOperationAsync("op-dup");

        var stored = await store.GetAsync("op-dup");
        stored!.Status.Should().Be(ExecutionJobStatus.Succeeded);
        // Exactly one durable advancing write: the terminal-guard short-circuits the later calls
        // before any further CAS, so the version advanced exactly once.
        store.WriteCount.Should().Be(1, "the terminal guard makes duplicate/out-of-order events a no-op");
    }

    // ---- Backstop sweep ----------------------------------------------------

    [Fact]
    public async Task Backstop_ReconcilesStaleNonTerminalOperation()
    {
        var stale = CreateJob("op-stale", ExecutionJobStatus.Running, providerOperationId: "p-stale", updatedAt: DateTimeOffset.UtcNow.AddMinutes(-10));
        var store = new VersionedJobStore(stale);
        var dispatcher = Substitute.For<IOperationReconcileDispatcher>();
        var sut = BuildBackstop(store, dispatcher, staleThreshold: TimeSpan.FromSeconds(90));

        await sut.SweepOnceAsync(TimeSpan.FromSeconds(90), CancellationToken.None);

        await dispatcher.Received(1).ReconcileOnceAsync(
            Arg.Is<OperationRef>(r => r.Kind == OperationKind.ExecutionJob && r.OperationId == "op-stale"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Backstop_NoOpsFreshOperation()
    {
        var fresh = CreateJob("op-fresh", ExecutionJobStatus.Running, providerOperationId: "p-fresh", updatedAt: DateTimeOffset.UtcNow);
        var store = new VersionedJobStore(fresh);
        var dispatcher = Substitute.For<IOperationReconcileDispatcher>();
        var sut = BuildBackstop(store, dispatcher, staleThreshold: TimeSpan.FromSeconds(90));

        await sut.SweepOnceAsync(TimeSpan.FromSeconds(90), CancellationToken.None);

        await dispatcher.DidNotReceiveWithAnyArgs().ReconcileOnceAsync(default, default);
    }

    [Fact]
    public async Task Backstop_SkipsTerminalOperation()
    {
        var terminal = CreateJob("op-done", ExecutionJobStatus.Succeeded, providerOperationId: "p-done", updatedAt: DateTimeOffset.UtcNow.AddHours(-1));
        var store = new VersionedJobStore(terminal);
        var dispatcher = Substitute.For<IOperationReconcileDispatcher>();
        var sut = BuildBackstop(store, dispatcher, staleThreshold: TimeSpan.FromSeconds(90));

        await sut.SweepOnceAsync(TimeSpan.FromSeconds(90), CancellationToken.None);

        await dispatcher.DidNotReceiveWithAnyArgs().ReconcileOnceAsync(default, default);
    }

    [Fact]
    public async Task Backstop_StaleOperationAlreadyAdvancedByEvent_IsCasNoOp()
    {
        // The backstop and a prior event both reconcile the same stale job. With the real reconciler,
        // the backstop re-reads a now-terminal record and makes no further durable write.
        var stale = CreateJob("op-heal", ExecutionJobStatus.Running, providerOperationId: "batch-heal", updatedAt: DateTimeOffset.UtcNow.AddMinutes(-10));
        var store = new VersionedJobStore(stale);
        var progress = new RecordingProgressStore();
        var backend = new CountingBatchBackend(ExecutionJobStatus.Succeeded);
        var dispatcher = BuildDispatcher(store, progress, backend);

        // Simulate the missed-event self-heal: an event already drove it terminal...
        var handler = new ControlPlaneEventHandler(store, Substitute.For<IWorkflowOperationStore>(), dispatcher, NullLogger<ControlPlaneEventHandler>.Instance);
        await handler.HandleExecutionJobEventAsync("batch-heal");
        var afterEvent = store.WriteCount;

        // ...then the backstop sweeps the (now terminal) job; the terminal guard makes it a no-op.
        var sut = BuildBackstop(store, dispatcher, staleThreshold: TimeSpan.FromSeconds(90));
        await sut.SweepOnceAsync(TimeSpan.FromSeconds(90), CancellationToken.None);

        afterEvent.Should().Be(1);
        store.WriteCount.Should().Be(1, "the backstop is a no-op once an event has advanced the job to terminal");
        (await store.GetAsync("op-heal"))!.Status.Should().Be(ExecutionJobStatus.Succeeded);
    }

    // ---- Helpers -----------------------------------------------------------

    private static OperationReconcileDispatcher BuildDispatcher(
        IExecutionJobStore store,
        IUniversalProgressStore progress,
        IBatchComputeBackend backend)
    {
        var reconciler = new ExecutionJobReconciler(
            store,
            [backend],
            progress,
            NullLogger<ExecutionJobReconciler>.Instance);

        return new OperationReconcileDispatcher(
            Substitute.For<IWorkflowOperationReconciler>(),
            reconciler,
            Substitute.For<IMetadataReleaseOperationReconciler>(),
            Substitute.For<ICoordinatedReleaseReconciler>());
    }

    private static ExecutionJobBackstopSweepService BuildBackstop(
        IExecutionJobStore store,
        IOperationReconcileDispatcher dispatcher,
        TimeSpan staleThreshold)
        => new(
            store,
            dispatcher,
            Options.Create(new ControlPlaneTriggerOptions { StaleThreshold = staleThreshold }),
            NullLogger<ExecutionJobBackstopSweepService>.Instance);

    private static ExecutionJobRecord CreateJob(
        string operationId,
        ExecutionJobStatus status,
        string? providerOperationId,
        DateTimeOffset? updatedAt = null) => new()
        {
            OperationId = operationId,
            Status = status,
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
            UpdatedAt = updatedAt ?? DateTimeOffset.UtcNow.AddMinutes(-1),
            ProviderOperationId = providerOperationId,
            Spec = new ExecutionJobSpec
            {
                Kind = ExecutionJobKind.Geoprocessing,
                TargetKind = BatchComputeTargetKind.AwsBatch,
                Backend = "aws-batch",
                WorkloadId = "plan-remote",
                WorkloadName = "Geoprocessing",
                Parameters = new Dictionary<string, string>
                {
                    [ExecutionJobParameterKeys.GeoprocessingPlanId] = "plan-remote"
                }
            }
        };

    /// <summary>
    /// In-memory execution-job store that enforces optimistic-concurrency CAS via the record
    /// <see cref="ExecutionJobRecord.Version"/> token, so duplicate/out-of-order reconciles exercise
    /// the same conflict path the Redis store would. Counts successful advancing writes.
    /// </summary>
    private sealed class VersionedJobStore(params ExecutionJobRecord[] jobs) : IExecutionJobStore
    {
        private readonly Dictionary<string, ExecutionJobRecord> _jobs =
            jobs.ToDictionary(job => job.OperationId, StringComparer.Ordinal);

        public int WriteCount { get; private set; }

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
            _jobs[operation.OperationId] = operation with { Version = operation.Version + 1 };
            return Task.CompletedTask;
        }

        public Task<bool> TrySetAsync(ExecutionJobRecord job, TimeSpan? ttl = null, CancellationToken cancellationToken = default)
        {
            if (!_jobs.TryGetValue(job.OperationId, out var current) || current.Version != job.Version)
            {
                return Task.FromResult(false);
            }

            _jobs[job.OperationId] = job with { Version = job.Version + 1 };
            WriteCount++;
            return Task.FromResult(true);
        }

        public Task<ExecutionJobPage> QueryAsync(ExecutionJobQuery query, CancellationToken cancellationToken = default)
            => Task.FromResult(new ExecutionJobPage { Items = _jobs.Values.ToArray() });

        public Task<IReadOnlyList<ExecutionJobRecord>> ListActiveAsync(ExecutionJobKind? kind = null, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<ExecutionJobRecord>>(_jobs.Values.ToArray());
    }

    /// <summary>
    /// Batch backend that reports a fixed observation and counts how many times it was observed.
    /// </summary>
    private sealed class CountingBatchBackend(ExecutionJobStatus observedStatus) : IBatchComputeBackend
    {
        public int ObserveCount { get; private set; }

        public string BackendName => "aws-batch";

        public BatchComputeTargetKind TargetKind => BatchComputeTargetKind.AwsBatch;

        public Task<BatchComputeBackendCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new BatchComputeBackendCapabilities { SupportsProgressPolling = true });

        public Task<BatchComputeSubmissionResult> StartAsync(ExecutionJobRecord job, CancellationToken cancellationToken = default)
            => Task.FromResult(new BatchComputeSubmissionResult { Status = ExecutionJobStatus.Provisioning });

        public Task<BatchComputeObservation> ObserveAsync(ExecutionJobRecord job, CancellationToken cancellationToken = default)
        {
            ObserveCount++;
            return Task.FromResult(new BatchComputeObservation
            {
                Status = observedStatus,
                ProviderOperationId = job.ProviderOperationId,
                PercentComplete = 100
            });
        }

        public Task<BatchComputeObservation> CancelAsync(ExecutionJobRecord job, CancellationToken cancellationToken = default)
            => Task.FromResult(new BatchComputeObservation { Status = ExecutionJobStatus.Cancelled });
    }

    private sealed class RecordingProgressStore : IUniversalProgressStore
    {
        private readonly Dictionary<string, IOperationProgress> _progress = new(StringComparer.Ordinal);

        public Task SetProgressAsync(string operationId, IOperationProgress progress, TimeSpan? ttl = null, CancellationToken cancellationToken = default)
        {
            _progress[operationId] = progress;
            return Task.CompletedTask;
        }

        public Task<ProgressCompareAndSetResult> TrySetProgressAsync(string operationId, IOperationProgress progress, OperationStatus expectedStatus, TimeSpan? ttl = null, CancellationToken cancellationToken = default)
            => Task.FromResult(ProgressCompareAndSetResult.Updated);

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
            => Task.FromResult<IReadOnlyList<string>>(_progress.Keys.ToArray());

        public Task<IReadOnlyList<TProgress>> GetActiveOperationsAsync<TProgress>(OperationType operationType, CancellationToken cancellationToken = default)
            where TProgress : class, IOperationProgress
            => Task.FromResult<IReadOnlyList<TProgress>>(_progress.Values.OfType<TProgress>().ToArray());
    }
}
