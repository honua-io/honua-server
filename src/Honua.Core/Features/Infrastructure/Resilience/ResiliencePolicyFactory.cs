// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Polly;

namespace Honua.Core.Features.Infrastructure.Resilience;

/// <summary>
/// Factory helpers to build consistent Polly retry/circuit policies across dependencies.
/// </summary>
public static class ResiliencePolicyFactory
{
    /// <inheritdoc/>
    public static IAsyncPolicy CreateStandardPolicy(
        PolicyBuilder builder,
        ResiliencePolicyOptions? options = null,
        Action<Exception, TimeSpan, int>? onRetry = null,
        Action<Exception, TimeSpan>? onBreak = null,
        Action? onReset = null)
    {
        var effective = options ?? ResiliencePolicyOptions.Default;

        var retryPolicy = builder.WaitAndRetryAsync(
            effective.MaxRetryAttempts,
            attempt => effective.GetDelay(attempt),
            (exception, delay, attempt, _) => onRetry?.Invoke(exception, delay, attempt));

        var resetHandler = onReset ?? (() => { });
        var circuitPolicy = builder.CircuitBreakerAsync(
            effective.CircuitBreakerFailures,
            effective.CircuitBreakDuration,
            (exception, breakDelay) => onBreak?.Invoke(exception, breakDelay),
            resetHandler);

        return Policy.WrapAsync(circuitPolicy, retryPolicy);
    }

    /// <inheritdoc/>
    public static IAsyncPolicy<TResult> CreateStandardPolicy<TResult>(
        PolicyBuilder<TResult> builder,
        ResiliencePolicyOptions? options = null,
        Action<DelegateResult<TResult>, TimeSpan, int>? onRetry = null,
        Action<DelegateResult<TResult>, TimeSpan>? onBreak = null,
        Action? onReset = null)
    {
        var effective = options ?? ResiliencePolicyOptions.Default;

        var retryPolicy = builder.WaitAndRetryAsync(
            effective.MaxRetryAttempts,
            attempt => effective.GetDelay(attempt),
            (result, delay, attempt, _) => onRetry?.Invoke(result, delay, attempt));

        var resetHandler = onReset ?? (() => { });
        var circuitPolicy = builder.CircuitBreakerAsync(
            effective.CircuitBreakerFailures,
            effective.CircuitBreakDuration,
            (result, breakDelay) => onBreak?.Invoke(result, breakDelay),
            resetHandler);

        return Policy.WrapAsync(circuitPolicy, retryPolicy);
    }
}
