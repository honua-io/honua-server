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

    internal static bool IsDeadlockError(NpgsqlException ex)
    {
        // PostgreSQL deadlock_detected error code
        return ex.SqlState == "40P01";
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

    private static Polly.Wrap.AsyncPolicyWrap BuildDeadlockRetryPolicy()
    {
        var builder = Policy.Handle<NpgsqlException>(IsDeadlockError);
        return BuildPolicyWithContextCallback(builder, DeadlockDefaults);
    }

    private static Polly.Wrap.AsyncPolicyWrap BuildPolicyWithContextCallback(PolicyBuilder builder, ResiliencePolicyOptions options)
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
