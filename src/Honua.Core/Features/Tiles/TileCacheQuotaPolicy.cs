// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System;
using System.Collections.Generic;

namespace Honua.Core.Features.Tiles;

/// <summary>
/// One entry in the tile-cache key index consulted by <see cref="TileCacheQuotaPolicy" /> (#1837):
/// a cache key, the stored tile byte size, and the last time the entry was read. The evictor
/// maintains these (e.g. in a Redis sorted set keyed by last-access) and asks the policy which
/// least-recently-used keys to drop when a configured size quota is exceeded.
/// </summary>
/// <param name="Key">The tile cache key.</param>
/// <param name="SizeBytes">The stored tile size in bytes.</param>
/// <param name="LastAccessUtc">The last time the entry was read (UTC).</param>
public readonly record struct TileCacheEntry(string Key, long SizeBytes, DateTimeOffset LastAccessUtc);

/// <summary>
/// Pure size-quota / LRU eviction policy for the tile cache (#1837). Given the current cache
/// footprint and the configured <see cref="TileCacheEvictionOptions" />, it returns the
/// least-recently-used keys that must be evicted to bring the cache back within the entry-count and
/// byte-size quotas. The policy performs no I/O and is a deterministic function of its inputs so it
/// can be unit-tested in isolation and reused by any cache backend (Redis tile-key index, in-memory
/// store, etc.).
/// </summary>
public static class TileCacheQuotaPolicy
{
    /// <summary>
    /// Selects the least-recently-used cache keys to evict so the cache satisfies both the
    /// entry-count and byte-size quotas in <paramref name="options" />. Returns an empty list when
    /// eviction is disabled, no quota is configured, or the cache is already within quota.
    /// </summary>
    /// <param name="entries">The current cache key index.</param>
    /// <param name="options">The eviction policy options.</param>
    /// <returns>The keys to evict, ordered least-recently-used first.</returns>
    public static IReadOnlyList<string> SelectEvictions(
        IEnumerable<TileCacheEntry> entries,
        TileCacheEvictionOptions options)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(options);

        var maxEntries = options.MaxEntries is { } me && me > 0 ? me : (long?)null;
        var maxBytes = options.MaxBytes is { } mb && mb > 0 ? mb : (long?)null;

        if (!options.Enabled || (maxEntries is null && maxBytes is null))
        {
            return [];
        }

        // Materialize and sort least-recently-used first. Ties break on key for determinism.
        var sorted = new List<TileCacheEntry>(entries);
        if (sorted.Count == 0)
        {
            return [];
        }

        sorted.Sort(static (a, b) =>
        {
            var byTime = a.LastAccessUtc.CompareTo(b.LastAccessUtc);
            return byTime != 0 ? byTime : string.CompareOrdinal(a.Key, b.Key);
        });

        long totalEntries = sorted.Count;
        long totalBytes = 0;
        foreach (var entry in sorted)
        {
            totalBytes += entry.SizeBytes;
        }

        var evicted = new List<string>();
        var index = 0;
        while (index < sorted.Count &&
               ((maxEntries is { } entryCap && totalEntries > entryCap) ||
                (maxBytes is { } byteCap && totalBytes > byteCap)))
        {
            var victim = sorted[index++];
            evicted.Add(victim.Key);
            totalEntries--;
            totalBytes -= victim.SizeBytes;
        }

        return evicted;
    }
}
