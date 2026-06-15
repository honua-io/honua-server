// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Data;
using System.Data.Common;

namespace Honua.Core.Features.Infrastructure.Abstractions;

/// <summary>
/// Provider-internal escape hatch that exposes raw ADO.NET connections and
/// transactions on top of <see cref="IDatabaseConnectionProvider"/>.
/// </summary>
/// <remarks>
/// <para>
/// This interface intentionally lives in <c>Honua.Core</c> (NOT
/// <c>Honua.Core.Abstractions</c>) so the abstractions contract surface stays
/// free of <c>System.Data.Common</c> types (audit C3 / ADR 0046). It exists for
/// data-access code that genuinely needs the underlying <see cref="DbConnection"/> —
/// typed dialect parameters (e.g. <c>NpgsqlDbType</c>), prepared-statement caches,
/// binary <c>COPY</c> bulk loads, and side-channel listeners.
/// </para>
/// <para>
/// New code should prefer <see cref="IDatabaseSessionFactory"/> /
/// <see cref="IDatabaseSession"/>, which own the connection lifetime and do not
/// leak ADO.NET types.
/// </para>
/// </remarks>
public interface IAdoNetDatabaseConnectionProvider : IDatabaseConnectionProvider
{
    /// <summary>
    /// Opens a database connection asynchronously. The caller owns the
    /// connection and MUST dispose it (typically via <c>await using</c>).
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>An open database connection</returns>
    Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Opens a database connection with a transaction. The caller owns both and
    /// MUST dispose them, committing or rolling back the transaction explicitly.
    /// </summary>
    /// <param name="isolationLevel">The isolation level</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Connection and transaction</returns>
    Task<(DbConnection Connection, DbTransaction Transaction)> OpenTransactionAsync(
        IsolationLevel isolationLevel = IsolationLevel.RepeatableRead,
        CancellationToken cancellationToken = default);
}
