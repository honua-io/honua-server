// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.CircuitBreaker;
using System.Diagnostics.CodeAnalysis;

namespace Honua.Core.Features.Infrastructure.Resilience;

public static partial class HttpClientResilienceExtensions
{
    public static IHttpClientBuilder AddResilientHttpClient<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TClient>(
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
                    if (logger is not null)
                    {
                        LogRetry(logger, request.RequestUri, attempt, delay.TotalMilliseconds, serviceType, errorMessage);
                    }

                    // Record retry metrics
                    RecordRetryMetrics(meter, serviceType, attempt, delay);
                },
                onCircuitBreaker: (result, state, duration) =>
                {
                    var errorMessage = GetFailureMessage(result);
                    switch (state)
                    {
                        case CircuitState.Open:
                            if (logger is not null)
                            {
                                LogCircuitOpened(logger, serviceType, duration.TotalMilliseconds, errorMessage);
                            }

                            break;
                        case CircuitState.HalfOpen:
                            if (logger is not null)
                            {
                                LogCircuitHalfOpen(logger, serviceType);
                            }

                            break;
                        case CircuitState.Closed:
                            if (logger is not null)
                            {
                                LogCircuitClosed(logger, serviceType);
                            }

                            break;
                    }

                    // Record circuit breaker metrics
                    RecordCircuitBreakerMetrics(meter, serviceType, state);
                });

            // Attach the context to the request: PolicyHttpMessageHandler executes the policy with
            // the request's own context, so without this the retry/circuit-breaker callbacks above
            // are never invoked and resilience events go unlogged.
            request.SetPolicyExecutionContext(context);

            return policy;
        });
    }

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "HTTP request to {RequestUri} failed (attempt {Attempt}), retrying in {DelayMs}ms. Service: {ServiceType}, Error: {ErrorMessage}")]
    private static partial void LogRetry(
        ILogger logger,
        Uri? requestUri,
        int attempt,
        double delayMs,
        string serviceType,
        string errorMessage);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Circuit breaker opened for service {ServiceType} for {DurationMs}ms. Last error: {ErrorMessage}")]
    private static partial void LogCircuitOpened(
        ILogger logger,
        string serviceType,
        double durationMs,
        string errorMessage);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Circuit breaker for service {ServiceType} is half-open, allowing test request")]
    private static partial void LogCircuitHalfOpen(
        ILogger logger,
        string serviceType);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Circuit breaker for service {ServiceType} closed successfully")]
    private static partial void LogCircuitClosed(
        ILogger logger,
        string serviceType);

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

internal sealed class HttpResilienceHandler
{
    // This class exists solely to provide a logger category for HTTP resilience operations
}
