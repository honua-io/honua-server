// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.ComponentModel.DataAnnotations;

namespace Honua.Core.Features.Infrastructure.Resilience;

/// <summary>
/// Configuration options for HTTP client resilience patterns.
/// Provides centralized configuration for retry policies, circuit breakers, and timeouts.
/// </summary>
public sealed class HttpResilienceOptions
{
    /// <summary>
    /// Configuration section name for binding from environment variables.
    /// </summary>
    public const string SectionName = "HttpResilience";

    /// <summary>
    /// Default resilience settings for fast API services (geocoding, webhooks).
    /// </summary>
    public HttpServiceResilienceConfig FastApi { get; init; } = new()
    {
        MaxRetryAttempts = 2,
        BaseDelayMs = 200,
        BackoffExponent = 1.5,
        JitterPercentage = 0.15,
        CircuitBreakerFailures = 3,
        CircuitBreakDurationSeconds = 15,
        TimeoutSeconds = 10
    };

    /// <summary>
    /// Default resilience settings for standard HTTP services.
    /// </summary>
    public HttpServiceResilienceConfig Standard { get; init; } = new()
    {
        MaxRetryAttempts = 3,
        BaseDelayMs = 500,
        BackoffExponent = 2.0,
        JitterPercentage = 0.2,
        CircuitBreakerFailures = 5,
        CircuitBreakDurationSeconds = 30,
        TimeoutSeconds = 30
    };

    /// <summary>
    /// Resilience settings for slow import/discovery services.
    /// </summary>
    public HttpServiceResilienceConfig SlowService { get; init; } = new()
    {
        MaxRetryAttempts = 5,
        BaseDelayMs = 1000,
        BackoffExponent = 2.0,
        JitterPercentage = 0.25,
        CircuitBreakerFailures = 8,
        CircuitBreakDurationSeconds = 120,
        TimeoutSeconds = 300
    };

    /// <summary>
    /// Service-specific overrides for individual HTTP clients.
    /// Key is the service type identifier, value is the specific configuration.
    /// </summary>
    public Dictionary<string, HttpServiceResilienceConfig> ServiceOverrides { get; init; } = new();

    /// <summary>
    /// Gets the effective resilience configuration for a service type.
    /// </summary>
    /// <param name="serviceType">Service type identifier.</param>
    /// <param name="defaultProfile">Default profile to use if no override exists.</param>
    /// <returns>Effective resilience configuration.</returns>
    public HttpServiceResilienceConfig GetServiceConfig(string serviceType, HttpServiceResilienceProfile defaultProfile = HttpServiceResilienceProfile.Standard)
    {
        if (ServiceOverrides.TryGetValue(serviceType, out var serviceConfig))
        {
            return serviceConfig;
        }

        return defaultProfile switch
        {
            HttpServiceResilienceProfile.FastApi => FastApi,
            HttpServiceResilienceProfile.SlowService => SlowService,
            _ => Standard
        };
    }

    /// <summary>
    /// Converts a HttpServiceResilienceConfig to ResiliencePolicyOptions.
    /// </summary>
    /// <param name="config">The HTTP service resilience configuration.</param>
    /// <returns>ResiliencePolicyOptions equivalent.</returns>
    public static ResiliencePolicyOptions ToResiliencePolicyOptions(HttpServiceResilienceConfig config)
    {
        return new ResiliencePolicyOptions
        {
            MaxRetryAttempts = config.MaxRetryAttempts,
            BaseDelay = TimeSpan.FromMilliseconds(config.BaseDelayMs),
            BackoffExponent = config.BackoffExponent,
            JitterPercentage = config.JitterPercentage,
            CircuitBreakerFailures = config.CircuitBreakerFailures,
            CircuitBreakDuration = TimeSpan.FromSeconds(config.CircuitBreakDurationSeconds)
        };
    }
}

/// <summary>
/// Configuration for a specific HTTP service's resilience behavior.
/// </summary>
public sealed class HttpServiceResilienceConfig
{
    /// <summary>
    /// Maximum number of retry attempts before failing.
    /// </summary>
    [Range(0, 10, ErrorMessage = "MaxRetryAttempts must be between 0 and 10")]
    public int MaxRetryAttempts { get; init; } = 3;

    /// <summary>
    /// Base delay in milliseconds for exponential backoff between retries.
    /// </summary>
    [Range(50, 30000, ErrorMessage = "BaseDelayMs must be between 50 and 30000")]
    public int BaseDelayMs { get; init; } = 500;

    /// <summary>
    /// Exponent for exponential backoff calculation.
    /// </summary>
    [Range(1.0, 3.0, ErrorMessage = "BackoffExponent must be between 1.0 and 3.0")]
    public double BackoffExponent { get; init; } = 2.0;

    /// <summary>
    /// Percentage of jitter to apply to retry delays (0.0 to 1.0).
    /// </summary>
    [Range(0.0, 1.0, ErrorMessage = "JitterPercentage must be between 0.0 and 1.0")]
    public double JitterPercentage { get; init; } = 0.2;

    /// <summary>
    /// Number of consecutive failures before opening the circuit breaker.
    /// </summary>
    [Range(1, 50, ErrorMessage = "CircuitBreakerFailures must be between 1 and 50")]
    public int CircuitBreakerFailures { get; init; } = 5;

    /// <summary>
    /// Duration in seconds the circuit breaker stays open before allowing a test request.
    /// </summary>
    [Range(5, 3600, ErrorMessage = "CircuitBreakDurationSeconds must be between 5 and 3600")]
    public int CircuitBreakDurationSeconds { get; init; } = 30;

    /// <summary>
    /// Timeout in seconds for individual HTTP operations.
    /// </summary>
    [Range(1, 600, ErrorMessage = "TimeoutSeconds must be between 1 and 600")]
    public int TimeoutSeconds { get; init; } = 30;
}

/// <summary>
/// Predefined resilience profiles for different types of HTTP services.
/// </summary>
public enum HttpServiceResilienceProfile
{
    /// <summary>
    /// Fast API services like geocoding, webhooks - optimized for low latency.
    /// </summary>
    FastApi,

    /// <summary>
    /// Standard HTTP services - balanced resilience settings.
    /// </summary>
    Standard,

    /// <summary>
    /// Slow services like imports, discovery - optimized for reliability over speed.
    /// </summary>
    SlowService
}
