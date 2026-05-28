// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Share.Abstractions;
using Honua.Core.Features.Share.Domain;
using Honua.Server.Features.ControlPlane;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Features.Admin.Share;

/// <summary>
/// Reconciles a Share export run with its backing execution job when that job reaches a terminal
/// state, so runner-backed runs progress past <see cref="ShareExportRunStatus.Queued"/> to the
/// mapped terminal status (e.g. when a job is cancelled via the Console jobs API). Mirrors the
/// <c>GeoprocessingJobTerminalCallback</c> pattern: best-effort, kind-guarded, and resolves the
/// Share store from a fresh scope because the store is scoped under the PostgreSQL provider while
/// this callback is a singleton fired outside any request scope (from worker terminal transitions
/// and from API-side operator cancel through the Console jobs API).
/// </summary>
internal sealed class ShareExportJobTerminalCallback(
    IServiceScopeFactory serviceScopeFactory,
    TimeProvider timeProvider,
    ILogger<ShareExportJobTerminalCallback> logger) : IJobTerminalCallback
{
    public async ValueTask OnTerminalAsync(ExecutionJobRecord job, CancellationToken cancellationToken)
    {
        if (job.Spec.Kind != ExecutionJobKind.ShareExport)
        {
            return;
        }

        var status = MapStatus(job.Status);
        if (status is null)
        {
            return;
        }

        if (!job.Spec.Parameters.TryGetValue(ExecutionJobParameterKeys.ShareExportId, out var exportId)
            || !job.Spec.Parameters.TryGetValue(ExecutionJobParameterKeys.ShareRunId, out var runId)
            || string.IsNullOrWhiteSpace(exportId)
            || string.IsNullOrWhiteSpace(runId))
        {
            return;
        }

        try
        {
            using var scope = serviceScopeFactory.CreateScope();
            var store = scope.ServiceProvider.GetService<IShareExportStore>();
            if (store is null)
            {
                return;
            }

            var existing = await store.GetRunAsync(exportId, runId, cancellationToken).ConfigureAwait(false);
            if (existing is null
                || existing.Status is ShareExportRunStatus.Succeeded
                    or ShareExportRunStatus.Failed
                    or ShareExportRunStatus.Cancelled)
            {
                // No matching run, or it already reached a terminal state we must not overwrite.
                return;
            }

            var reconciled = existing with
            {
                Status = status.Value,
                StartedAt = existing.StartedAt ?? job.ClaimedAt,
                CompletedAt = job.CompletedAt ?? timeProvider.GetUtcNow(),
                // Carry worker-published artifacts from the backing job into run history so Console's
                // run detail matches the Operate job. The job record is the authoritative artifact
                // source at terminal time (failed jobs may still publish diagnostics), so copy them
                // whenever present and keep any already on the run only when the job published none.
                ResultArtifacts = job.ArtifactReferences.Count > 0
                    ? job.ArtifactReferences
                    : existing.ResultArtifacts,
                LastError = status.Value == ShareExportRunStatus.Failed
                    ? job.ErrorMessage ?? existing.LastError
                    : existing.LastError
            };

            await store.UpdateRunAsync(reconciled, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Best-effort: a failed reconcile must not block the job's terminal transition. The run
            // stays at its prior status until a later terminal notification succeeds.
            ShareAdminLog.ExportRunReconcileFailed(logger, runId, exportId, status.Value, job.OperationId, ex);
        }
    }

    private static ShareExportRunStatus? MapStatus(ExecutionJobStatus status)
        => status switch
        {
            ExecutionJobStatus.Succeeded => ShareExportRunStatus.Succeeded,
            ExecutionJobStatus.Failed => ShareExportRunStatus.Failed,
            ExecutionJobStatus.Cancelled => ShareExportRunStatus.Cancelled,
            _ => null
        };
}
