// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Infrastructure.Resilience;

/// <summary>
/// Shared options for retry and circuit breaker policies across external dependencies.
/// Bind to the "Resilience" configuration section to override defaults.
/// </summary>
public sealed record ResiliencePolicyOptions
{
    /// <summary>
    /// Configuration section name for appsettings.json binding.
    /// </summary>
    public const string SectionName = "Resilience";

    /// <summary>
    /// Maximum number of retry attempts before failing.
    /// </summary>
    public int MaxRetryAttempts { get; init; } = 3;

    /// <summary>
    /// Base delay for exponential backoff between retries.
    /// </summary>
    public TimeSpan BaseDelay { get; init; } = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// Exponent for exponential backoff calculation.
    /// </summary>
    public double BackoffExponent { get; init; } = 2.0;

    /// <summary>
    /// Percentage of jitter to apply to retry delays (0.0 to 1.0).
    /// </summary>
    public double JitterPercentage { get; init; }

    /// <summary>
    /// Number of consecutive failures before the circuit breaker opens.
    /// </summary>
    public int CircuitBreakerFailures { get; init; } = 5;

    /// <summary>
    /// Duration the circuit breaker stays open before allowing a test request.
    /// </summary>
    public TimeSpan CircuitBreakDuration { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Singleton default instance with standard defaults.
    /// </summary>
    public static ResiliencePolicyOptions Default { get; } = new();

    /// <inheritdoc/>
    public TimeSpan GetDelay(int attempt)
    {
        var delayMs = BaseDelay.TotalMilliseconds * Math.Pow(BackoffExponent, attempt);
        if (JitterPercentage > 0)
        {
            var jitterMultiplier = 1.0 + (Random.Shared.NextDouble() * 2 - 1) * JitterPercentage;
            delayMs *= jitterMultiplier;
        }

        if (delayMs < 0)
        {
            delayMs = 0;
        }

        return TimeSpan.FromMilliseconds(delayMs);
    }

    /// <inheritdoc/>
    public int RetryAfterSeconds => (int)Math.Ceiling(CircuitBreakDuration.TotalSeconds);
}
