// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Infrastructure.Abstractions;

/// <summary>
/// Provides database connection metadata and provider-level resilience helpers.
/// </summary>
/// <remarks>
/// Connection lifetime is owned by <see cref="IDatabaseSession"/> /
/// <see cref="IDatabaseSessionFactory"/> — this interface deliberately does NOT
/// expose ADO.NET connections or transactions (audit C3 / ADR 0046). Provider
/// assemblies that need the raw <c>System.Data.Common</c> escape hatch consume
/// <c>IAdoNetDatabaseConnectionProvider</c> from <c>Honua.Core</c> instead.
/// </remarks>
public interface IDatabaseConnectionProvider
{
    /// <summary>
    /// Gets a database connection string.
    /// </summary>
    /// <returns>The connection string</returns>
    string GetConnectionString();

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
