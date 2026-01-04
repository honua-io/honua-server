// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Data;
using System.Data.Common;

namespace Honua.Core.Features.Infrastructure.Abstractions;

/// <summary>
/// Provides database connections with built-in resilience and reliability
/// </summary>
/// <remarks>
/// This abstraction hides infrastructure concerns (connection pooling, retry policies, etc.)
/// from domain logic while providing reliable database connectivity.
/// </remarks>
public interface IDatabaseConnectionProvider
{
    /// <summary>
    /// Opens a database connection with automatic retry for transient failures
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>An open database connection</returns>
    /// <remarks>
    /// The returned connection includes:
    /// - Automatic retry for transient connection errors
    /// - Connection pooling and resource management
    /// - Structured logging for retry attempts
    /// - Proper error handling and cancellation support
    /// </remarks>
    Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Opens a database connection and begins a transaction with the specified isolation level
    /// </summary>
    /// <param name="isolationLevel">The isolation level for the transaction</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>A tuple containing the open connection and active transaction</returns>
    /// <remarks>
    /// Recommended isolation levels:
    /// - <see cref="IsolationLevel.RepeatableRead"/> for most operations (default safe choice)
    /// - <see cref="IsolationLevel.Serializable"/> for critical business operations requiring full consistency
    /// - <see cref="IsolationLevel.ReadCommitted"/> for read-heavy operations where some phantom reads are acceptable
    ///
    /// The caller is responsible for properly disposing both the transaction and connection.
    /// </remarks>
    Task<(DbConnection Connection, DbTransaction Transaction)> OpenTransactionAsync(
        IsolationLevel isolationLevel = IsolationLevel.RepeatableRead,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Executes a database operation with deadlock retry policy
    /// </summary>
    /// <typeparam name="T">Return type of the operation</typeparam>
    /// <param name="operation">The database operation to execute</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Result of the database operation</returns>
    /// <remarks>
    /// This method wraps the operation in a retry policy that specifically handles PostgreSQL
    /// deadlock errors (40P01). Deadlocks are safe to retry as the entire transaction is rolled back.
    /// Uses exponential backoff: 100ms, 200ms, 400ms for up to 3 retry attempts.
    /// </remarks>
    Task<T> ExecuteWithDeadlockRetryAsync<T>(
        Func<Task<T>> operation,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Executes a database operation with deadlock retry policy (no return value)
    /// </summary>
    /// <param name="operation">The database operation to execute</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Task representing the completion of the operation</returns>
    /// <remarks>
    /// This method wraps the operation in a retry policy that specifically handles PostgreSQL
    /// deadlock errors (40P01). Deadlocks are safe to retry as the entire transaction is rolled back.
    /// Uses exponential backoff: 100ms, 200ms, 400ms for up to 3 retry attempts.
    /// </remarks>
    Task ExecuteWithDeadlockRetryAsync(
        Func<Task> operation,
        CancellationToken cancellationToken = default);
}
