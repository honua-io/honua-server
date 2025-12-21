// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Npgsql;
using Polly;

namespace Honua.Postgres.Features.Infrastructure.Resilience;

/// <summary>
/// Resilience policies for database operations with PostgreSQL
/// </summary>
internal static class ResiliencePolicies
{
    /// <summary>
    /// Retry policy for transient connection errors ONLY.
    /// IMPORTANT: Only retry connection acquisition, not mid-transaction errors.
    /// Once a transaction starts, failures should propagate (transaction will rollback).
    /// Retrying after partial execution risks duplicate operations.
    /// </summary>
    /// <param name="onRetry">Optional callback for retry events (for logging)</param>
    /// <returns>Async retry policy for connection acquisition</returns>
    public static IAsyncPolicy GetConnectionRetryPolicy(Action<Exception, TimeSpan, int>? onRetry = null)
    {
        return Policy
            .Handle<NpgsqlException>(IsConnectionError)
            .Or<TimeoutException>()
            .WaitAndRetryAsync(
                retryCount: 3,
                sleepDurationProvider: attempt => TimeSpan.FromMilliseconds(100 * Math.Pow(2, attempt)),
                onRetry: (exception, timespan, attempt, context) => onRetry?.Invoke(exception, timespan, attempt));
    }

    /// <summary>
    /// Determines if a PostgreSQL exception represents a connection-level error
    /// that is safe to retry without risking duplicate operations
    /// </summary>
    /// <param name="ex">The Npgsql exception to evaluate</param>
    /// <returns>True if the error is a retryable connection error</returns>
    private static bool IsConnectionError(NpgsqlException ex)
    {
        // Only connection-level errors are safe to retry
        return ex.SqlState switch
        {
            "57P03" => true,  // cannot_connect_now
            "08000" => true,  // connection_exception
            "08003" => true,  // connection_does_not_exist
            "08006" => true,  // connection_failure
            _ => false
        };
        // NOTE: serialization_failure (40001) and deadlock_detected (40P01)
        // are NOT retried here - they require application-level retry with
        // fresh transaction, handled by the caller if needed.
    }
}
