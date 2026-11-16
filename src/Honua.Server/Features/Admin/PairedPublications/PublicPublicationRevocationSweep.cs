// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Metadata.Abstractions;

namespace Honua.Server.Features.Admin.PairedPublications;

internal sealed partial class PublicPublicationRevocationSweep(
    IPublicPublicationJournal journal, PublicPublicationRevoker revoker, ILogger<PublicPublicationRevocationSweep> logger)
{
    internal async Task SweepAsync(CancellationToken cancellationToken)
    {
        var outstanding = await journal.ReadOutstandingAsync(cancellationToken).ConfigureAwait(false);
        foreach (var generation in outstanding.Where(g => g.State is PublicPublicationState.Pending or PublicPublicationState.Revoking))
        {
            try
            {
                var key = generation.Reservation.Key;
                if (generation.State == PublicPublicationState.Pending &&
                    !await RequestAbandonedCopyCleanupAsync(key, cancellationToken).ConfigureAwait(false))
                {
                    continue;
                }
                await revoker.RevokeAsync(key, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                Log.CleanupFailed(logger, generation.Reservation.Key.PublicationId + "/" + generation.Reservation.Key.Version, exception);
            }
        }
    }

    private async Task<bool> RequestAbandonedCopyCleanupAsync(PublicPublicationKey key, CancellationToken cancellationToken)
    {
        // The writer holds this fence throughout uploads and promotion. Re-read after taking it:
        // a completed concurrent writer must keep its ready generation.
        await using var session = await journal.OpenAsync(key, cancellationToken).ConfigureAwait(false);
        var current = await session.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (current?.State != PublicPublicationState.Pending)
        {
            return current?.State == PublicPublicationState.Revoking;
        }
        await session.BeginRevocationAsync(cancellationToken).ConfigureAwait(false);
        await session.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    private static partial class Log
    {
        [LoggerMessage(EventId = 9257, Level = LogLevel.Warning,
            Message = "Public publication {Generation} cleanup remains outstanding; retrying next sweep.")]
        internal static partial void CleanupFailed(ILogger logger, string generation, Exception exception);
    }
}
