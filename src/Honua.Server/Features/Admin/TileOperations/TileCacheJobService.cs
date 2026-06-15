// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Infrastructure.Progress;
using Honua.ControlPlane;
using Microsoft.Extensions.Options;

namespace Honua.Server.Features.Admin.TileOperations;

/// <summary>
/// Submits tile-cache / PMTiles operations as durable
/// <see cref="ExecutionJobKind.TileCache"/> execution jobs and dispatches them to a
/// configured <see cref="IBatchComputeBackend"/> (issue #1697). Mirrors the
/// geoprocessing submission flow: build an <see cref="ExecutionJobSpec"/>, create the
/// <see cref="ExecutionJobRecord"/> in <see cref="IExecutionJobStore"/>, enqueue for
/// the in-process worker when the backend is <c>local</c>, then submit to the remote
/// backend otherwise. Progress, retry, heartbeat, and cancellation flow through the
/// shared <c>ExecutionJobReconciler</c> and <see cref="IUniversalProgressStore"/>, and
/// a <see cref="TileOperationProgress"/> is seeded so the existing admin job-status API
/// observes batch jobs through the same endpoints as in-process jobs.
/// <para>
/// This service is only registered and consulted when <see cref="TileCacheBatchOptions.Enabled"/>
/// is set and the execution-job substrate (Redis-backed store/queue) is present; the
/// legacy in-process channel remains the zero-config default fallback otherwise.
/// </para>
/// </summary>
internal sealed partial class TileCacheJobService : ITileCacheJobService
{
    private readonly IExecutionJobStore _jobStore;
    private readonly IUniversalProgressStore _progressStore;
    private readonly IJobQueue? _jobQueue;
    private readonly IReadOnlyList<IBatchComputeBackend> _backends;
    private readonly IOptionsMonitor<TileCacheBatchOptions> _options;
    private readonly ILogger<TileCacheJobService> _logger;

    private static readonly TimeSpan ProgressRetention = TimeSpan.FromHours(24);

    public TileCacheJobService(
        IExecutionJobStore jobStore,
        IUniversalProgressStore progressStore,
        IEnumerable<IBatchComputeBackend> backends,
        IOptionsMonitor<TileCacheBatchOptions> options,
        ILogger<TileCacheJobService> logger,
        IJobQueue? jobQueue = null)
    {
        _jobStore = jobStore ?? throw new ArgumentNullException(nameof(jobStore));
        _progressStore = progressStore ?? throw new ArgumentNullException(nameof(progressStore));
        _backends = (backends ?? throw new ArgumentNullException(nameof(backends))).ToArray();
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _jobQueue = jobQueue;
    }

    /// <summary>
    /// Whether batch dispatch is currently enabled by configuration.
    /// </summary>
    public bool IsEnabled => _options.CurrentValue.Enabled;

    public async Task<string> SubmitAsync(
        TileOperationStartRequest request,
        string? schemaName,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var options = _options.CurrentValue;
        var jobId = $"tile-{Guid.NewGuid():N}";
        var now = DateTimeOffset.UtcNow;
        var spec = TileCacheExecutionSpecBuilder.Build(request, schemaName, options);

        var jobRecord = new ExecutionJobRecord
        {
            OperationId = jobId,
            Status = ExecutionJobStatus.Queued,
            CreatedAt = now,
            UpdatedAt = now,
            CurrentPhase = "Queued",
            Spec = spec
        };

        var created = await _jobStore.TryCreateAsync(jobRecord, cancellationToken: cancellationToken)
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

        try
        {
            if (string.Equals(spec.Backend, LocalBatchComputeBackend.BackendId, StringComparison.Ordinal))
            {
                if (_jobQueue != null)
                {
                    await _jobQueue.EnqueueAsync(jobId, cancellationToken: cancellationToken).ConfigureAwait(false);
                }
            }
            else
            {
                await SubmitToRemoteBackendAsync(jobRecord, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            await ExecutionJobSubmissionHelper.TryRollbackCreatedJobAsync(
                _jobStore,
                jobId,
                progressStore: _progressStore,
                progressRetention: ProgressRetention,
                failureMessage: "Tile-cache submission failed.",
                cancellationToken: CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        Log.JobSubmitted(_logger, jobId, request.Operation, spec.Backend);
        return jobId;
    }

    public async Task<bool> CancelAsync(string jobId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(jobId))
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

    private async Task SubmitToRemoteBackendAsync(ExecutionJobRecord job, CancellationToken cancellationToken)
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
            _jobStore,
            _progressStore,
            ProgressRetention,
            _logger,
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

        [LoggerMessage(9222, LogLevel.Warning,
            "Tile-cache job {OperationId} cannot be cancelled: backend {Backend} is not registered")]
        public static partial void BackendUnavailableForCancel(ILogger logger, string operationId, string backend);
    }
}

/// <summary>
/// Submission/cancellation surface for batch-dispatched tile-cache execution jobs.
/// </summary>
internal interface ITileCacheJobService
{
    /// <summary>
    /// Whether config-driven batch dispatch is currently enabled.
    /// </summary>
    bool IsEnabled { get; }

    /// <summary>
    /// Submits a tile operation as a <see cref="ExecutionJobKind.TileCache"/> execution
    /// job and returns the durable job identifier.
    /// </summary>
    Task<string> SubmitAsync(
        TileOperationStartRequest request,
        string? schemaName,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Requests cancellation of a batch-dispatched tile-cache job. Returns
    /// <c>false</c> when the job is unknown, already terminal, or its backend is
    /// unavailable.
    /// </summary>
    Task<bool> CancelAsync(string jobId, CancellationToken cancellationToken = default);
}
