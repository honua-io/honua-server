// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Diagnostics;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.Migration.Domain;

namespace Honua.Migration;

internal sealed partial class GeoservicesImportJobExecutor
{
    public async Task<JobExecutionResult?> RecoverOutcomeAsync(
        ExecutionJobRecord job, IJobExecutionContext context, CancellationToken cancellationToken)
    {
        var request = _jobManager.ReadRequest(job);
        if (request is null)
        {
            return null;
        }

        var licenseCancellation = _licensePolicy?.OperationCancellation ?? CancellationToken.None;
        using var recoveryCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, licenseCancellation);
        using var progress = new ImportJobProgressController<GeoservicesImportProgress>(job.OperationId,
            _jobManager.ProgressStore,
            ImportExecutionJobQueue<GeoservicesImportRequest, GeoservicesImportProgress>.Retention);
        var run = new ImportRun(job.OperationId, request with { JobId = job.OperationId }, progress,
            context, Stopwatch.StartNew(), licenseCancellation, recoveryCancellation.Token);
        return await RecoverReceiptAsync(run).ConfigureAwait(false);
    }

    private async Task<JobExecutionResult?> RecoverAsync(ImportRun run)
    {
        var recovered = await RecoverReceiptAsync(run).ConfigureAwait(false);
        if (recovered is not null)
        {
            return recovered;
        }

        var stored = run.Progress.CurrentProgress;
        return stored is not null && GeoservicesImportJobManager.IsTerminalStatus(stored.Status)
            ? ReplayTerminal(stored) : null;
    }

    private async Task<JobExecutionResult?> RecoverReceiptAsync(ImportRun run)
    {
        var stored = await _jobManager.ProgressStore.GetProgressAsync(run.JobId, run.Token).ConfigureAwait(false);
        run.Progress.Seed(stored);
        if (stored != null && GeoservicesImportJobManager.IsTerminalStatus(stored.Status) &&
            (stored.Status != GeoservicesImportStatus.Cancelled || stored.DataCommitted))
        {
            if (stored.DataCommitted)
            {
                await RecordCommitAsync(run).ConfigureAwait(false);
            }

            return ReplayTerminal(stored);
        }

        await using var scope = _scopeFactory.CreateAsyncScope();
        GeoservicesImportPlanning.RestoreTenant(scope.ServiceProvider, run.Request.Target);
        if (scope.ServiceProvider.GetRequiredService<IGeoservicesImportService>() is not IGeoservicesImportCommitRecovery recovery)
        {
            return null;
        }

        // Source validation and credentials cannot determine whether an earlier attempt committed.
        // Recovery faults must reach the retry policy without stamping the import as failed.
        var result = await recovery.RecoverCommittedImportAsync(run.Request, run.Token).ConfigureAwait(false);
        if (result is null)
        {
            return null;
        }

        run.Stopwatch.Stop();
        return await CompleteAsync(run, result).ConfigureAwait(false);
    }
}
