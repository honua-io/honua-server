// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Infrastructure.Resilience;

public sealed record ResiliencePolicyOptions
{
    public const string SectionName = "Resilience";

    public int MaxRetryAttempts { get; init; } = 3;

    public TimeSpan BaseDelay { get; init; } = TimeSpan.FromMilliseconds(100);

    public double BackoffExponent { get; init; } = 2.0;

    public double JitterPercentage { get; init; }

    public int CircuitBreakerFailures { get; init; } = 5;

    public TimeSpan CircuitBreakDuration { get; init; } = TimeSpan.FromSeconds(30);

    public static ResiliencePolicyOptions Default { get; } = new();

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

    public int RetryAfterSeconds => (int)Math.Ceiling(CircuitBreakDuration.TotalSeconds);
}
