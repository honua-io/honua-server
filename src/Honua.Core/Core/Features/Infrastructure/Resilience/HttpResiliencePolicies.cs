// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.CircuitBreaker;
using Polly.Wrap;

namespace Honua.Core.Features.Infrastructure.Resilience;

public static class HttpResiliencePolicies
{
    internal const string OnRetryCallbackKey = "OnRetryCallback";

    internal const string OnCircuitBreakerCallbackKey = "OnCircuitBreakerCallback";

    // Per-service-type policy caches for circuit breaker isolation.
    private static readonly ConcurrentDictionary<string, IAsyncPolicy<HttpResponseMessage>> _httpPolicies = new(StringComparer.Ordinal);

    public static ResiliencePolicyOptions HttpDefaults { get; } = new()
    {
        MaxRetryAttempts = 3,
        BaseDelay = TimeSpan.FromMilliseconds(500),
        BackoffExponent = 2.0,
        JitterPercentage = 0.2,
        CircuitBreakerFailures = 5,
        CircuitBreakDuration = TimeSpan.FromSeconds(30)
    };

    public static ResiliencePolicyOptions FastApiDefaults { get; } = new()
    {
        MaxRetryAttempts = 2,
        BaseDelay = TimeSpan.FromMilliseconds(200),
        BackoffExponent = 1.5,
        JitterPercentage = 0.15,
        CircuitBreakerFailures = 3,
        CircuitBreakDuration = TimeSpan.FromSeconds(15)
    };

    public static ResiliencePolicyOptions SlowServiceDefaults { get; } = new()
    {
        MaxRetryAttempts = 5,
        BaseDelay = TimeSpan.FromSeconds(1),
        BackoffExponent = 2.0,
        JitterPercentage = 0.25,
        CircuitBreakerFailures = 8,
        CircuitBreakDuration = TimeSpan.FromMinutes(2)
    };

    public static IAsyncPolicy<HttpResponseMessage> GetHttpPolicy(
        string serviceType,
        ResiliencePolicyOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceType);
        var effectiveOptions = options ?? HttpDefaults;
        var cacheKey = $"{serviceType}|{GetOptionsHash(effectiveOptions)}";

        return _httpPolicies.GetOrAdd(cacheKey, _ => BuildHttpPolicy(effectiveOptions));
    }

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
            onBreak: (result, breakDelay, context) =>
            {
                if (context.TryGetValue(OnCircuitBreakerCallbackKey, out var obj) &&
                    obj is Action<DelegateResult<HttpResponseMessage>, CircuitState, TimeSpan> callback)
                {
                    callback(result, CircuitState.Open, breakDelay);
                }
            },
            onReset: _ =>
            {
                // Reset events are not currently observed by tests.
            },
            onHalfOpen: () =>
            {
                // Half-open events are not currently observed by tests.
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
