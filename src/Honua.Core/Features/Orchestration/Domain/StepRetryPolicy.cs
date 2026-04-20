// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Orchestration.Domain;

/// <summary>
/// Per-step retry policy with exponential backoff between attempts.
/// </summary>
public sealed record StepRetryPolicy
{
    /// <summary>
    /// Maximum number of submission attempts for the step (inclusive of the first attempt).
    /// </summary>
    public required int MaxAttempts { get; init; }

    /// <summary>
    /// Delay applied before the second attempt, in seconds.
    /// </summary>
    public required int InitialDelaySeconds { get; init; }

    /// <summary>
    /// Exponential backoff multiplier applied between attempts.
    /// </summary>
    public double BackoffMultiplier { get; init; } = 2.0;

    /// <summary>
    /// Upper bound on the computed retry delay, in seconds.
    /// </summary>
    public int MaxDelaySeconds { get; init; } = 300;

    /// <summary>
    /// Computes the delay before the given attempt number using exponential backoff.
    /// </summary>
    /// <param name="completedAttempts">
    /// Number of attempts that have already completed (1-based index of the next attempt minus one).
    /// </param>
    public TimeSpan ComputeDelay(int completedAttempts)
    {
        if (completedAttempts <= 0 || InitialDelaySeconds <= 0)
        {
            return TimeSpan.Zero;
        }

        var multiplier = BackoffMultiplier <= 0 ? 1.0 : BackoffMultiplier;
        var delay = InitialDelaySeconds * Math.Pow(multiplier, completedAttempts - 1);
        var clamped = Math.Min(delay, MaxDelaySeconds <= 0 ? delay : MaxDelaySeconds);
        return TimeSpan.FromSeconds(clamped);
    }
}
