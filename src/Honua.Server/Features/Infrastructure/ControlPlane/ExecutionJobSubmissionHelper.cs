// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;

namespace Honua.Server.Features.Infrastructure.ControlPlane;

internal static class ExecutionJobSubmissionHelper
{
    internal const string SubmissionFailurePhase = "Failed (submission)";
    internal const string SubmissionFailureMessage = "Submission failed: progress or queue persistence error.";

    public static async Task TryRollbackCreatedJobAsync(
        IExecutionJobStore jobStore,
        string operationId,
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
                ErrorMessage = SubmissionFailureMessage,
                CurrentPhase = SubmissionFailurePhase
            };

            await jobStore.TrySetAsync(failedJob, cancellationToken: cancellationToken).ConfigureAwait(false);
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
}
