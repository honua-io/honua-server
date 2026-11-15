// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Migration.Abstractions;

namespace Honua.Migration;

internal sealed partial class GeoservicesImportJobExecutor
{
    private async Task<JobExecutionResult?> RecoverAsync(ImportRun run)
    {
        var stored = await _jobManager.ProgressStore.GetProgressAsync(run.JobId, run.Token).ConfigureAwait(false);
        run.Progress.Seed(stored);
        if (stored != null && GeoservicesImportJobManager.IsTerminalStatus(stored.Status))
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
