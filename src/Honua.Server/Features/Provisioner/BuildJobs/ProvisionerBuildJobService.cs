// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.ControlPlane;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Microsoft.Extensions.Options;

namespace Honua.Server.Features.Provisioner.BuildJobs;

/// <summary>
/// Submits per-area geocoder/router build operations as durable
/// <see cref="ExecutionJobKind.GeocoderBuild"/> / <see cref="ExecutionJobKind.RouterBuild"/>
/// execution jobs and dispatches them to a configured <see cref="IBatchComputeBackend"/>.
/// Structurally identical to <c>TileCacheJobService</c> (issue #1697): build an
/// <see cref="ExecutionJobSpec"/>, create the <see cref="ExecutionJobRecord"/> in
/// <see cref="IExecutionJobStore"/>, seed a <see cref="GeoprocessingProgress"/> so the
/// shared job-status surface observes the submission, enqueue for the in-process worker
/// when the backend is <c>local</c>, then submit to the remote backend (GP-on-Batch /
/// Fargate Spot) otherwise. Progress, retry, heartbeat, and cancellation flow through the
/// shared <c>ExecutionJobReconciler</c> and <see cref="IUniversalProgressStore"/>.
/// </summary>
internal sealed partial class ProvisionerBuildJobService : IProvisionerBuildJobService
{
    private readonly IExecutionJobStore _jobStore;
    private readonly IUniversalProgressStore _progressStore;
    private readonly IJobQueue? _jobQueue;
    private readonly IReadOnlyList<IBatchComputeBackend> _backends;
    private readonly IOptionsMonitor<ProvisionerBuildBatchOptions> _options;
    private readonly ILogger<ProvisionerBuildJobService> _logger;

    private static readonly TimeSpan ProgressRetention = TimeSpan.FromHours(24);

    public ProvisionerBuildJobService(
        IExecutionJobStore jobStore,
        IUniversalProgressStore progressStore,
        IEnumerable<IBatchComputeBackend> backends,
        IOptionsMonitor<ProvisionerBuildBatchOptions> options,
        ILogger<ProvisionerBuildJobService> logger,
        IJobQueue? jobQueue = null)
    {
        _jobStore = jobStore ?? throw new ArgumentNullException(nameof(jobStore));
        _progressStore = progressStore ?? throw new ArgumentNullException(nameof(progressStore));
        _backends = (backends ?? throw new ArgumentNullException(nameof(backends))).ToArray();
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _jobQueue = jobQueue;
    }

    /// <inheritdoc />
    public bool IsEnabled => _options.CurrentValue.Enabled;

    /// <inheritdoc />
    public Task<string> SubmitGeocoderBuildAsync(
        GeocoderBuildRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var spec = GeocoderBuildExecutionSpecBuilder.Build(request, _options.CurrentValue);
        return SubmitAsync("geocoder-build", "geo", spec, cancellationToken);
    }

    /// <inheritdoc />
    public Task<string> SubmitRouterBuildAsync(
        RouterBuildRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var spec = RouterBuildExecutionSpecBuilder.Build(request, _options.CurrentValue);
        return SubmitAsync("router-build", "route", spec, cancellationToken);
    }

    private async Task<string> SubmitAsync(
        string operationLabel,
        string idPrefix,
        ExecutionJobSpec spec,
        CancellationToken cancellationToken)
    {
        var jobId = $"{idPrefix}-{Guid.NewGuid():N}";
        var now = DateTimeOffset.UtcNow;

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
            throw new InvalidOperationException($"Failed to create {operationLabel} execution job '{jobId}'.");
        }

        // Seed geoprocessing-shaped progress so the shared job-status surface reflects the
        // submission immediately, before the worker claims the job.
        var progress = GeoprocessingProgress.CreateForSubmittedJob(jobId, spec.WorkloadName);
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
                failureMessage: $"{operationLabel} submission failed.",
                cancellationToken: CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        Log.JobSubmitted(_logger, jobId, operationLabel, spec.Backend);
        return jobId;
    }

    /// <inheritdoc />
    public async Task<bool> CancelAsync(string jobId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(jobId))
        {
            return false;
        }

        var job = await _jobStore.GetAsync(jobId, cancellationToken).ConfigureAwait(false);
        if (job == null || IsTerminal(job.Status))
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

        var cancelling = job with
        {
            CancellationRequestedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            Status = observation.Status,
            CurrentPhase = observation.Message ?? "Cancellation requested"
        };
        await _jobStore.TrySetAsync(cancelling, cancellationToken: cancellationToken).ConfigureAwait(false);

        // Bridge the geoprocessing-shaped progress so the admin status surface observes the
        // cancellation request without waiting for the reconciler.
        await ExecutionJobSubmissionHelper.BridgeExecutionJobProgressAsync(
            _progressStore, cancelling, ProgressRetention, _logger, cancellationToken).ConfigureAwait(false);

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
        [LoggerMessage(9240, LogLevel.Information,
            "Provisioner build job {OperationId} submitted: operation={Operation}, backend={Backend}")]
        public static partial void JobSubmitted(ILogger logger, string operationId, string operation, string backend);

        [LoggerMessage(9241, LogLevel.Information,
            "Provisioner build job {OperationId} cancellation requested on backend {Backend}")]
        public static partial void JobCancellationRequested(ILogger logger, string operationId, string backend);

        [LoggerMessage(9242, LogLevel.Warning,
            "Provisioner build job {OperationId} cannot be cancelled: backend {Backend} is not registered")]
        public static partial void BackendUnavailableForCancel(ILogger logger, string operationId, string backend);
    }
}

/// <summary>
/// Submission/cancellation surface for batch-dispatched per-area geocoder/router build
/// execution jobs.
/// </summary>
internal interface IProvisionerBuildJobService
{
    /// <summary>Whether config-driven batch dispatch is currently enabled.</summary>
    bool IsEnabled { get; }

    /// <summary>
    /// Submits a per-area geocoder/locator build as a
    /// <see cref="ExecutionJobKind.GeocoderBuild"/> execution job and returns the durable
    /// job identifier.
    /// </summary>
    Task<string> SubmitGeocoderBuildAsync(GeocoderBuildRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Submits a per-area routing-graph build as a
    /// <see cref="ExecutionJobKind.RouterBuild"/> execution job and returns the durable job
    /// identifier.
    /// </summary>
    Task<string> SubmitRouterBuildAsync(RouterBuildRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Requests cancellation of a batch-dispatched build job. Returns <c>false</c> when the
    /// job is unknown, already terminal, or its backend is unavailable.
    /// </summary>
    Task<bool> CancelAsync(string jobId, CancellationToken cancellationToken = default);
}
