// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Runtime.CompilerServices;
using Honua.Core.Features.Infrastructure.Resilience;
using Npgsql;
using Polly;

namespace Honua.Db.Postgres.Features.Infrastructure.Resilience;

internal static class ResiliencePolicies
{
    internal const string OnRetryCallbackKey = "OnRetryCallback";

    // Per-data-source policy caches. ConditionalWeakTable holds a weak reference
    // to the NpgsqlDataSource key, so policies are eligible for collection as
    // soon as their owning data source is disposed/GC'd. This matters for the
    // SecureConnectionDataSourceCache, which can churn data source instances
    // when secure connection registry entries are rotated.
    private static readonly ConditionalWeakTable<NpgsqlDataSource, IAsyncPolicy> _connectionPolicies = new();
    private static readonly ConditionalWeakTable<NpgsqlDataSource, IAsyncPolicy> _deadlockPolicies = new();

    public static ResiliencePolicyOptions DeadlockDefaults { get; } = new()
    {
        MaxRetryAttempts = 3,
        BaseDelay = TimeSpan.FromMilliseconds(100),
        BackoffExponent = 2.0,
        JitterPercentage = 0.25,
        CircuitBreakerFailures = 10,
        CircuitBreakDuration = TimeSpan.FromSeconds(15)
    };

    public static IAsyncPolicy GetConnectionRetryPolicy(NpgsqlDataSource dataSource)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        return _connectionPolicies.GetValue(dataSource, static _ => BuildConnectionRetryPolicy());
    }

    public static IAsyncPolicy GetDeadlockRetryPolicy(NpgsqlDataSource dataSource)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        return _deadlockPolicies.GetValue(dataSource, static _ => BuildDeadlockRetryPolicy());
    }

    public static Context CreateRetryContext(Action<Exception, TimeSpan, int>? onRetry = null)
    {
        var context = new Context();
        if (onRetry is not null)
        {
            context[OnRetryCallbackKey] = onRetry;
        }

        return context;
    }

    internal static bool IsConnectionError(NpgsqlException ex)
    {
        ArgumentNullException.ThrowIfNull(ex);

        if (!string.IsNullOrEmpty(ex.SqlState))
        {
            return IsConnectionErrorSqlState(ex.SqlState);
        }

        // Transport-level transient errors (e.g., RDS Proxy socket recycle or failover drop)
        // have no SQLSTATE. Exclude wrapped TimeoutException so slow query timeouts are not
        // misclassified as connection failures.
        return ex.IsTransient && ex.InnerException is not TimeoutException;
    }

    internal static bool IsConnectionErrorSqlState(string? sqlState)
    {
        // Only connection-level and failover/shutdown errors are safe to retry here.
        // serialization_failure (40001) and deadlock_detected (40P01) require
        // fresh-transaction retry via DeadlockRetryPolicy.
        return sqlState switch
        {
            "57P01" => true,  // admin_shutdown (Aurora/RDS failover)
            "57P02" => true,  // crash_shutdown
            "57P03" => true,  // cannot_connect_now
            "08000" => true,  // connection_exception
            "08001" => true,  // sqlclient_unable_to_establish_sqlconnection
            "08003" => true,  // connection_does_not_exist
            "08004" => true,  // sqlserver_rejected_establishment_of_sqlconnection
            "08006" => true,  // connection_failure
            "53300" => true,  // too_many_connections
            _ => false
        };
    }

    internal static bool IsDeadlockError(NpgsqlException ex)
    {
        ArgumentNullException.ThrowIfNull(ex);

        // PostgreSQL deadlock_detected (40P01) and serialization_failure (40001)
        // are retryable when the caller executes a fresh transaction.
        return ex.SqlState is "40P01" or "40001";
    }

    internal static IAsyncPolicy CreateFreshConnectionRetryPolicy() => BuildConnectionRetryPolicy();

    internal static IAsyncPolicy CreateFreshDeadlockRetryPolicy() => BuildDeadlockRetryPolicy();

    private static Polly.Wrap.AsyncPolicyWrap BuildConnectionRetryPolicy()
    {
        var builder = Policy
            .Handle<NpgsqlException>(IsConnectionError)
            .Or<TimeoutException>();

        return BuildPolicyWithContextCallback(builder, ResiliencePolicyOptions.Default);
    }

    private static Polly.Retry.AsyncRetryPolicy BuildDeadlockRetryPolicy()
    {
        // Localized deadlocks and serialization failures must never trip a
        // database-wide circuit breaker; retry with jittered exponential backoff only.
        var builder = Policy.Handle<NpgsqlException>(IsDeadlockError);
        return BuildRetryPolicyWithContextCallback(builder, DeadlockDefaults);
    }

    private static Polly.Retry.AsyncRetryPolicy BuildRetryPolicyWithContextCallback(PolicyBuilder builder, ResiliencePolicyOptions options)
    {
        return builder.WaitAndRetryAsync(
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
    }

    private static Polly.Wrap.AsyncPolicyWrap BuildPolicyWithContextCallback(PolicyBuilder builder, ResiliencePolicyOptions options)
    {
        var retryPolicy = BuildRetryPolicyWithContextCallback(builder, options);
        var circuitPolicy = builder.CircuitBreakerAsync(
            options.CircuitBreakerFailures,
            options.CircuitBreakDuration);

        return Policy.WrapAsync(retryPolicy, circuitPolicy);
    }
}
