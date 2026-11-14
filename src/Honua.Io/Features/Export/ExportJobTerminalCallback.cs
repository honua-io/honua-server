// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;

namespace Honua.Io.Export;

internal sealed class ExportJobTerminalCallback(
    ILogger<ExportJobTerminalCallback> logger,
    IServiceScopeFactory? scopeFactory = null) : IJobTerminalCallback
{
    public async ValueTask OnTerminalAsync(ExecutionJobRecord job, CancellationToken cancellationToken)
    {
        if (job.Spec.Kind != ExecutionJobKind.Export
            || job.Status is not (ExecutionJobStatus.Succeeded or ExecutionJobStatus.Failed or ExecutionJobStatus.Cancelled))
        {
            return;
        }

        try
        {
            await DeleteArtifactsAsync(job, cancellationToken).ConfigureAwait(false);
        }
        // Best effort cleanup must not block the job's terminal transition.
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ExportJobServiceLog.TerminalProgressFailed(logger, job.OperationId, ex);
        }
    }

    private async Task DeleteArtifactsAsync(ExecutionJobRecord job, CancellationToken cancellationToken)
    {
        if (scopeFactory is null)
        {
            return;
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var storage = scope.ServiceProvider.GetRequiredService<ICloudFileStorage>();
        var artifacts = job.CleanupArtifactReferences.Concat(job.ArtifactReferences).Distinct(StringComparer.Ordinal);
        if (job.Status == ExecutionJobStatus.Succeeded)
        {
            artifacts = artifacts.Except(job.ArtifactReferences, StringComparer.Ordinal);
        }
        foreach (var fileId in artifacts)
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
