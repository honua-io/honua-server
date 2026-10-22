// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Alerts;

/// <summary>
/// Evaluation and retry clock. Elapsed time follows <see cref="TimeProvider.GetTimestamp"/>,
/// so a wall-clock step does not shorten a cooldown or schedule a retry in the past.
/// </summary>
internal sealed class AlertMonotonicClock
{
    private readonly TimeProvider _time;
    private readonly long _originTimestamp;
    private readonly DateTimeOffset _originUtc;

    public AlertMonotonicClock(TimeProvider? time = null)
    {
        _time = time ?? TimeProvider.System;
        _originTimestamp = _time.GetTimestamp();
        _originUtc = _time.GetUtcNow().ToUniversalTime();
    }

    public static AlertMonotonicClock System { get; } = new(TimeProvider.System);

    public DateTimeOffset GetUtcNow()
    {
        var elapsed = _time.GetElapsedTime(_originTimestamp);
        if (elapsed < TimeSpan.Zero)
        {
            elapsed = TimeSpan.Zero;
        }

        return _originUtc + elapsed;
    }
}
