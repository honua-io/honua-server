// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.ControlPlane.Domain;

/// <summary>
/// Backoff strategy used when retrying failed execution jobs.
/// </summary>
public enum BackoffStrategy
{
    /// <summary>
    /// No additional delay between retries beyond the base interval.
    /// </summary>
    Fixed,

    /// <summary>
    /// Delay increases linearly with each retry attempt.
    /// </summary>
    Linear,

    /// <summary>
    /// Delay doubles with each retry attempt.
    /// </summary>
    Exponential
}

/// <summary>
/// Retry policy governing how failed execution jobs are retried.
/// </summary>
public sealed record JobRetryPolicy
{
    /// <summary>
    /// Default retry policy: 3 attempts with exponential backoff starting at 30 seconds.
    /// </summary>
    public static readonly JobRetryPolicy Default = new()
    {
        MaxAttempts = 3,
        Strategy = BackoffStrategy.Exponential,
        BaseDelay = TimeSpan.FromSeconds(30),
        MaxDelay = TimeSpan.FromMinutes(10)
    };

    /// <summary>
    /// No-retry policy for jobs that should not be retried on failure.
    /// </summary>
    public static readonly JobRetryPolicy None = new()
    {
        MaxAttempts = 1,
        Strategy = BackoffStrategy.Fixed,
        BaseDelay = TimeSpan.Zero,
        MaxDelay = TimeSpan.Zero
    };

    /// <summary>
    /// Maximum number of execution attempts including the initial attempt.
    /// A value of 1 means no retries.
    /// </summary>
    public int MaxAttempts { get; init; } = 3;

    /// <summary>
    /// Backoff strategy applied between retry attempts.
    /// </summary>
    public BackoffStrategy Strategy { get; init; } = BackoffStrategy.Exponential;

    /// <summary>
    /// Base delay before the first retry. Subsequent delays are computed from this
    /// value according to <see cref="Strategy"/>.
    /// </summary>
    public TimeSpan BaseDelay { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Upper bound on the computed delay between retries.
    /// </summary>
    public TimeSpan MaxDelay { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Computes the delay before the given retry attempt using the configured backoff strategy.
    /// </summary>
    /// <param name="attemptNumber">One-based attempt number (2 = first retry).</param>
    /// <returns>Delay before this attempt should start.</returns>
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
            BackoffStrategy.Exponential => TimeSpan.FromTicks(BaseDelay.Ticks * (1L << retryIndex)),
            _ => BaseDelay
        };

        return delay > MaxDelay ? MaxDelay : delay;
    }

    /// <summary>
    /// Returns true when the given attempt count has not exceeded the maximum.
    /// </summary>
    /// <param name="attemptCount">Current completed attempt count.</param>
    public bool ShouldRetry(int attemptCount) => attemptCount < MaxAttempts;
}

/// <summary>
/// Heartbeat policy governing worker liveness detection for claimed execution jobs.
/// </summary>
public sealed record JobHeartbeatPolicy
{
    /// <summary>
    /// Default heartbeat policy: 30-second interval, 90-second timeout.
    /// </summary>
    public static readonly JobHeartbeatPolicy Default = new()
    {
        Interval = TimeSpan.FromSeconds(30),
        Timeout = TimeSpan.FromSeconds(90)
    };

    /// <summary>
    /// Expected interval at which workers send heartbeat signals.
    /// </summary>
    public TimeSpan Interval { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Duration after the last heartbeat before the job is considered abandoned.
    /// Must be greater than <see cref="Interval"/> to allow for transient delays.
    /// </summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(90);

    /// <summary>
    /// Returns true when the heartbeat has expired relative to <paramref name="now"/>.
    /// </summary>
    /// <param name="lastHeartbeat">Timestamp of the last heartbeat.</param>
    /// <param name="now">Current time.</param>
    public bool IsExpired(DateTimeOffset lastHeartbeat, DateTimeOffset now)
        => (now - lastHeartbeat) > Timeout;
}

/// <summary>
/// Timeout policy governing maximum execution duration for jobs.
/// </summary>
public sealed record JobTimeoutPolicy
{
    /// <summary>
    /// Default timeout policy: 1 hour maximum execution time.
    /// </summary>
    public static readonly JobTimeoutPolicy Default = new()
    {
        MaxDuration = TimeSpan.FromHours(1)
    };

    /// <summary>
    /// Long-running timeout policy: 24 hours for ETL or large geoprocessing workloads.
    /// </summary>
    public static readonly JobTimeoutPolicy LongRunning = new()
    {
        MaxDuration = TimeSpan.FromHours(24)
    };

    /// <summary>
    /// Maximum wall-clock duration for job execution. The job is marked as failed
    /// if it exceeds this duration.
    /// </summary>
    public TimeSpan MaxDuration { get; init; } = TimeSpan.FromHours(1);

    /// <summary>
    /// Returns true when the job has exceeded the maximum execution duration.
    /// </summary>
    /// <param name="startedAt">When the job execution started.</param>
    /// <param name="now">Current time.</param>
    public bool IsExpired(DateTimeOffset startedAt, DateTimeOffset now)
        => (now - startedAt) > MaxDuration;
}
