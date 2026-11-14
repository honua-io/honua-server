// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Migration;

namespace Honua.Infrastructure.Progress;

internal sealed class ExecutionJobProgressReader(IUniversalProgressStore progressStore, IExecutionJobStore? jobStore)
{
    internal const string InitialProgressParameter = "honua.progress.initial";

    public async Task<IOperationProgress?> ReadAsync(string operationId, CancellationToken cancellationToken)
    {
        var progress = await progressStore.GetProgressAsync(operationId, cancellationToken).ConfigureAwait(false);
        if (progress is not null)
        {
            return await RepairAsync(progress, cancellationToken).ConfigureAwait(false);
        }

        var job = jobStore is null ? null : await jobStore.GetAsync(operationId, cancellationToken).ConfigureAwait(false);
        if (job is null || !job.Spec.Parameters.TryGetValue(InitialProgressParameter, out var initial))
        {
            return null;
        }

        // Retained jobs outlive the progress TTL. Rebuild the response without recreating a stale
        // writable snapshot that could compete with a worker or operator retry.
        var seed = OperationProgressSerializer.Deserialize(initial);
        return seed is not null && string.Equals(seed.OperationId, operationId, StringComparison.Ordinal)
            && (seed is ExportProgress && job.Spec.Kind == ExecutionJobKind.Export
                || seed is TileOperationProgress && job.Spec.Kind == ExecutionJobKind.TileCache)
            ? Project(seed, job)
            : null;
    }

    public async Task<IOperationProgress> RepairAsync(IOperationProgress progress, CancellationToken cancellationToken)
    {
        if (jobStore is null || progress is not (ExportProgress or TileOperationProgress))
        {
            return progress;
        }

        var job = await jobStore.GetAsync(progress.OperationId, cancellationToken).ConfigureAwait(false);
        // Derive the response without writing a stale terminal snapshot over an operator retry.
        return job is null ? progress : Project(progress, job);
    }

    public static IOperationProgress Project(IOperationProgress progress, ExecutionJobRecord job)
    {
        var status = job.Status switch
        {
            ExecutionJobStatus.Queued => OperationStatus.Queued,
            ExecutionJobStatus.Provisioning or ExecutionJobStatus.Running => OperationStatus.Processing,
            ExecutionJobStatus.Succeeded => OperationStatus.Completed,
            ExecutionJobStatus.Failed => OperationStatus.Failed,
            ExecutionJobStatus.Cancelled => OperationStatus.Cancelled,
            _ => (OperationStatus?)null
        };
        if (status is null)
        {
            return progress;
        }

        var projection = new StatusProjection(status.Value, progress, job);
        return progress switch
        {
            ExportProgress export when job.Spec.Kind == ExecutionJobKind.Export => ProjectExport(export, projection),
            TileOperationProgress tiles when job.Spec.Kind == ExecutionJobKind.TileCache => ProjectTiles(tiles, projection),
            _ => progress
        };
    }

    private static ExportProgress ProjectExport(ExportProgress export, StatusProjection projection)
        => export with
        {
            Status = projection.Status,
            CompletedAt = projection.CompletedAt,
            CurrentPhase = projection.Phase,
            ErrorMessage = projection.ErrorMessage,
            ProcessedFeatures = projection.ResetCounts ? 0 : export.ProcessedFeatures,
            OutputSizeBytes = projection.ResetCounts ? 0 : export.OutputSizeBytes,
            DownloadUrl = projection.Status == OperationStatus.Completed ? export.DownloadUrl : null
        };

    private static TileOperationProgress ProjectTiles(TileOperationProgress tiles, StatusProjection projection)
        => tiles with
        {
            Status = projection.Status,
            CompletedAt = projection.CompletedAt,
            CurrentPhase = projection.Phase,
            ErrorMessage = projection.ErrorMessage,
            ProcessedTiles = projection.ResetCounts ? 0 : tiles.ProcessedTiles,
            SuccessfulTiles = projection.ResetCounts ? 0 : tiles.SuccessfulTiles,
            FailedTiles = projection.ResetCounts ? 0 : tiles.FailedTiles,
            DownloadUrl = projection.Status == OperationStatus.Completed ? tiles.DownloadUrl : null,
            PublishedArtifact = projection.Status == OperationStatus.Completed ? tiles.PublishedArtifact : null
        };

    private sealed record StatusProjection(OperationStatus Status, IOperationProgress Progress, ExecutionJobRecord Job)
    {
        private bool Active => Status is OperationStatus.Queued or OperationStatus.Processing;
        public bool ResetCounts => Active && (Status == OperationStatus.Queued || Progress.Status != Status);
        public DateTimeOffset? CompletedAt => Active ? null : Job.CompletedAt ?? Job.UpdatedAt;
        public string? ErrorMessage => Active ? null : Job.ErrorMessage;
        public string? Phase => Status == OperationStatus.Processing && Progress.Status == Status
            ? Progress.CurrentPhase
            : Status.ToString();
    }
}
