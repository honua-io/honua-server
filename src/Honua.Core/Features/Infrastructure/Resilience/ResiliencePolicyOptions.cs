// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Infrastructure.Resilience;

/// <summary>
/// Shared options for retry and circuit breaker policies across external dependencies.
/// </summary>
public sealed record ResiliencePolicyOptions
{
    public int MaxRetryAttempts { get; init; } = 3;
    public TimeSpan BaseDelay { get; init; } = TimeSpan.FromMilliseconds(100);
    public double BackoffExponent { get; init; } = 2.0;
    public int CircuitBreakerFailures { get; init; } = 5;
    public TimeSpan CircuitBreakDuration { get; init; } = TimeSpan.FromSeconds(30);

    public static ResiliencePolicyOptions Default { get; } = new();

    public TimeSpan GetDelay(int attempt)
    {
        var delayMs = BaseDelay.TotalMilliseconds * Math.Pow(BackoffExponent, attempt);
        return TimeSpan.FromMilliseconds(delayMs);
    }

    public int RetryAfterSeconds => (int)Math.Ceiling(CircuitBreakDuration.TotalSeconds);
}
