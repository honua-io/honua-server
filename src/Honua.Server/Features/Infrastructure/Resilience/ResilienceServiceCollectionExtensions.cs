// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Microsoft.Extensions.Options;
using Polly;
using Polly.CircuitBreaker;
using Polly.Timeout;
using Honua.Core.Features.Infrastructure.Resilience;
using Honua.Core.Features.Infrastructure.Monitoring;

namespace Honua.Server.Features.Infrastructure.Resilience;

/// <summary>
/// Service collection extensions for registering resilience patterns.
/// </summary>
internal static class ResilienceServiceCollectionExtensions
{
    /// <summary>
    /// Adds circuit breaker and resilience patterns to HTTP clients.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">The configuration.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddResiliencePatterns(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // Configure circuit breaker options
        services.Configure<ExternalServiceCircuitBreakerOptions>(
            configuration.GetSection(ExternalServiceCircuitBreakerOptions.SectionName));

        // Register connection pool metrics
        services.AddSingleton<ConnectionPoolMetrics>();

        // Register circuit breaker factory
        services.AddSingleton<CircuitBreakerFactory>();

        return services;
    }

    /// <summary>
    /// Configures HTTP client with circuit breaker and resilience patterns.
    /// </summary>
    /// <param name="httpClientBuilder">The HTTP client builder.</param>
    /// <param name="circuitBreakerOptions">Circuit breaker options.</param>
    /// <param name="serviceName">Service name for metrics tagging.</param>
    /// <returns>The HTTP client builder for chaining.</returns>
    public static IHttpClientBuilder AddResiliencePolicy(
        this IHttpClientBuilder httpClientBuilder,
        CircuitBreakerOptions circuitBreakerOptions,
        string serviceName)
    {
        return httpClientBuilder.AddPolicyHandler((serviceProvider, request) =>
        {
            var logger = serviceProvider.GetRequiredService<ILogger<ResilienceServiceCollectionExtensions>>();

            // Create retry policy with exponential backoff
            var retryPolicy = Policy
                .Handle<HttpRequestException>()
                .Or<TimeoutRejectedException>()
                .WaitAndRetryAsync(
                    retryCount: circuitBreakerOptions.MaxRetryAttempts,
                    sleepDurationProvider: retryAttempt => TimeSpan.FromMilliseconds(
                        Math.Min(
                            circuitBreakerOptions.InitialRetryDelayMs * Math.Pow(2, retryAttempt - 1),
                            circuitBreakerOptions.MaxRetryDelayMs)),
                    onRetry: (outcome, timespan, retryCount, context) =>
                    {
                        logger.LogWarning(
                            "Retry {RetryCount} for {ServiceName} after {Delay}ms. Reason: {Reason}",
                            retryCount,
                            serviceName,
                            timespan.TotalMilliseconds,
                            outcome.Exception?.Message ?? "Unknown");
                    });

            // Create circuit breaker policy
            var circuitBreakerPolicy = Policy
                .Handle<HttpRequestException>()
                .Or<TimeoutRejectedException>()
                .CircuitBreakerAsync(
                    handledEventsAllowedBeforeBreaking: circuitBreakerOptions.FailureThreshold,
                    durationOfBreak: circuitBreakerOptions.DurationOfBreak,
                    onBreak: (exception, duration) =>
                    {
                        logger.LogWarning(
                            "Circuit breaker opened for {ServiceName} for {Duration}ms. Reason: {Reason}",
                            serviceName,
                            duration.TotalMilliseconds,
                            exception.Message);
                    },
                    onReset: () =>
                    {
                        logger.LogInformation(
                            "Circuit breaker reset for {ServiceName}",
                            serviceName);
                    },
                    onHalfOpen: () =>
                    {
                        logger.LogInformation(
                            "Circuit breaker half-open for {ServiceName}",
                            serviceName);
                    });

            // Create timeout policy
            var timeoutPolicy = Policy.TimeoutAsync<HttpResponseMessage>(circuitBreakerOptions.Timeout);

            // Combine policies: Timeout -> Retry -> Circuit Breaker
            return Policy.WrapAsync(retryPolicy, circuitBreakerPolicy, timeoutPolicy);
        });
    }
}

/// <summary>
/// Factory for creating circuit breaker instances for different services.
/// </summary>
internal sealed class CircuitBreakerFactory
{
    private readonly ExternalServiceCircuitBreakerOptions _options;
    private readonly ILogger<CircuitBreakerFactory> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="CircuitBreakerFactory"/> class.
    /// </summary>
    /// <param name="options">Circuit breaker options.</param>
    /// <param name="logger">Logger instance.</param>
    public CircuitBreakerFactory(
        IOptions<ExternalServiceCircuitBreakerOptions> options,
        ILogger<CircuitBreakerFactory> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>
    /// Creates a circuit breaker for ArcGIS REST services.
    /// </summary>
    /// <returns>Circuit breaker policy.</returns>
    public IAsyncPolicy<HttpResponseMessage> CreateArcGisRestCircuitBreaker()
    {
        return CreateCircuitBreaker(_options.ArcGisRest, "ArcGIS REST");
    }

    /// <summary>
    /// Creates a circuit breaker for GeoServer REST services.
    /// </summary>
    /// <returns>Circuit breaker policy.</returns>
    public IAsyncPolicy<HttpResponseMessage> CreateGeoServerRestCircuitBreaker()
    {
        return CreateCircuitBreaker(_options.GeoServerRest, "GeoServer REST");
    }

    /// <summary>
    /// Creates a circuit breaker for webhook delivery.
    /// </summary>
    /// <returns>Circuit breaker policy.</returns>
    public IAsyncPolicy<HttpResponseMessage> CreateWebhookCircuitBreaker()
    {
        return CreateCircuitBreaker(_options.Webhooks, "Webhook");
    }

    /// <summary>
    /// Creates a circuit breaker for identity provider services.
    /// </summary>
    /// <returns>Circuit breaker policy.</returns>
    public IAsyncPolicy<HttpResponseMessage> CreateIdentityProviderCircuitBreaker()
    {
        return CreateCircuitBreaker(_options.IdentityProvider, "Identity Provider");
    }

    /// <summary>
    /// Creates a circuit breaker with the specified options.
    /// </summary>
    /// <param name="options">Circuit breaker options.</param>
    /// <param name="serviceName">Service name for logging.</param>
    /// <returns>Circuit breaker policy.</returns>
    private IAsyncPolicy<HttpResponseMessage> CreateCircuitBreaker(
        CircuitBreakerOptions options,
        string serviceName)
    {
        return Policy
            .HandleResult<HttpResponseMessage>(r => !r.IsSuccessStatusCode)
            .Or<HttpRequestException>()
            .Or<TimeoutRejectedException>()
            .CircuitBreakerAsync(
                handledEventsAllowedBeforeBreaking: options.FailureThreshold,
                durationOfBreak: options.DurationOfBreak,
                onBreak: (result, duration) =>
                {
                    _logger.LogWarning(
                        "Circuit breaker opened for {ServiceName} for {Duration}ms",
                        serviceName,
                        duration.TotalMilliseconds);
                },
                onReset: () =>
                {
                    _logger.LogInformation(
                        "Circuit breaker reset for {ServiceName}",
                        serviceName);
                },
                onHalfOpen: () =>
                {
                    _logger.LogInformation(
                        "Circuit breaker half-open for {ServiceName}",
                        serviceName);
                });
    }
}