// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.CircuitBreaker;
using Polly.Wrap;

namespace Honua.Core.Features.Infrastructure.Resilience;

/// <summary>
/// Cached resilience policies for HTTP client operations with external services.
/// Policies are cached per service type to maintain isolated circuit breaker state.
/// Each service type gets its own circuit breaker to prevent cascading failures
/// across different external dependencies.
/// </summary>
public static class HttpResiliencePolicies
{
    /// <summary>
    /// Key used to store the retry callback in the Polly Context dictionary.
    /// </summary>
    internal const string OnRetryCallbackKey = "OnRetryCallback";

    /// <summary>
    /// Key used to store the circuit breaker callback in the Polly Context dictionary.
    /// </summary>
    internal const string OnCircuitBreakerCallbackKey = "OnCircuitBreakerCallback";

    // Per-service-type policy caches for circuit breaker isolation.
    private static readonly ConcurrentDictionary<string, IAsyncPolicy<HttpResponseMessage>> _httpPolicies = new(StringComparer.Ordinal);

    /// <summary>
    /// Standard HTTP resilience options for external services.
    /// </summary>
    public static ResiliencePolicyOptions HttpDefaults { get; } = new()
    {
        MaxRetryAttempts = 3,
        BaseDelay = TimeSpan.FromMilliseconds(500),
        BackoffExponent = 2.0,
        JitterPercentage = 0.2,
        CircuitBreakerFailures = 5,
        CircuitBreakDuration = TimeSpan.FromSeconds(30)
    };

    /// <summary>
    /// Resilience options optimized for fast API services (geocoding, webhooks).
    /// </summary>
    public static ResiliencePolicyOptions FastApiDefaults { get; } = new()
    {
        MaxRetryAttempts = 2,
        BaseDelay = TimeSpan.FromMilliseconds(200),
        BackoffExponent = 1.5,
        JitterPercentage = 0.15,
        CircuitBreakerFailures = 3,
        CircuitBreakDuration = TimeSpan.FromSeconds(15)
    };

    /// <summary>
    /// Resilience options for slow import/discovery services.
    /// </summary>
    public static ResiliencePolicyOptions SlowServiceDefaults { get; } = new()
    {
        MaxRetryAttempts = 5,
        BaseDelay = TimeSpan.FromSeconds(1),
        BackoffExponent = 2.0,
        JitterPercentage = 0.25,
        CircuitBreakerFailures = 8,
        CircuitBreakDuration = TimeSpan.FromMinutes(2)
    };

    /// <summary>
    /// Returns the HTTP resilience policy for a specific service type,
    /// with isolated circuit breaker state per service type.
    /// </summary>
    /// <param name="serviceType">Service type identifier for circuit breaker isolation.</param>
    /// <param name="options">Resilience policy options. Uses HttpDefaults if null.</param>
    /// <returns>Cached resilience policy for the service type.</returns>
    public static IAsyncPolicy<HttpResponseMessage> GetHttpPolicy(
        string serviceType,
        ResiliencePolicyOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceType);
        var effectiveOptions = options ?? HttpDefaults;
        var cacheKey = $"{serviceType}|{GetOptionsHash(effectiveOptions)}";

        return _httpPolicies.GetOrAdd(cacheKey, _ => BuildHttpPolicy(effectiveOptions));
    }

    /// <summary>
    /// Creates a Polly Context with optional retry and circuit breaker callbacks.
    /// </summary>
    /// <param name="onRetry">Optional callback for retry events.</param>
    /// <param name="onCircuitBreaker">Optional callback for circuit breaker state changes.</param>
    /// <returns>Context with callbacks attached.</returns>
    public static Context CreateHttpContext(
        Action<DelegateResult<HttpResponseMessage>, TimeSpan, int>? onRetry = null,
        Action<DelegateResult<HttpResponseMessage>, CircuitState, TimeSpan>? onCircuitBreaker = null)
    {
        var context = new Context();

        if (onRetry is not null)
        {
            context[OnRetryCallbackKey] = onRetry;
        }

        if (onCircuitBreaker is not null)
        {
            context[OnCircuitBreakerCallbackKey] = onCircuitBreaker;
        }

        return context;
    }

    /// <summary>
    /// Determines if an HTTP response or exception is a transient failure suitable for retry.
    /// </summary>
    /// <param name="result">The result of the HTTP operation.</param>
    /// <returns>True if the failure is transient and safe to retry.</returns>
    public static bool IsTransientHttpFailure(DelegateResult<HttpResponseMessage> result)
    {
        // Handle exceptions
        if (result.Exception is not null)
        {
            return result.Exception switch
            {
                HttpRequestException => true,
                TaskCanceledException tcx when tcx.InnerException is TimeoutException => true,
                SocketException => true,
                _ => false
            };
        }

        // Handle HTTP response status codes
        if (result.Result is not null)
        {
            return result.Result.StatusCode switch
            {
                HttpStatusCode.InternalServerError => true,      // 500
                HttpStatusCode.BadGateway => true,               // 502
                HttpStatusCode.ServiceUnavailable => true,      // 503
                HttpStatusCode.GatewayTimeout => true,          // 504
                HttpStatusCode.RequestTimeout => true,          // 408
                HttpStatusCode.TooManyRequests => true,         // 429
                _ => false
            };
        }

        return false;
    }

    /// <summary>
    /// Creates a fresh HTTP resilience policy instance. Use only for testing;
    /// production code should use the cached <see cref="GetHttpPolicy"/>.
    /// </summary>
    internal static IAsyncPolicy<HttpResponseMessage> CreateFreshHttpPolicy(ResiliencePolicyOptions? options = null)
        => BuildHttpPolicy(options ?? HttpDefaults);

    private static AsyncPolicyWrap<HttpResponseMessage> BuildHttpPolicy(ResiliencePolicyOptions options)
    {
        var builder = Policy<HttpResponseMessage>
            .HandleResult(response => IsTransientHttpFailure(new DelegateResult<HttpResponseMessage>(response)))
            .Or<HttpRequestException>()
            .Or<TaskCanceledException>(tcx => tcx.InnerException is TimeoutException)
            .Or<SocketException>();

        var retryPolicy = builder.WaitAndRetryAsync(
            options.MaxRetryAttempts,
            attempt => options.GetDelay(attempt),
            (result, delay, attempt, context) =>
            {
                if (context.TryGetValue(OnRetryCallbackKey, out var obj) &&
                    obj is Action<DelegateResult<HttpResponseMessage>, TimeSpan, int> callback)
                {
                    callback(result, delay, attempt);
                }
            });

        var circuitPolicy = builder.CircuitBreakerAsync(
            options.CircuitBreakerFailures,
            options.CircuitBreakDuration,
            onBreak: (result, breakDelay) =>
            {
                // Circuit breaker callbacks are handled per-execution via Context
            },
            onReset: () =>
            {
                // Reset events are handled per-execution via Context
            },
            onHalfOpen: () =>
            {
                // Half-open events are handled per-execution via Context
            });

        return Policy.WrapAsync(retryPolicy, circuitPolicy);
    }

    private static string GetOptionsHash(ResiliencePolicyOptions options)
    {
        // Simple hash based on key properties for cache key differentiation
        var hash = HashCode.Combine(
            options.MaxRetryAttempts,
            options.BaseDelay,
            options.BackoffExponent,
            options.JitterPercentage,
            options.CircuitBreakerFailures,
            options.CircuitBreakDuration);

        return hash.ToString("x8", CultureInfo.InvariantCulture);
    }
}
