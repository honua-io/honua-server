// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Infrastructure.Resilience;

/// <summary>
/// Shared options for retry and circuit breaker policies across external dependencies.
/// </summary>
public sealed record ResiliencePolicyOptions
{
    /// <inheritdoc/>
    public int MaxRetryAttempts { get; init; } = 3;
    /// <inheritdoc/>
    public TimeSpan BaseDelay { get; init; } = TimeSpan.FromMilliseconds(100);
    /// <inheritdoc/>
    public double BackoffExponent { get; init; } = 2.0;
    /// <inheritdoc/>
    public double JitterPercentage { get; init; }
    /// <inheritdoc/>
    public int CircuitBreakerFailures { get; init; } = 5;
    /// <inheritdoc/>
    public TimeSpan CircuitBreakDuration { get; init; } = TimeSpan.FromSeconds(30);

    /// <inheritdoc/>
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
