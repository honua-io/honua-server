// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Diagnostics;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Infrastructure.Progress;
using Honua.ControlPlane;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Honua.Server.Features.Admin.TileOperations;

// This singleton resolves the scoped database provider inside a per-submission scope so host startup validation does not fail.
internal sealed partial class TileCacheJobService : ITileCacheJobService
{
    internal const string JobStoreUnavailableMessage = "Tile operations require the system database job store.";
    internal const string SchedulerIdentityName = "honua/tile-cache-scheduler";

    private readonly IExecutionJobStore? _jobStore;
    private readonly IUniversalProgressStore _progressStore;
    private readonly IJobQueue? _jobQueue;
    private readonly IReadOnlyList<IBatchComputeBackend> _backends;
    private readonly IOptionsMonitor<TileCacheBatchOptions> _options;
    private readonly ILogger<TileCacheJobService> _logger;
    private readonly IServiceScopeFactory _scopeFactory;

    private static readonly TimeSpan ProgressRetention = TimeSpan.FromHours(24);

    private static readonly OperationType[] TileOperationTypes =
        [OperationType.TileCache, OperationType.PMTilesArchive, OperationType.PMTilesPublish];

    public TileCacheJobService(
        IExecutionJobStore? jobStore,
        IUniversalProgressStore progressStore,
        IEnumerable<IBatchComputeBackend> backends,
        IOptionsMonitor<TileCacheBatchOptions> options,
        IServiceScopeFactory scopeFactory,
        ILogger<TileCacheJobService> logger,
        IJobQueue? jobQueue = null)
    {
        _jobStore = jobStore;
        _progressStore = progressStore ?? throw new ArgumentNullException(nameof(progressStore));
        _backends = (backends ?? throw new ArgumentNullException(nameof(backends))).ToArray();
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _jobQueue = jobQueue;
    }

    public async Task<string> SubmitAsync(TileOperationSubmission submission, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(submission);
        var jobStore = _jobStore ?? throw new InvalidOperationException(JobStoreUnavailableMessage);
        _ = _jobQueue ?? throw new InvalidOperationException(JobStoreUnavailableMessage);
        var request = TileCacheExecutionSpecBuilder.NormalizeRequest(submission.Request);
        var jobRecord = await CreateRecordAsync(submission with { Request = request }, cancellationToken).ConfigureAwait(false);
        await SeedProgressAsync(jobRecord.OperationId, request, cancellationToken).ConfigureAwait(false);
        if (!await jobStore.TryCreateAsync(jobRecord, cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            await _progressStore.DeleteProgressAsync(jobRecord.OperationId, CancellationToken.None).ConfigureAwait(false);
            throw new InvalidOperationException($"Failed to create tile-cache execution job '{jobRecord.OperationId}'.");
        }

        await DispatchAsync(jobStore, jobRecord, cancellationToken).ConfigureAwait(false);

        TileOperationMetrics.QueueDepth.Add(1, new TagList { { "operation", request.Operation } });
        Log.JobSubmitted(_logger, jobRecord.OperationId, request.Operation, jobRecord.Spec.Backend);
        return jobRecord.OperationId;
    }

    public async Task<TileOperationProgress?> GetAsync(string jobId, CancellationToken cancellationToken = default)
        => await new ExecutionJobProgressReader(_progressStore, _jobStore).ReadAsync(jobId, cancellationToken)
            .ConfigureAwait(false) as TileOperationProgress;

    public async Task<IReadOnlyList<TileOperationProgress>> ListAsync(bool activeOnly, CancellationToken cancellationToken = default)
    {
        var operationIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var type in TileOperationTypes)
        {
            operationIds.UnionWith(await _progressStore.GetActiveOperationIdsAsync(type, cancellationToken).ConfigureAwait(false));
        }

        var result = new List<TileOperationProgress>(operationIds.Count);
        foreach (var operationId in operationIds)
        {
            var progress = await GetAsync(operationId, cancellationToken).ConfigureAwait(false);
            if (progress == null
                || (activeOnly && progress.Status is not (OperationStatus.Queued or OperationStatus.Processing)))
            {
                continue;
            }

            result.Add(progress);
        }

        return result
            .OrderByDescending(static item => item.StartedAt)
            .ToArray();
    }

    public async Task<bool> CancelAsync(string jobId, CancellationToken cancellationToken = default)
    {
        var job = await GetJobAsync(jobId, cancellationToken).ConfigureAwait(false);
        if (job == null || ExecutionJobCancellationHelper.IsTerminal(job.Status))
        {
            return false;
        }

        var backend = _backends.Resolve(job.Spec.Backend, job.Spec.TargetKind);
        if (backend == null)
        {
            Log.BackendUnavailableForCancel(_logger, jobId, job.Spec.Backend);
            return false;
        }

        var cancelled = string.Equals(job.Spec.Backend, LocalBatchComputeBackend.BackendId, StringComparison.Ordinal)
            ? await CancelLocalAsync(backend, job, cancellationToken).ConfigureAwait(false)
            : await CancelRemoteAsync(backend, job, cancellationToken).ConfigureAwait(false);
        if (!cancelled)
        {
            return false;
        }

        await CloseProgressAsync(jobId, cancellationToken).ConfigureAwait(false);
        Log.JobCancellationRequested(_logger, jobId, job.Spec.Backend);
        return true;
    }

    // Only an ended job is retried: a retry resumes the same generation, which a running job still owns.
    public async Task<string?> RetryAsync(string jobId, string? requestedBy, CancellationToken cancellationToken = default)
    {
        var job = await GetJobAsync(jobId, cancellationToken).ConfigureAwait(false);
        if (job is not { Status: ExecutionJobStatus.Failed or ExecutionJobStatus.Cancelled }
            || job.Spec.Kind != ExecutionJobKind.TileCache
            || !TileCacheExecutionSpecBuilder.TryParse(job.Spec.Parameters, out var request, out var schemaName, out var tenantScope, out _))
        {
            return null;
        }

        return await SubmitAsync(new TileOperationSubmission(request, requestedBy, schemaName, tenantScope), cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<ExecutionJobRecord?> GetJobAsync(string jobId, CancellationToken cancellationToken)
        => string.IsNullOrWhiteSpace(jobId) || _jobStore == null
            ? null
            : await _jobStore.GetAsync(jobId, cancellationToken).ConfigureAwait(false);

    private async Task<ExecutionJobRecord> CreateRecordAsync(TileOperationSubmission submission, CancellationToken cancellationToken)
    {
        var request = submission.Request;
        var jobId = $"tile-{Guid.NewGuid():N}";

        // The first submission names the resumable generation; a retry forwards it unchanged so the
        // new job resumes the same generation instead of starting a fresh full-grid pass.
        if (string.IsNullOrWhiteSpace(request.GenerationId))
        {
            request = request with { GenerationId = jobId };
        }

        var targetLayerIds = await ResolveTargetLayerIdsAsync(request, cancellationToken).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;
        return new ExecutionJobRecord
        {
            OperationId = jobId,
            Status = ExecutionJobStatus.Queued,
            CreatedAt = now,
            UpdatedAt = now,
            CurrentPhase = "Queued",
            Audit = new OperationAuditInfo { RequestedBy = submission.RequestedBy },
            // Concurrent generations of one (service, gridset, style) window run one at a time, so two
            // workers never seed, expire or delete the same key window at once.
            Concurrency = new OperationConcurrencyPolicy
            {
                PartitionKey = TileCacheExecutionSpecBuilder.BuildPartitionKey(request, targetLayerIds),
                RequiresExclusiveLease = true
            },
            Spec = TileCacheExecutionSpecBuilder.Build(request, submission.SchemaName, submission.TenantScope, _options.CurrentValue)
        };
    }

    // GET /admin/tile-operations/jobs/{id} answers from this progress before a worker claims the job.
    private Task SeedProgressAsync(string jobId, TileOperationStartRequest request, CancellationToken cancellationToken)
        => _progressStore.SetProgressAsync(
            jobId,
            TileOperationProgress.CreateInitial(jobId, request.Operation, request.ServiceId, request.LayerId, request.TileMatrixSetId),
            ProgressRetention,
            cancellationToken);

    private async Task DispatchAsync(
        IExecutionJobStore jobStore,
        ExecutionJobRecord job,
        CancellationToken cancellationToken)
    {
        if (string.Equals(job.Spec.Backend, LocalBatchComputeBackend.BackendId, StringComparison.Ordinal))
        {
            await EnqueueAsync(_jobQueue!, job.OperationId, cancellationToken).ConfigureAwait(false);
            return;
        }

        try
        {
            await SubmitToRemoteBackendAsync(jobStore, job, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            await ExecutionJobSubmissionHelper.TryRollbackCreatedJobAsync(
                jobStore,
                job.OperationId,
                progressStore: _progressStore,
                progressRetention: ProgressRetention,
                failureMessage: "Tile-cache submission failed.",
                cancellationToken: CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    // A queued job ends here; a claimed job keeps running until its worker sees the request.
    private async Task<bool> CancelLocalAsync(
        IBatchComputeBackend backend,
        ExecutionJobRecord job,
        CancellationToken cancellationToken)
    {
        var outcome = await ExecutionJobCancellationHelper.TryApplyAsync(
            _jobStore!, job.OperationId, job, "Cancelled", cancellationToken: cancellationToken).ConfigureAwait(false);
        // Ending a queued job here fires no terminal callback, so it leaves the outstanding count here.
        if (outcome.State == ExecutionJobCancellationState.Cancelled && job.ClaimedBy == null)
        {
            TileOperationMetrics.RecordJobEnded(job);
        }

        if (outcome.State is not (ExecutionJobCancellationState.Cancelled or ExecutionJobCancellationState.CancellationRequested))
        {
            return false;
        }

        await backend.CancelAsync(outcome.Job ?? job, cancellationToken).ConfigureAwait(false);
        return true;
    }

    private async Task<bool> CancelRemoteAsync(
        IBatchComputeBackend backend,
        ExecutionJobRecord job,
        CancellationToken cancellationToken)
    {
        var stamp = await ExecutionJobCancellationHelper.TryStampRemoteCancelRequestedAtAsync(
            _jobStore!, job, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (stamp.Outcome is not (RemoteCancelStampOutcome.Stamped or RemoteCancelStampOutcome.AlreadyStamped))
        {
            return false;
        }

        var observation = await backend.CancelAsync(stamp.Job!, cancellationToken).ConfigureAwait(false);
        var applied = await ExecutionJobCancellationHelper.TryApplyBackendCancelAsync(
            _jobStore!, stamp.Job!, observation, cancellationToken).ConfigureAwait(false);
        return applied.Outcome == BackendCancelApplyOutcome.Applied;
    }

    private async Task CloseProgressAsync(string jobId, CancellationToken cancellationToken)
    {
        var progress = await _progressStore.GetProgressAsync<TileOperationProgress>(jobId, cancellationToken)
            .ConfigureAwait(false);
        if (progress is { Status: OperationStatus.Queued or OperationStatus.Processing })
        {
            var cancelled = progress.WithCancellation(DateTimeOffset.UtcNow, "Cancellation requested");
            await _progressStore.TrySetProgressAsync(
                    new OperationProgressWrite(jobId, cancelled, ProgressRetention),
                    progress.Status,
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    // The record is durable, so the operation is accepted: the next reconciliation sweep queues a
    // local job whose enqueue was lost.
    private async Task EnqueueAsync(IJobQueue jobQueue, string jobId, CancellationToken cancellationToken)
    {
        try
        {
            await jobQueue.EnqueueAsync(jobId, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.EnqueueDeferredToDispatchRepair(_logger, jobId, ex);
        }
    }

    private async Task SubmitToRemoteBackendAsync(
        IExecutionJobStore jobStore,
        ExecutionJobRecord job,
        CancellationToken cancellationToken)
    {
        var backend = _backends.Resolve(job.Spec.Backend, job.Spec.TargetKind);
        if (backend == null)
        {
            throw new InvalidOperationException(
                $"No batch compute backend registered for '{job.Spec.Backend}' ({job.Spec.TargetKind}).");
        }

        await ExecutionJobSubmissionHelper.StartOnRemoteBackendAsync(
            job,
            backend,
            jobStore,
            _progressStore,
            ProgressRetention,
            _logger,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<int>> ResolveTargetLayerIdsAsync(
        TileOperationStartRequest request,
        CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var graphProvider = scope.ServiceProvider.GetRequiredService<IMetadataV2GraphProvider>();
        return await TileCacheTargetResolver.ResolveLayerIdsAsync(
            request,
            graphProvider,
            cancellationToken).ConfigureAwait(false);
    }

    private static partial class Log
    {
        [LoggerMessage(9220, LogLevel.Information,
            "Tile-cache job {OperationId} submitted: operation={Operation}, backend={Backend}")]
        public static partial void JobSubmitted(ILogger logger, string operationId, string operation, string backend);

        [LoggerMessage(9221, LogLevel.Information,
            "Tile-cache job {OperationId} cancellation requested on backend {Backend}")]
        public static partial void JobCancellationRequested(ILogger logger, string operationId, string backend);

        [LoggerMessage(9223, LogLevel.Warning,
            "Tile-cache job {OperationId} was accepted but could not be queued; the next reconciliation sweep queues it")]
        public static partial void EnqueueDeferredToDispatchRepair(ILogger logger, string operationId, Exception exception);

        [LoggerMessage(9222, LogLevel.Warning,
            "Tile-cache job {OperationId} cannot be cancelled: backend {Backend} is not registered")]
        public static partial void BackendUnavailableForCancel(ILogger logger, string operationId, string backend);
    }
}

internal interface ITileCacheJobService
{
    Task<string> SubmitAsync(TileOperationSubmission submission, CancellationToken cancellationToken = default);

    Task<TileOperationProgress?> GetAsync(string jobId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<TileOperationProgress>> ListAsync(bool activeOnly, CancellationToken cancellationToken = default);

    Task<bool> CancelAsync(string jobId, CancellationToken cancellationToken = default);

    Task<string?> RetryAsync(string jobId, string? requestedBy, CancellationToken cancellationToken = default);
}

// RequestedBy records who asked for the operation; the warming targets and expiry sweep use SchedulerIdentityName.
internal sealed record TileOperationSubmission(
    TileOperationStartRequest Request,
    string? RequestedBy,
    string? SchemaName = null,
    string? TenantScope = null);
