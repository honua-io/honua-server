// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Infrastructure.Progress;

namespace Honua.Server.Features.Admin.TileOperations;

// Every ended tile job leaves the outstanding count here. The executor reports its own outcome; this
// closes the tile progress when the worker ends the job without it (license expiry, timeout, operator
// cancellation, retries exhausted after a lost node).
internal sealed partial class TileCacheJobTerminalCallback(
    IUniversalProgressStore progressStore,
    ILogger<TileCacheJobTerminalCallback> logger) : IJobTerminalCallback
{
    private static readonly TimeSpan ProgressRetention = TimeSpan.FromHours(24);

    public async ValueTask OnTerminalAsync(ExecutionJobRecord job, CancellationToken cancellationToken)
    {
        if (job.Spec.Kind != ExecutionJobKind.TileCache)
        {
            return;
        }

        TileOperationMetrics.RecordJobEnded(job);
        if (job.Status is not (ExecutionJobStatus.Failed or ExecutionJobStatus.Cancelled))
        {
            return;
        }

        try
        {
            var progress = await progressStore.GetProgressAsync<TileOperationProgress>(job.OperationId, cancellationToken)
                .ConfigureAwait(false);
            if (progress is null || progress.Status is not (OperationStatus.Queued or OperationStatus.Processing))
            {
                return;
            }

            var completedAt = job.CompletedAt ?? DateTimeOffset.UtcNow;
            var terminal = job.Status == ExecutionJobStatus.Cancelled
                ? (TileOperationProgress)progress.WithCancellation(completedAt, "Cancelled")
                : progress with
                {
                    Status = OperationStatus.Failed,
                    CompletedAt = completedAt,
                    ErrorMessage = job.ErrorMessage ?? "Tile operation failed.",
                    CurrentPhase = "Failed"
                };
            await progressStore.TrySetProgressAsync(
                    new OperationProgressWrite(job.OperationId, terminal, ProgressRetention),
                    progress.Status,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        // Best effort by contract: a failed projection must not block the job's terminal transition.
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.ProgressProjectionFailed(logger, job.OperationId, ex);
        }
    }

    private static partial class Log
    {
        [LoggerMessage(9225, LogLevel.Warning, "Failed to close tile operation progress for terminal job {OperationId}")]
        public static partial void ProgressProjectionFailed(ILogger logger, string operationId, Exception exception);
    }
}
