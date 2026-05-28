// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Data;
using System.Data.Common;

namespace Honua.Core.Features.Infrastructure.Abstractions;

/// <summary>
/// Provides database connections.
/// </summary>
public interface IDatabaseConnectionProvider
{
    /// <summary>
    /// Gets a database connection string.
    /// </summary>
    /// <returns>The connection string</returns>
    string GetConnectionString();

    /// <summary>
    /// Opens a database connection asynchronously.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>An open database connection</returns>
    Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Opens a database connection with a transaction.
    /// </summary>
    /// <param name="isolationLevel">The isolation level</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Connection and transaction</returns>
    Task<(DbConnection Connection, DbTransaction Transaction)> OpenTransactionAsync(
        IsolationLevel isolationLevel = IsolationLevel.RepeatableRead,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Executes an operation with automatic deadlock retry.
    /// </summary>
    /// <typeparam name="T">Return type</typeparam>
    /// <param name="operation">The operation to execute</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The operation result</returns>
    Task<T> ExecuteWithDeadlockRetryAsync<T>(
        Func<Task<T>> operation,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Executes an operation with automatic deadlock retry.
    /// </summary>
    /// <param name="operation">The operation to execute</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Task representing the operation</returns>
    Task ExecuteWithDeadlockRetryAsync(
        Func<Task> operation,
        CancellationToken cancellationToken = default);
}
