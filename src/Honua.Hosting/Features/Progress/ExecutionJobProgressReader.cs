// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;

namespace Honua.Infrastructure.Progress;

internal sealed class ExecutionJobProgressReader(IUniversalProgressStore progressStore, IExecutionJobStore? jobStore)
{
    public async Task<IOperationProgress?> ReadAsync(string operationId, CancellationToken cancellationToken)
    {
        var progress = await progressStore.GetProgressAsync(operationId, cancellationToken).ConfigureAwait(false);
        return progress is null ? null : await RepairAsync(progress, cancellationToken).ConfigureAwait(false);
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
            ExecutionJobStatus.Succeeded => OperationStatus.Completed,
            ExecutionJobStatus.Failed => OperationStatus.Failed,
            ExecutionJobStatus.Cancelled => OperationStatus.Cancelled,
            _ => (OperationStatus?)null
        };
        if (status is null)
        {
            return progress;
        }

        var completedAt = job.CompletedAt ?? job.UpdatedAt;
        var phase = status.Value.ToString();
        return progress switch
        {
            ExportProgress export when job.Spec.Kind == ExecutionJobKind.Export => export with
            {
                Status = status.Value,
                CompletedAt = completedAt,
                CurrentPhase = phase,
                ErrorMessage = job.ErrorMessage,
                DownloadUrl = status == OperationStatus.Completed ? export.DownloadUrl : null
            },
            TileOperationProgress tiles when job.Spec.Kind == ExecutionJobKind.TileCache => tiles with
            {
                Status = status.Value,
                CompletedAt = completedAt,
                CurrentPhase = phase,
                ErrorMessage = job.ErrorMessage
            },
            _ => progress
        };
    }
}
