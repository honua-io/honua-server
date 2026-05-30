// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Data;

namespace Honua.Core.Features.Infrastructure.Abstractions;

/// <summary>
/// Represents a connection-scoped unit of database work. The session OWNS the
/// underlying ADO.NET connection (and, when transactional, the transaction) so
/// callers never see <see cref="System.Data.Common.DbConnection"/> or
/// <see cref="System.Data.Common.DbTransaction"/> on the public surface.
/// </summary>
/// <remarks>
/// <para>
/// <b>Lifetime contract</b>: every session MUST be released via
/// <see cref="IAsyncDisposable.DisposeAsync"/> (typically using
/// <c>await using</c>). Disposing the session releases the underlying
/// connection back to the pool. For a transactional session, disposal
/// without an explicit <see cref="CommitAsync"/> rolls the transaction
/// back.
/// </para>
/// <para>
/// This abstraction replaces direct exposure of
/// <c>IDatabaseConnectionProvider.OpenConnectionAsync</c> /
/// <c>OpenTransactionAsync</c> (see audit C3 / ADR 0046) so that
/// abstractions assemblies do not leak ADO.NET types into the
/// protocol/feature contract surface.
/// </para>
/// </remarks>
public interface IDatabaseSession : IAsyncDisposable
{
    /// <summary>
    /// Gets the connection string of the underlying database session.
    /// </summary>
    /// <remarks>
    /// Provided for the rare legacy call sites that need to spawn an
    /// ancillary connection (e.g. a side-channel listener). New code
    /// should prefer composing through <see cref="IDatabaseSessionFactory"/>
    /// instead of dialling a raw connection from the connection string.
    /// </remarks>
    string ConnectionString { get; }

    /// <summary>
    /// Indicates whether this session has an active transaction.
    /// </summary>
    bool IsTransactional { get; }

    /// <summary>
    /// Executes a non-query SQL statement (INSERT/UPDATE/DELETE/DDL).
    /// </summary>
    /// <param name="sql">The SQL command text. Parameters are referenced by name (e.g. <c>@id</c>).</param>
    /// <param name="parameters">
    /// Optional parameter object. Supported shapes:
    /// <list type="bullet">
    ///   <item><description><c>null</c> (no parameters)</description></item>
    ///   <item><description><c>IReadOnlyDictionary&lt;string, object?&gt;</c> — name → value</description></item>
    ///   <item><description>Anonymous object — property names become parameter names</description></item>
    /// </list>
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Number of rows affected.</returns>
    Task<int> ExecuteAsync(string sql, object? parameters = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Executes a query and materialises a single scalar/record, or <c>default</c> if no row returned.
    /// </summary>
    /// <typeparam name="T">Scalar result type (e.g. <c>int</c>, <c>string</c>, <c>DateTimeOffset</c>).</typeparam>
    /// <param name="sql">The SQL command text.</param>
    /// <param name="parameters">Optional parameter object (see <see cref="ExecuteAsync"/>).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The scalar value of the first column of the first row, or <c>default</c>.</returns>
    Task<T?> QuerySingleOrDefaultAsync<T>(string sql, object? parameters = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Streams the results of a query, materialising each row's first column to <typeparamref name="T"/>.
    /// </summary>
    /// <typeparam name="T">Scalar result type.</typeparam>
    /// <param name="sql">The SQL command text.</param>
    /// <param name="parameters">Optional parameter object (see <see cref="ExecuteAsync"/>).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>An async stream of scalar values.</returns>
    IAsyncEnumerable<T> QueryAsync<T>(string sql, object? parameters = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Begins a new transactional session nested on top of this session's connection.
    /// </summary>
    /// <param name="isolationLevel">Transaction isolation level. Defaults to <see cref="IsolationLevel.RepeatableRead"/>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// A transactional session. Dispose without <see cref="CommitAsync"/> to roll back; call
    /// <see cref="CommitAsync"/> then dispose to commit.
    /// </returns>
    Task<IDatabaseSession> BeginTransactionAsync(
        IsolationLevel isolationLevel = IsolationLevel.RepeatableRead,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Commits the current transaction. Only valid when <see cref="IsTransactional"/> is true.
    /// After commit, the session must still be disposed to release the connection.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task CommitAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Rolls back the current transaction explicitly. Only valid when <see cref="IsTransactional"/> is true.
    /// Disposing the session without a prior <see cref="CommitAsync"/> performs an implicit rollback.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task RollbackAsync(CancellationToken cancellationToken = default);
}
