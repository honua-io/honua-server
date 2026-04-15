// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Publishing.Domain;

/// <summary>
/// Defines how a published service is kept in sync with its upstream source.
/// Refresh is strictly one-way: the source is always authoritative and changes
/// flow from source into the published copy. Bidirectional sync and CDC are out of scope.
/// </summary>
public sealed record RefreshPolicy
{
    /// <summary>
    /// How refresh operations are triggered.
    /// </summary>
    public required RefreshMode Mode { get; init; }

    /// <summary>
    /// Suggested interval between scheduled refreshes when <see cref="Mode"/> is <see cref="RefreshMode.Scheduled"/>.
    /// </summary>
    public TimeSpan? Interval { get; init; }

    /// <summary>
    /// When the most recent successful refresh completed.
    /// </summary>
    public DateTimeOffset? LastRefreshAt { get; init; }

    /// <summary>
    /// Earliest time the next scheduled refresh should run.
    /// </summary>
    public DateTimeOffset? NextRefreshAt { get; init; }

    /// <summary>
    /// Creates a manual-only refresh policy.
    /// </summary>
    public static RefreshPolicy Manual()
        => new() { Mode = RefreshMode.Manual };

    /// <summary>
    /// Creates a scheduled refresh policy with the specified interval.
    /// </summary>
    public static RefreshPolicy Scheduled(TimeSpan interval)
    {
        var now = DateTimeOffset.UtcNow;
        return new RefreshPolicy
        {
            Mode = RefreshMode.Scheduled,
            Interval = interval,
            NextRefreshAt = now + interval
        };
    }

    /// <summary>
    /// Returns an updated policy reflecting a completed refresh, advancing the next refresh time
    /// when the policy is scheduled.
    /// </summary>
    public RefreshPolicy WithRefreshCompleted()
    {
        var now = DateTimeOffset.UtcNow;
        return this with
        {
            LastRefreshAt = now,
            NextRefreshAt = Mode == RefreshMode.Scheduled && Interval.HasValue
                ? now + Interval.Value
                : null
        };
    }
}
