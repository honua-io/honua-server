// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.ComponentModel.DataAnnotations;

namespace Honua.Core.Features.Infrastructure.Resilience;

/// <summary>
/// Configuration options for circuit breaker patterns in external service integrations.
/// Provides resilience against cascading failures and enables graceful degradation.
/// </summary>
public sealed class CircuitBreakerOptions
{
    /// <summary>
    /// Configuration section name for binding from environment variables.
    /// </summary>
    public const string SectionName = "CircuitBreaker";

    /// <summary>
    /// Number of consecutive failures before opening the circuit.
    /// Default is 5 failures.
    /// </summary>
    [Range(1, 100, ErrorMessage = "FailureThreshold must be between 1 and 100")]
    public int FailureThreshold { get; init; } = 5;

    /// <summary>
    /// Duration the circuit stays open before attempting to close.
    /// Default is 30 seconds.
    /// </summary>
    [Range(1, 300, ErrorMessage = "DurationOfBreakSeconds must be between 1 and 300")]
    public int DurationOfBreakSeconds { get; init; } = 30;

    /// <summary>
    /// Number of successful calls required to close the circuit when in half-open state.
    /// Default is 3 successful calls.
    /// </summary>
    [Range(1, 20, ErrorMessage = "SuccessThreshold must be between 1 and 20")]
    public int SuccessThreshold { get; init; } = 3;

    /// <summary>
    /// Timeout for individual HTTP operations in seconds.
    /// Default is 30 seconds.
    /// </summary>
    [Range(5, 300, ErrorMessage = "TimeoutSeconds must be between 5 and 300")]
    public int TimeoutSeconds { get; init; } = 30;

    /// <summary>
    /// Initial retry delay in milliseconds.
    /// Default is 500ms.
    /// </summary>
    [Range(100, 10000, ErrorMessage = "InitialRetryDelayMs must be between 100 and 10000")]
    public int InitialRetryDelayMs { get; init; } = 500;

    /// <summary>
    /// Maximum retry delay in milliseconds with exponential backoff.
    /// Default is 5000ms (5 seconds).
    /// </summary>
    [Range(1000, 60000, ErrorMessage = "MaxRetryDelayMs must be between 1000 and 60000")]
    public int MaxRetryDelayMs { get; init; } = 5000;

    /// <summary>
    /// Maximum number of retry attempts.
    /// Default is 3 retries.
    /// </summary>
    [Range(1, 10, ErrorMessage = "MaxRetryAttempts must be between 1 and 10")]
    public int MaxRetryAttempts { get; init; } = 3;

    /// <summary>
    /// Gets the duration of break as a TimeSpan.
    /// </summary>
    public TimeSpan DurationOfBreak => TimeSpan.FromSeconds(DurationOfBreakSeconds);

    /// <summary>
    /// Gets the timeout as a TimeSpan.
    /// </summary>
    public TimeSpan Timeout => TimeSpan.FromSeconds(TimeoutSeconds);

    /// <summary>
    /// Gets the initial retry delay as a TimeSpan.
    /// </summary>
    public TimeSpan InitialRetryDelay => TimeSpan.FromMilliseconds(InitialRetryDelayMs);

    /// <summary>
    /// Gets the maximum retry delay as a TimeSpan.
    /// </summary>
    public TimeSpan MaxRetryDelay => TimeSpan.FromMilliseconds(MaxRetryDelayMs);
}

/// <summary>
/// External service circuit breaker configurations.
/// </summary>
public sealed class ExternalServiceCircuitBreakerOptions
{
    /// <summary>
    /// Configuration section name for external services.
    /// </summary>
    public const string SectionName = "ExternalServices:CircuitBreaker";

    /// <summary>
    /// Circuit breaker options for ArcGIS REST services.
    /// </summary>
    public CircuitBreakerOptions ArcGisRest { get; init; } = new();

    /// <summary>
    /// Circuit breaker options for GeoServer REST services.
    /// </summary>
    public CircuitBreakerOptions GeoServerRest { get; init; } = new();

    /// <summary>
    /// Circuit breaker options for webhook delivery.
    /// </summary>
    public CircuitBreakerOptions Webhooks { get; init; } = new();

    /// <summary>
    /// Circuit breaker options for identity provider connectivity.
    /// </summary>
    public CircuitBreakerOptions IdentityProvider { get; init; } = new();
}