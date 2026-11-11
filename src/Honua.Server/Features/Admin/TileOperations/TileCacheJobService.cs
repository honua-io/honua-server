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
        var request = TileCacheExecutionSpecBuilder.NormalizeRequest(submission.Request);
        var jobStore = _jobStore ?? throw new InvalidOperationException(JobStoreUnavailableMessage);
        var jobQueue = _jobQueue ?? throw new InvalidOperationException(JobStoreUnavailableMessage);

        var options = _options.CurrentValue;
        var jobId = $"tile-{Guid.NewGuid():N}";

        // The first submission names the resumable generation; a retry forwards it unchanged so the
        // new job resumes the same generation instead of starting a fresh full-grid pass.
        if (string.IsNullOrWhiteSpace(request.GenerationId))
        {
            request = request with { GenerationId = jobId };
        }

        var now = DateTimeOffset.UtcNow;
        var spec = TileCacheExecutionSpecBuilder.Build(request, submission.SchemaName, submission.TenantScope, options);
        var targetLayerIds = await ResolveTargetLayerIdsAsync(request, cancellationToken).ConfigureAwait(false);

        var jobRecord = new ExecutionJobRecord
        {
            OperationId = jobId,
            Status = ExecutionJobStatus.Queued,
            CreatedAt = now,
            UpdatedAt = now,
            CurrentPhase = "Queued",
            Audit = new OperationAuditInfo { RequestedBy = submission.RequestedBy },
            // Replica safety (issue #2661): serialize concurrent generations of the same
            // (service, gridset, style) window under an exclusive lease so two backend workers
            // never seed/expire/delete the same key window at once.
            Concurrency = new OperationConcurrencyPolicy
            {
                PartitionKey = TileCacheExecutionSpecBuilder.BuildPartitionKey(request, targetLayerIds),
                RequiresExclusiveLease = true
            },
            Spec = spec
        };

        var created = await jobStore.TryCreateAsync(jobRecord, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        if (!created)
        {
            throw new InvalidOperationException($"Failed to create tile-cache execution job '{jobId}'.");
        }

        // Seed the tile-shaped progress so GET /admin/tile-operations/jobs/{id} reflects
        // the submission immediately, before the worker claims it.
        var progress = TileOperationProgress.CreateInitial(
            jobId,
            request.Operation,
            request.ServiceId,
            request.LayerId,
            request.TileMatrixSetId);
        await _progressStore.SetProgressAsync(jobId, progress, ProgressRetention, cancellationToken)
            .ConfigureAwait(false);

        if (string.Equals(spec.Backend, LocalBatchComputeBackend.BackendId, StringComparison.Ordinal))
        {
            await EnqueueAsync(jobQueue, jobId, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            try
            {
                await SubmitToRemoteBackendAsync(jobStore, jobRecord, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                await ExecutionJobSubmissionHelper.TryRollbackCreatedJobAsync(
                    jobStore,
                    jobId,
                    progressStore: _progressStore,
                    progressRetention: ProgressRetention,
                    failureMessage: "Tile-cache submission failed.",
                    cancellationToken: CancellationToken.None).ConfigureAwait(false);
                throw;
            }
        }

        TileOperationMetrics.QueueDepth.Add(1, new TagList { { "operation", request.Operation } });
        Log.JobSubmitted(_logger, jobId, request.Operation, spec.Backend);
        return jobId;
    }

    public Task<TileOperationProgress?> GetAsync(string jobId, CancellationToken cancellationToken = default)
        => _progressStore.GetProgressAsync<TileOperationProgress>(jobId, cancellationToken);

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
            var progress = await _progressStore.GetProgressAsync<TileOperationProgress>(operationId, cancellationToken).ConfigureAwait(false);
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
        if (string.IsNullOrWhiteSpace(jobId) || _jobStore == null)
        {
            return false;
        }

        var job = await _jobStore.GetAsync(jobId, cancellationToken).ConfigureAwait(false);
        if (job == null)
        {
            return false;
        }

        if (IsTerminal(job.Status))
        {
            return false;
        }

        var backend = _backends.Resolve(job.Spec.Backend, job.Spec.TargetKind);
        if (backend == null)
        {
            Log.BackendUnavailableForCancel(_logger, jobId, job.Spec.Backend);
            return false;
        }

        var observation = await backend.CancelAsync(job, cancellationToken).ConfigureAwait(false);

        // Durably record the cancellation request so the reconciler and worker honour it,
        // and bridge the tile-shaped progress for the admin status API.
        var cancelling = job with
        {
            CancellationRequestedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            Status = observation.Status,
            CurrentPhase = observation.Message ?? "Cancellation requested"
        };
        await _jobStore.TrySetAsync(cancelling, cancellationToken: cancellationToken).ConfigureAwait(false);

        var progress = await _progressStore.GetProgressAsync<TileOperationProgress>(jobId, cancellationToken)
            .ConfigureAwait(false);
        if (progress != null && progress.Status is OperationStatus.Queued or OperationStatus.Processing)
        {
            var cancelled = (TileOperationProgress)progress.WithCancellation(DateTimeOffset.UtcNow, "Cancellation requested");
            await _progressStore.SetProgressAsync(jobId, cancelled, ProgressRetention, cancellationToken)
                .ConfigureAwait(false);
        }

        Log.JobCancellationRequested(_logger, jobId, job.Spec.Backend);
        return true;
    }

    public async Task<string?> RetryAsync(string jobId, string? requestedBy, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(jobId) || _jobStore == null)
        {
            return null;
        }

        var progress = await _progressStore.GetProgressAsync<TileOperationProgress>(jobId, cancellationToken).ConfigureAwait(false);
        if (progress == null || progress.Status is not (OperationStatus.Failed or OperationStatus.Cancelled))
        {
            return null;
        }

        var job = await _jobStore.GetAsync(jobId, cancellationToken).ConfigureAwait(false);
        if (job is null
            || job.Spec.Kind != ExecutionJobKind.TileCache
            || !TileCacheExecutionSpecBuilder.TryParse(job.Spec.Parameters, out var request, out var schemaName, out var tenantScope, out _))
        {
            return null;
        }

        return await SubmitAsync(new TileOperationSubmission(request, requestedBy, schemaName, tenantScope), cancellationToken)
            .ConfigureAwait(false);
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

    private static bool IsTerminal(ExecutionJobStatus status)
        => status is ExecutionJobStatus.Succeeded
            or ExecutionJobStatus.Failed
            or ExecutionJobStatus.Cancelled;

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
