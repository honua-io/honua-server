// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Infrastructure.Progress;

namespace Honua.Server.Features.Admin.TileOperations;

// Each attempt replaces only its own last snapshot. A successor's progress revokes every older writer.
internal sealed class TileCacheAttemptProgress(
    IUniversalProgressStore progressStore,
    IJobExecutionContext context)
{
    private static readonly TimeSpan Retention = TimeSpan.FromHours(24);
    private IOperationProgress? _expected;

    public async Task StartAsync(TileOperationProgress progress, CancellationToken cancellationToken)
    {
        while (true)
        {
            _expected = await progressStore.GetProgressAsync(progress.JobId, cancellationToken).ConfigureAwait(false);
            await context.ThrowIfExecutionLeaseLostAsync(cancellationToken).ConfigureAwait(false);
            // A manual retry retains the previous attempt's cancelled snapshot. The current
            // execution context checks durable cancellation and lease ownership before replacement.
            if (await TryWriteAsync(progress, cancellationToken).ConfigureAwait(false))
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(10), cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task WriteAsync(TileOperationProgress progress, CancellationToken cancellationToken)
    {
        await context.ThrowIfExecutionLeaseLostAsync(cancellationToken).ConfigureAwait(false);
        if (!await TryWriteAsync(progress, cancellationToken).ConfigureAwait(false))
        {
            throw new OperationCanceledException("The tile execution attempt no longer owns progress.", cancellationToken);
        }
    }

    private async Task<bool> TryWriteAsync(TileOperationProgress progress, CancellationToken cancellationToken)
    {
        var result = await progressStore.TryReplaceProgressAsync(
                new OperationProgressWrite(progress.JobId, progress, Retention), _expected, cancellationToken).ConfigureAwait(false);
        if (result.Outcome != ProgressCompareAndSetOutcome.Updated)
        {
            return false;
        }

        _expected = progress;
        return true;
    }
}
