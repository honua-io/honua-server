// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.ControlPlane.Domain;

public enum BackoffStrategy
{
    Fixed,

    Linear,

    Exponential
}

public sealed record JobRetryPolicy
{
    public static readonly JobRetryPolicy Default = new()
    {
        MaxAttempts = 3,
        Strategy = BackoffStrategy.Exponential,
        BaseDelay = TimeSpan.FromSeconds(30),
        MaxDelay = TimeSpan.FromMinutes(10)
    };

    public static readonly JobRetryPolicy None = new()
    {
        MaxAttempts = 1,
        Strategy = BackoffStrategy.Fixed,
        BaseDelay = TimeSpan.Zero,
        MaxDelay = TimeSpan.Zero
    };

    public int MaxAttempts { get; init; } = 3;

    public BackoffStrategy Strategy { get; init; } = BackoffStrategy.Exponential;

    public TimeSpan BaseDelay { get; init; } = TimeSpan.FromSeconds(30);

    public TimeSpan MaxDelay { get; init; } = TimeSpan.FromMinutes(10);

    public TimeSpan ComputeDelay(int attemptNumber)
    {
        if (attemptNumber <= 1)
        {
            return TimeSpan.Zero;
        }

        var retryIndex = attemptNumber - 2; // 0-based retry index
        var delay = Strategy switch
        {
            BackoffStrategy.Fixed => BaseDelay,
            BackoffStrategy.Linear => TimeSpan.FromTicks(BaseDelay.Ticks * (retryIndex + 1)),
            BackoffStrategy.Exponential => ComputeExponentialDelay(retryIndex),
            _ => BaseDelay
        };

        return delay > MaxDelay ? MaxDelay : delay;
    }

    private TimeSpan ComputeExponentialDelay(int retryIndex)
    {
        // Cap the shift exponent: 1L << retryIndex masks the count to (count & 63) once
        // retryIndex >= 64, wrapping the multiplier back toward 1 and collapsing the delay
        // to BaseDelay instead of saturating. Clamp the exponent to 62 (the largest safe
        // shift for a long) so high MaxAttempts policies stay monotonic. Anything that
        // overflows TimeSpan.Ticks is treated as saturated at MaxDelay.
        var shift = Math.Min(retryIndex, 62);
        var multiplier = 1L << shift;

        try
        {
            return checked(TimeSpan.FromTicks(BaseDelay.Ticks * multiplier));
        }
        catch (OverflowException)
        {
            return MaxDelay;
        }
    }

    public bool ShouldRetry(int attemptCount) => attemptCount < MaxAttempts;
}

public sealed record JobHeartbeatPolicy
{
    public static readonly JobHeartbeatPolicy Default = new()
    {
        Interval = TimeSpan.FromSeconds(30),
        Timeout = TimeSpan.FromSeconds(90)
    };

    public TimeSpan Interval { get; init; } = TimeSpan.FromSeconds(30);

    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(90);

    public bool IsExpired(DateTimeOffset lastHeartbeat, DateTimeOffset now)
        => (now - lastHeartbeat) > Timeout;
}

public sealed record JobTimeoutPolicy
{
    public static readonly JobTimeoutPolicy Default = new()
    {
        MaxDuration = TimeSpan.FromHours(1)
    };

    public static readonly JobTimeoutPolicy LongRunning = new()
    {
        MaxDuration = TimeSpan.FromHours(24)
    };

    public TimeSpan MaxDuration { get; init; } = TimeSpan.FromHours(1);

    public bool IsExpired(DateTimeOffset startedAt, DateTimeOffset now)
        => (now - startedAt) > MaxDuration;
}
