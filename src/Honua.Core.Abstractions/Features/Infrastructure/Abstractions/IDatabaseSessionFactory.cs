// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Data;

namespace Honua.Core.Features.Infrastructure.Abstractions;

/// <summary>
/// Factory that opens <see cref="IDatabaseSession"/> instances against the
/// configured backing database. Sessions OWN the underlying ADO.NET
/// connection — callers MUST <c>await using</c> the returned session to
/// release the connection back to the pool.
/// </summary>
/// <remarks>
/// This is the post-C3 replacement for the leaky
/// <c>IDatabaseConnectionProvider.OpenConnectionAsync</c> / <c>OpenTransactionAsync</c>
/// surface. See ADR 0046 for the migration plan and the call-site
/// triage that left some consumers on the legacy provider.
/// </remarks>
public interface IDatabaseSessionFactory
{
    /// <summary>
    /// Opens a new session bound to a freshly acquired connection.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>An open, non-transactional session.</returns>
    Task<IDatabaseSession> OpenAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Opens a new session and immediately begins a transaction in one call.
    /// </summary>
    /// <param name="isolationLevel">Transaction isolation level. Defaults to <see cref="IsolationLevel.RepeatableRead"/>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>An open, transactional session.</returns>
    Task<IDatabaseSession> OpenTransactionAsync(
        IsolationLevel isolationLevel = IsolationLevel.RepeatableRead,
        CancellationToken cancellationToken = default);
}
