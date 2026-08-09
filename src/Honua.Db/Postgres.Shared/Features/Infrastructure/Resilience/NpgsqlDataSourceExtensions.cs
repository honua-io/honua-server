// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Npgsql;
using Polly;

namespace Honua.Postgres.Features.Infrastructure.Resilience;

/// <summary>
/// Extension methods for NpgsqlDataSource to add resilience policies
/// </summary>
internal static class NpgsqlDataSourceExtensions
{
    /// <summary>
    /// Opens a connection with the per-data-source retry policy for transient
    /// connection errors. The optional <paramref name="onRetry"/> callback is
    /// threaded via <see cref="Polly.Context"/> so the cached policy can serve
    /// callers with different logging needs. Each data source has its own
    /// circuit breaker so a failure on one source cannot starve healthy ones.
    /// </summary>
    /// <param name="dataSource">The Npgsql data source</param>
    /// <param name="onRetry">Optional callback for retry events (for logging)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Open database connection</returns>
    public static async Task<NpgsqlConnection> OpenConnectionWithRetryAsync(
        this NpgsqlDataSource dataSource,
        Action<Exception, TimeSpan, int>? onRetry = null,
        CancellationToken cancellationToken = default)
    {
        var context = ResiliencePolicies.CreateRetryContext(onRetry);

        return await ResiliencePolicies.GetConnectionRetryPolicy(dataSource).ExecuteAsync(
            async (_) => await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false),
            context).ConfigureAwait(false);
    }

    /// <summary>
    /// Executes a database operation with the per-data-source deadlock retry policy.
    /// </summary>
    /// <typeparam name="T">Return type of the operation</typeparam>
    /// <param name="dataSource">The Npgsql data source</param>
    /// <param name="operation">The database operation to execute</param>
    /// <param name="onRetry">Optional callback for retry events (for logging)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Result of the database operation</returns>
    public static async Task<T> ExecuteWithDeadlockRetryAsync<T>(
        this NpgsqlDataSource dataSource,
        Func<Task<T>> operation,
        Action<Exception, TimeSpan, int>? onRetry = null,
        CancellationToken cancellationToken = default)
    {
        var context = ResiliencePolicies.CreateRetryContext(onRetry);

        return await ResiliencePolicies.GetDeadlockRetryPolicy(dataSource).ExecuteAsync(
            async (_) => await operation().ConfigureAwait(false),
            context).ConfigureAwait(false);
    }

    /// <summary>
    /// Executes a database operation with the per-data-source deadlock retry policy (no return value).
    /// </summary>
    /// <param name="dataSource">The Npgsql data source</param>
    /// <param name="operation">The database operation to execute</param>
    /// <param name="onRetry">Optional callback for retry events (for logging)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Task representing the completion of the operation</returns>
    public static async Task ExecuteWithDeadlockRetryAsync(
        this NpgsqlDataSource dataSource,
        Func<Task> operation,
        Action<Exception, TimeSpan, int>? onRetry = null,
        CancellationToken cancellationToken = default)
    {
        var context = ResiliencePolicies.CreateRetryContext(onRetry);

        await ResiliencePolicies.GetDeadlockRetryPolicy(dataSource).ExecuteAsync(
            async (_) => await operation().ConfigureAwait(false),
            context).ConfigureAwait(false);
    }
}
