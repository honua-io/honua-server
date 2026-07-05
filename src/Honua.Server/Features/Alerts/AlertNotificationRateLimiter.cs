// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using Honua.Core.Features.Alerts.Domain;

namespace Honua.Alerts;

/// <summary>
/// In-memory, per-channel sliding-window rate limiter that caps outbound alert
/// notifications to <see cref="AlertDispatchOptions.MaxNotificationsPerMinutePerChannel"/>
/// per rolling minute. The dispatch worker checks it BEFORE calling a delivery sink;
/// a capped dispatch is rescheduled (retry budget untouched), never dead-lettered.
/// A cap of <c>0</c> disables limiting. The limiter is process-local, matching the
/// single-leader dispatcher model.
/// </summary>
internal sealed class AlertNotificationRateLimiter
{
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    private readonly ConcurrentDictionary<AlertChannelType, Queue<DateTimeOffset>> _windows = new();

    /// <summary>
    /// Attempts to admit one notification for a channel at <paramref name="now"/>.
    /// Returns <c>true</c> and records the admission when under the cap; returns
    /// <c>false</c> when the channel is at its cap for the current window. A
    /// non-positive <paramref name="maxPerMinute"/> disables the cap (always admits).
    /// </summary>
    public bool TryAcquire(AlertChannelType channelType, int maxPerMinute, DateTimeOffset now)
    {
        if (maxPerMinute <= 0)
        {
            return true;
        }

        var window = _windows.GetOrAdd(channelType, static _ => new Queue<DateTimeOffset>());
        lock (window)
        {
            var cutoff = now - Window;
            while (window.Count > 0 && window.Peek() <= cutoff)
            {
                window.Dequeue();
            }

            if (window.Count >= maxPerMinute)
            {
                return false;
            }

            window.Enqueue(now);
            return true;
        }
    }
}
