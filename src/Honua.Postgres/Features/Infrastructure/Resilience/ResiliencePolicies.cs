// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Resilience;
using Npgsql;
using Polly;

namespace Honua.Postgres.Features.Infrastructure.Resilience;

/// <summary>
/// Cached resilience policies for database operations with PostgreSQL.
/// Policies are created once and reused across all requests. The onRetry callback
/// is threaded via <see cref="Polly.Context"/> at execution time, allowing a single
/// cached policy to serve callers with different logging needs.
/// </summary>
internal static class ResiliencePolicies
{
    /// <summary>
    /// Key used to store the <see cref="Action{Exception, TimeSpan, Int32}"/> retry callback
    /// in the <see cref="Polly.Context"/> dictionary.
    /// </summary>
    internal const string OnRetryCallbackKey = "OnRetryCallback";

    /// <summary>
    /// Cached retry + circuit breaker policy for transient connection errors.
    /// The circuit breaker is shared across all callers, so it correctly accumulates
    /// failures and trips when the database is genuinely unavailable.
    /// </summary>
    public static IAsyncPolicy ConnectionRetryPolicy { get; } = BuildConnectionRetryPolicy();

    /// <summary>
    /// Default deadlock resilience options.
    /// Higher circuit breaker threshold and shorter break duration than the standard policy,
    /// since deadlocks are safe to retry after full rollback.
    /// </summary>
    public static ResiliencePolicyOptions DeadlockDefaults { get; } = new()
    {
        MaxRetryAttempts = 3,
        BaseDelay = TimeSpan.FromMilliseconds(100),
        BackoffExponent = 2.0,
        CircuitBreakerFailures = 10,
        CircuitBreakDuration = TimeSpan.FromSeconds(15)
    };

    /// <summary>
    /// Cached retry + circuit breaker policy for deadlock errors.
    /// Safe to retry deadlock errors with fresh transaction, as the entire transaction was rolled back.
    /// Uses exponential backoff: 100ms, 200ms, 400ms for 3 retry attempts.
    /// </summary>
    public static IAsyncPolicy DeadlockRetryPolicy { get; } = BuildDeadlockRetryPolicy();

    /// <summary>
    /// Creates a <see cref="Context"/> that carries the optional retry callback.
    /// Pass this to <c>policy.ExecuteAsync(func, context)</c>.
    /// </summary>
    public static Context CreateRetryContext(Action<Exception, TimeSpan, int>? onRetry = null)
    {
        var context = new Context();
        if (onRetry is not null)
        {
            context[OnRetryCallbackKey] = onRetry;
        }

        return context;
    }

    /// <summary>
    /// Determines if a PostgreSQL exception represents a connection-level error
    /// that is safe to retry without risking duplicate operations.
    /// </summary>
    internal static bool IsConnectionError(NpgsqlException ex)
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
        // deadlock_detected (40P01) is handled by DeadlockRetryPolicy.
    }

    /// <summary>
    /// Determines if a PostgreSQL exception represents a deadlock error
    /// that is safe to retry with a fresh transaction.
    /// </summary>
    internal static bool IsDeadlockError(NpgsqlException ex)
    {
        // PostgreSQL deadlock_detected error code
        return ex.SqlState == "40P01";
    }

    /// <summary>
    /// Creates a fresh connection retry policy instance. Use only for testing;
    /// production code should use the cached <see cref="ConnectionRetryPolicy"/>.
    /// </summary>
    internal static IAsyncPolicy CreateFreshConnectionRetryPolicy() => BuildConnectionRetryPolicy();

    private static IAsyncPolicy BuildConnectionRetryPolicy()
    {
        var builder = Policy
            .Handle<NpgsqlException>(IsConnectionError)
            .Or<TimeoutException>();

        return BuildPolicyWithContextCallback(builder, ResiliencePolicyOptions.Default);
    }

    private static IAsyncPolicy BuildDeadlockRetryPolicy()
    {
        var builder = Policy.Handle<NpgsqlException>(IsDeadlockError);
        return BuildPolicyWithContextCallback(builder, DeadlockDefaults);
    }

    private static IAsyncPolicy BuildPolicyWithContextCallback(PolicyBuilder builder, ResiliencePolicyOptions options)
    {
        var retryPolicy = builder.WaitAndRetryAsync(
            options.MaxRetryAttempts,
            attempt => options.GetDelay(attempt),
            (exception, delay, attempt, context) =>
            {
                if (context.TryGetValue(OnRetryCallbackKey, out var obj) &&
                    obj is Action<Exception, TimeSpan, int> callback)
                {
                    callback(exception, delay, attempt);
                }
            });

        var circuitPolicy = builder.CircuitBreakerAsync(
            options.CircuitBreakerFailures,
            options.CircuitBreakDuration);

        return Policy.WrapAsync(retryPolicy, circuitPolicy);
    }
}
