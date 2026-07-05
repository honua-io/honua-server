// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Data.Common;

namespace Honua.Postgres.Features.Infrastructure;

/// <summary>
/// Extension methods for <see cref="DbTransaction"/> that guard against the
/// phantom-commit race: if a <see cref="CancellationToken"/> fires after the
/// server accepts COMMIT but before the network ACK arrives, the ADO.NET driver
/// throws <see cref="OperationCanceledException"/> yet the transaction is
/// durably committed server-side. Callers that observe the exception and retry
/// produce duplicate mutations.
/// </summary>
/// <remarks>
/// Always call <see cref="CommitSafelyAsync"/> instead of
/// <see cref="DbTransaction.CommitAsync(CancellationToken)"/> when a live
/// cancellation token is in scope. The Roslyn analyzer HN0001 (CommitAsyncWithLiveToken)
/// enforces this at build time.
/// </remarks>
internal static class DbTransactionExtensions
{
    /// <summary>
    /// Checks <paramref name="cancellationToken"/> before the COMMIT round-trip
    /// begins, then commits with <see cref="CancellationToken.None"/> so the
    /// in-flight COMMIT is never interrupted mid-flight.
    /// </summary>
    /// <param name="transaction">The transaction to commit.</param>
    /// <param name="cancellationToken">
    /// Token checked before committing. If it is already cancelled the method
    /// throws <see cref="OperationCanceledException"/> without touching the
    /// database. Once the commit begins it always runs to completion.
    /// </param>
    public static Task CommitSafelyAsync(this DbTransaction transaction, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return transaction.CommitAsync(CancellationToken.None);
    }
}
