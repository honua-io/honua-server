// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Publishing.Domain;

public sealed record RefreshPolicy
{
    public required RefreshMode Mode { get; init; }

    public TimeSpan? Interval { get; init; }

    public DateTimeOffset? LastRefreshAt { get; init; }

    public DateTimeOffset? NextRefreshAt { get; init; }

    public static RefreshPolicy Manual()
        => new() { Mode = RefreshMode.Manual };

    public static RefreshPolicy Scheduled(TimeSpan interval)
    {
        if (interval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(interval),
                interval,
                "Scheduled refresh interval must be positive.");
        }

        var now = DateTimeOffset.UtcNow;
        return new RefreshPolicy
        {
            Mode = RefreshMode.Scheduled,
            Interval = interval,
            NextRefreshAt = now + interval
        };
    }

    public RefreshPolicy WithRefreshCompleted(DateTimeOffset? completedAt = null)
    {
        var now = completedAt ?? DateTimeOffset.UtcNow;
        return this with
        {
            LastRefreshAt = now,
            NextRefreshAt = Mode == RefreshMode.Scheduled && Interval.HasValue
                ? now + Interval.Value
                : null
        };
    }
}
