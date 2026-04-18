// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;

namespace Honua.Server.Features.Infrastructure.ControlPlane;

internal static partial class ExecutionJobSubmissionHelper
{
    internal const string SubmissionFailurePhase = "Failed (submission)";
    internal const string SubmissionFailureMessage = "Submission failed.";

    public static async Task TryRollbackCreatedJobAsync(
        IExecutionJobStore jobStore,
        string operationId,
        IUniversalProgressStore? progressStore = null,
        TimeSpan? progressRetention = null,
        string? failureMessage = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(jobStore);
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);

        try
        {
            var current = await jobStore.GetAsync(operationId, cancellationToken).ConfigureAwait(false);
            if (current == null || current.Status is not (ExecutionJobStatus.Queued or ExecutionJobStatus.Provisioning))
            {
                return;
            }

            var now = DateTimeOffset.UtcNow;
            var failedJob = current with
            {
                Status = ExecutionJobStatus.Failed,
                UpdatedAt = now,
                CompletedAt = now,
                ErrorMessage = failureMessage ?? SubmissionFailureMessage,
                CurrentPhase = SubmissionFailurePhase
            };

            var committed = await jobStore.TrySetAsync(failedJob, cancellationToken: cancellationToken).ConfigureAwait(false);

            if (progressStore != null && committed)
            {
                await BridgeTerminalSubmissionProgressAsync(
                    progressStore, failedJob, progressRetention ?? TimeSpan.FromDays(7), cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch
        {
            // Best-effort rollback; job TTL or manual intervention will repair.
        }
    }

    public static bool IsSubmissionRollback(ExecutionJobRecord job)
        => job.Status == ExecutionJobStatus.Failed
            && string.Equals(job.CurrentPhase, SubmissionFailurePhase, StringComparison.Ordinal);

    /// <summary>
    /// Submits an execution job to a remote batch compute backend with CAS guards
    /// around the Provisioning and post-start state transitions.
    /// </summary>
    public static async Task<ExecutionJobRecord> StartOnRemoteBackendAsync(
        ExecutionJobRecord job,
        IBatchComputeBackend backend,
        IExecutionJobStore jobStore,
        IUniversalProgressStore progressStore,
        TimeSpan progressRetention,
        ILogger? logger,
        CancellationToken cancellationToken)
    {
        var provisioning = job with
        {
            Status = ExecutionJobStatus.Provisioning,
            UpdatedAt = DateTimeOffset.UtcNow,
            CurrentPhase = "Submitting to backend"
        };
        if (!await jobStore.TrySetAsync(provisioning, cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            var current = await jobStore.GetAsync(job.OperationId, cancellationToken).ConfigureAwait(false);
            return current ?? job;
        }

        var submission = await backend.StartAsync(provisioning, cancellationToken).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;
        var updated = provisioning with
        {
            Status = submission.Status,
            UpdatedAt = now,
            CompletedAt = ExecutionJobReconciler.IsTerminal(submission.Status) ? now : provisioning.CompletedAt,
            ProviderOperationId = submission.ProviderOperationId ?? provisioning.ProviderOperationId,
            CurrentPhase = submission.Message ?? provisioning.CurrentPhase,
            AttemptCount = provisioning.AttemptCount + 1
        };

        if (!await jobStore.TrySetAsync(updated, cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            if (logger != null)
            {
                Log.PostStartCasConflict(logger, job.OperationId);
            }

            var current = await jobStore.GetAsync(job.OperationId, cancellationToken).ConfigureAwait(false);
            return current ?? updated;
        }

        await BridgeTerminalSubmissionProgressAsync(progressStore, updated, progressRetention, cancellationToken)
            .ConfigureAwait(false);
        return updated;
    }

    /// <summary>
    /// Bridges geoprocessing progress when a backend submission returns a terminal status
    /// synchronously, before the job drops out of the active index.
    /// </summary>
    public static async Task BridgeTerminalSubmissionProgressAsync(
        IUniversalProgressStore progressStore,
        ExecutionJobRecord job,
        TimeSpan retention,
        CancellationToken cancellationToken = default)
    {
        if (!ExecutionJobReconciler.IsTerminal(job.Status))
        {
            return;
        }

        try
        {
            var existing = await progressStore
                .GetProgressAsync<GeoprocessingProgress>(job.OperationId, cancellationToken)
                .ConfigureAwait(false);

            var bridged = ExecutionJobReconciler.BuildProgress(job, existing);
            if (bridged != null)
            {
                await progressStore
                    .SetProgressAsync(job.OperationId, bridged, retention, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch
        {
            // Best-effort; terminal status is already persisted on the job record.
        }
    }

    internal static partial class Log
    {
        [LoggerMessage(9040, LogLevel.Warning, "Post-start CAS conflict for execution job {OperationId}: returning authoritative store record")]
        public static partial void PostStartCasConflict(ILogger logger, string operationId);
    }
}
