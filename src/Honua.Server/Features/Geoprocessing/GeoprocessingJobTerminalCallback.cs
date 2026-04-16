// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;

namespace Honua.Server.Features.Geoprocessing;

/// <summary>
/// Synchronizes the admin progress store when a geoprocessing execution job
/// reaches a terminal state, keeping the admin operations API consistent with
/// the authoritative <see cref="IExecutionJobStore"/> record.
/// </summary>
internal sealed partial class GeoprocessingJobTerminalCallback(
    IUniversalProgressStore progressStore,
    ILogger<GeoprocessingJobTerminalCallback> logger) : IJobTerminalCallback
{
    private static readonly TimeSpan ProgressRetention = TimeSpan.FromDays(7);

    public async ValueTask OnTerminalAsync(ExecutionJobRecord job, CancellationToken cancellationToken)
    {
        if (job.Spec.Kind != ExecutionJobKind.Geoprocessing)
        {
            return;
        }

        try
        {
            var progress = await progressStore.GetProgressAsync<GeoprocessingProgress>(
                job.OperationId, cancellationToken).ConfigureAwait(false);
            if (progress == null)
            {
                return;
            }

            if (progress.Status is OperationStatus.Completed or OperationStatus.Failed or OperationStatus.Cancelled)
            {
                return;
            }

            var completedAt = job.CompletedAt ?? DateTimeOffset.UtcNow;

            GeoprocessingProgress? updated = job.Status switch
            {
                ExecutionJobStatus.Succeeded => progress with
                {
                    WorkflowStatus = GeoprocessingWorkflowStatus.Completed,
                    CurrentStageStatus = GeoprocessingStageStatus.Completed,
                    CompletedAt = completedAt,
                    CurrentPhase = "Completed"
                },
                ExecutionJobStatus.Failed => progress with
                {
                    WorkflowStatus = GeoprocessingWorkflowStatus.Failed,
                    CurrentStageStatus = GeoprocessingStageStatus.Failed,
                    CompletedAt = completedAt,
                    ErrorMessage = job.ErrorMessage,
                    CurrentPhase = "Failed"
                },
                ExecutionJobStatus.Cancelled => (GeoprocessingProgress)progress.WithCancellation(
                    completedAt, "Cancelled"),
                _ => null
            };

            if (updated != null)
            {
                await progressStore.SetProgressAsync(
                    job.OperationId, updated, ProgressRetention, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            Log.ProgressSyncFailed(logger, job.OperationId, ex);
        }
    }

    private static partial class Log
    {
        [LoggerMessage(8020, LogLevel.Warning, "Failed to synchronize admin progress for terminal job {OperationId}; admin view may be stale until TTL expiry")]
        public static partial void ProgressSyncFailed(ILogger logger, string operationId, Exception exception);
    }
}
