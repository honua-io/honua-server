// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Concurrent;
using Honua.Core.Features.Alerts.Domain;

namespace Honua.Alerts;

// A capped dispatch is rescheduled and is not dead-lettered; the cap is per replica because every replica claims work.
internal sealed class AlertNotificationRateLimiter
{
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    private readonly ConcurrentDictionary<AlertChannelType, Queue<DateTimeOffset>> _windows = new();

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
