// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Resilience;
using Npgsql;
using Polly;

namespace Honua.Postgres.Features.Infrastructure.Resilience;

/// <summary>
/// Resilience policies for database operations with PostgreSQL
/// </summary>
internal static class ResiliencePolicies
{
    /// <summary>
    /// Retry + circuit breaker policy for transient connection errors ONLY.
    /// IMPORTANT: Only retry connection acquisition, not mid-transaction errors.
    /// Once a transaction starts, failures should propagate (transaction will rollback).
    /// Retrying after partial execution risks duplicate operations.
    /// </summary>
    /// <param name="onRetry">Optional callback for retry events (for logging)</param>
    /// <returns>Async retry policy for connection acquisition</returns>
    public static IAsyncPolicy GetConnectionRetryPolicy(Action<Exception, TimeSpan, int>? onRetry = null)
    {
        var builder = Policy
            .Handle<NpgsqlException>(IsConnectionError)
            .Or<TimeoutException>();

        return ResiliencePolicyFactory.CreateStandardPolicy(
            builder,
            ResiliencePolicyOptions.Default,
            onRetry: onRetry);
    }

    /// <summary>
    /// Retry policy for deadlock errors ONLY.
    /// Safe to retry deadlock errors with fresh transaction, as the entire transaction was rolled back.
    /// Uses exponential backoff: 100ms, 200ms, 400ms for 3 retry attempts.
    /// </summary>
    /// <param name="onRetry">Optional callback for retry events (for logging)</param>
    /// <returns>Async retry policy for deadlock detection</returns>
    public static IAsyncPolicy GetDeadlockRetryPolicy(Action<Exception, TimeSpan, int>? onRetry = null)
    {
        var builder = Policy.Handle<NpgsqlException>(IsDeadlockError);

        var options = new ResiliencePolicyOptions
        {
            MaxRetryAttempts = 3,
            BaseDelay = TimeSpan.FromMilliseconds(100),
            BackoffExponent = 2.0,
            CircuitBreakerFailures = 10, // Higher threshold for deadlocks
            CircuitBreakDuration = TimeSpan.FromSeconds(15) // Shorter circuit break duration
        };

        return ResiliencePolicyFactory.CreateStandardPolicy(
            builder,
            options,
            onRetry: onRetry);
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
        // NOTE: serialization_failure (40001) is NOT retried here as it requires
        // application-level retry with fresh transaction.
        // deadlock_detected (40P01) is handled by GetDeadlockRetryPolicy().
    }

    /// <summary>
    /// Determines if a PostgreSQL exception represents a deadlock error
    /// that is safe to retry with a fresh transaction
    /// </summary>
    /// <param name="ex">The Npgsql exception to evaluate</param>
    /// <returns>True if the error is a deadlock that can be retried</returns>
    private static bool IsDeadlockError(NpgsqlException ex)
    {
        // PostgreSQL deadlock_detected error code
        return ex.SqlState == "40P01";
    }
}
