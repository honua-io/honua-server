// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Infrastructure.Progress;

namespace Honua.Io.Export;

// The executor records a completed export itself. Every other ending (failure, cancellation, license
// expiry, timeout, retries exhausted after a lost node) is decided by the worker and lands here.
internal sealed class ExportJobTerminalCallback(
    IUniversalProgressStore progressStore,
    ILogger<ExportJobTerminalCallback> logger,
    IServiceScopeFactory? scopeFactory = null) : IJobTerminalCallback
{
    public async ValueTask OnTerminalAsync(ExecutionJobRecord job, CancellationToken cancellationToken)
    {
        if (job.Spec.Kind != ExecutionJobKind.Export
            || job.Status is not (ExecutionJobStatus.Failed or ExecutionJobStatus.Cancelled))
        {
            return;
        }

        try
        {
            await DeleteArtifactsAsync(job, cancellationToken).ConfigureAwait(false);
            var progress = await progressStore.GetProgressAsync<ExportProgress>(job.OperationId, cancellationToken)
                .ConfigureAwait(false);
            if (progress is null)
            {
                return;
            }

            var terminal = ExecutionJobProgressReader.Project(progress, job);
            await progressStore.TrySetProgressAsync(
                    new OperationProgressWrite(job.OperationId, terminal, ExportJobService.Retention),
                    progress.Status,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        // Best effort by contract: a failed projection must not block the job's terminal transition.
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ExportJobServiceLog.TerminalProgressFailed(logger, job.OperationId, ex);
        }
    }

    private async Task DeleteArtifactsAsync(ExecutionJobRecord job, CancellationToken cancellationToken)
    {
        if (scopeFactory is null || job.ArtifactReferences.Count == 0)
        {
            return;
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var storage = scope.ServiceProvider.GetRequiredService<ICloudFileStorage>();
        foreach (var fileId in job.ArtifactReferences)
        {
            try
            {
                await storage.DeleteAsync(fileId, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                ExportJobServiceLog.TerminalProgressFailed(logger, job.OperationId, ex);
            }
        }
    }
}
