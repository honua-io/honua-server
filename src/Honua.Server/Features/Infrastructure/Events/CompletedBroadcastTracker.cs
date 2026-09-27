// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;

namespace Honua.Infrastructure.Events;

/// <summary>
/// Retains fallback broadcast deduplication IDs for the same lifetime as their Redis keys.
/// The retry queue's recovery loop sweeps expired IDs even when no new events arrive.
/// </summary>
internal sealed class CompletedBroadcastTracker(TimeSpan retention, TimeProvider? timeProvider = null)
{
    private readonly ConcurrentDictionary<string, DateTimeOffset> _completed = new(StringComparer.Ordinal);
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    internal int Count => _completed.Count;

    internal void MarkCompleted(string eventId)
        => _completed[eventId] = _timeProvider.GetUtcNow().Add(retention);

    internal bool IsCompleted(string eventId)
    {
        while (_completed.TryGetValue(eventId, out var expiresAt))
        {
            if (expiresAt > _timeProvider.GetUtcNow())
            {
                return true;
            }

            // Remove only the expired version; a concurrent completion may have renewed it.
            // If removal loses that race, recheck the new deadline before allowing a broadcast.
            if (_completed.TryRemove(new KeyValuePair<string, DateTimeOffset>(eventId, expiresAt)))
            {
                return false;
            }
        }

        return false;
    }

    internal void RemoveExpired()
    {
        var now = _timeProvider.GetUtcNow();
        foreach (var entry in _completed)
        {
            if (entry.Value <= now)
            {
                _completed.TryRemove(entry);
            }
        }
    }

    internal void Clear() => _completed.Clear();
}
