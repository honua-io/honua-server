// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Migration.Abstractions;

namespace Honua.Migration;

// All worker writes must participate in the same conditional transitions as
// cancellation. A separate read before an unconditional write loses cancellation.
internal sealed class GeoServerImportProgressStore(IUniversalProgressStore store)
    : IDistributedProgressStore<GeoServerImportProgress>, IProgressStoreRecovery
{
    public Task ProbeRecoveryAsync(CancellationToken cancellationToken = default)
        => store is IProgressStoreRecovery recovery
            ? recovery.ProbeRecoveryAsync(cancellationToken)
            : Task.CompletedTask;

    public async Task SetProgressAsync(string jobId, GeoServerImportProgress progress,
        TimeSpan? ttl = null, CancellationToken cancellationToken = default)
    {
        var current = await store.GetProgressAsync(jobId, cancellationToken).ConfigureAwait(false);
        if (current == null)
        {
            // New records start queued. Late active or terminal reports cannot recreate one.
            if (progress.Status == GeoServerImportStatus.Queued)
            {
                await store.SetProgressAsync(jobId, progress, ttl, cancellationToken).ConfigureAwait(false);
            }
            return;
        }

        while (current.Status != OperationStatus.Cancelled)
        {
            var result = await store.TrySetProgressAsync(jobId, progress, current.Status, ttl, cancellationToken).ConfigureAwait(false);
            if (result.Outcome != ProgressCompareAndSetOutcome.StatusMismatch || result.CurrentProgress == null)
            {
                return;
            }
            current = result.CurrentProgress;
        }
    }

    public Task<GeoServerImportProgress?> GetProgressAsync(string jobId, CancellationToken cancellationToken = default)
        => store.GetProgressAsync<GeoServerImportProgress>(jobId, cancellationToken);

    public Task DeleteProgressAsync(string jobId, CancellationToken cancellationToken = default)
        => store.DeleteProgressAsync(jobId, cancellationToken);

    public async Task<IReadOnlyList<string>> GetActiveJobIdsAsync(CancellationToken cancellationToken = default)
    {
        var operations = await store.GetActiveOperationsAsync<GeoServerImportProgress>(
            OperationType.ExternalImport, cancellationToken).ConfigureAwait(false);
        return operations.Select(p => p.JobId).ToArray();
    }
}
