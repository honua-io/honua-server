// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.CircuitBreaker;

namespace Honua.Core.Features.Infrastructure.Resilience;

/// <summary>
/// Extension methods for adding HTTP client resilience policies to service registration.
/// </summary>
public static class HttpClientResilienceExtensions
{
    /// <summary>
    /// Adds a typed HTTP client with standardized resilience policies.
    /// </summary>
    /// <typeparam name="TClient">The typed HTTP client type.</typeparam>
    /// <param name="services">The service collection.</param>
    /// <param name="serviceType">Service type identifier for circuit breaker isolation.</param>
    /// <param name="options">Optional resilience policy options.</param>
    /// <param name="configureClient">Optional HTTP client configuration.</param>
    /// <param name="configureHandler">Optional message handler configuration.</param>
    /// <returns>The HTTP client builder for further configuration.</returns>
    public static IHttpClientBuilder AddResilientHttpClient<TClient>(
        this IServiceCollection services,
        string serviceType,
        ResiliencePolicyOptions? options = null,
        Action<HttpClient>? configureClient = null,
        Func<HttpMessageHandler>? configureHandler = null)
        where TClient : class
    {
        var builder = services.AddHttpClient<TClient>();

        if (configureClient is not null)
        {
            builder.ConfigureHttpClient(configureClient);
        }

        if (configureHandler is not null)
        {
            builder.ConfigurePrimaryHttpMessageHandler(configureHandler);
        }

        return builder.AddHttpResiliencePolicy(serviceType, options);
    }

    /// <summary>
    /// Adds a named HTTP client with standardized resilience policies.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="name">The HTTP client name.</param>
    /// <param name="serviceType">Service type identifier for circuit breaker isolation.</param>
    /// <param name="options">Optional resilience policy options.</param>
    /// <param name="configureClient">Optional HTTP client configuration.</param>
    /// <param name="configureHandler">Optional message handler configuration.</param>
    /// <returns>The HTTP client builder for further configuration.</returns>
    public static IHttpClientBuilder AddResilientHttpClient(
        this IServiceCollection services,
        string name,
        string serviceType,
        ResiliencePolicyOptions? options = null,
        Action<HttpClient>? configureClient = null,
        Func<HttpMessageHandler>? configureHandler = null)
    {
        var builder = services.AddHttpClient(name);

        if (configureClient is not null)
        {
            builder.ConfigureHttpClient(configureClient);
        }

        if (configureHandler is not null)
        {
            builder.ConfigurePrimaryHttpMessageHandler(configureHandler);
        }

        return builder.AddHttpResiliencePolicy(serviceType, options);
    }

    /// <summary>
    /// Adds resilience policies to an existing HTTP client builder.
    /// </summary>
    /// <param name="builder">The HTTP client builder.</param>
    /// <param name="serviceType">Service type identifier for circuit breaker isolation.</param>
    /// <param name="options">Optional resilience policy options.</param>
    /// <returns>The HTTP client builder for chaining.</returns>
    public static IHttpClientBuilder AddHttpResiliencePolicy(
        this IHttpClientBuilder builder,
        string serviceType,
        ResiliencePolicyOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceType);

        return builder.AddPolicyHandler((serviceProvider, request) =>
        {
            var logger = serviceProvider.GetService<ILogger<HttpResilienceHandler>>();
            var meter = serviceProvider.GetService<object>(); // Placeholder for metrics

            var policy = HttpResiliencePolicies.GetHttpPolicy(serviceType, options);

            var context = HttpResiliencePolicies.CreateHttpContext(
                onRetry: (result, delay, attempt) =>
                {
                    var errorMessage = GetFailureMessage(result);
                    logger?.LogWarning(
                        "HTTP request to {RequestUri} failed (attempt {Attempt}), retrying in {DelayMs}ms. Service: {ServiceType}, Error: {ErrorMessage}",
                        request.RequestUri, attempt, delay.TotalMilliseconds, serviceType, errorMessage);

                    // Record retry metrics
                    RecordRetryMetrics(meter, serviceType, attempt, delay);
                },
                onCircuitBreaker: (result, state, duration) =>
                {
                    var errorMessage = GetFailureMessage(result);
                    switch (state)
                    {
                        case CircuitState.Open:
                            logger?.LogError(
                                "Circuit breaker opened for service {ServiceType} for {DurationMs}ms. Last error: {ErrorMessage}",
                                serviceType, duration.TotalMilliseconds, errorMessage);
                            break;
                        case CircuitState.HalfOpen:
                            logger?.LogInformation(
                                "Circuit breaker for service {ServiceType} is half-open, allowing test request",
                                serviceType);
                            break;
                        case CircuitState.Closed:
                            logger?.LogInformation(
                                "Circuit breaker for service {ServiceType} closed successfully",
                                serviceType);
                            break;
                    }

                    // Record circuit breaker metrics
                    RecordCircuitBreakerMetrics(meter, serviceType, state);
                });

            return policy;
        });
    }

    private static string GetFailureMessage(DelegateResult<HttpResponseMessage> result)
    {
        if (result.Exception is not null)
        {
            return result.Exception.Message;
        }

        if (result.Result is not null)
        {
            return $"HTTP {(int)result.Result.StatusCode} {result.Result.StatusCode}";
        }

        return "Unknown failure";
    }

    private static void RecordRetryMetrics(object? meter, string serviceType, int attempt, TimeSpan delay)
    {
        // Metrics recording is optional - if System.Diagnostics.DiagnosticSource is available,
        // this could be enhanced to emit telemetry. For now, we'll skip metrics to avoid
        // adding dependencies to Core.
    }

    private static void RecordCircuitBreakerMetrics(object? meter, string serviceType, CircuitState state)
    {
        // Metrics recording is optional - if System.Diagnostics.DiagnosticSource is available,
        // this could be enhanced to emit telemetry. For now, we'll skip metrics to avoid
        // adding dependencies to Core.
    }
}

/// <summary>
/// Internal class to provide logger context for HTTP resilience operations.
/// </summary>
internal sealed class HttpResilienceHandler
{
    // This class exists solely to provide a logger category for HTTP resilience operations
}
