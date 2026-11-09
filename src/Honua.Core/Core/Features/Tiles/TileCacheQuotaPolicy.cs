// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System;
using System.Collections.Generic;

namespace Honua.Core.Features.Tiles;

public readonly record struct TileCacheEntry(
    string Key,
    long SizeBytes,
    DateTimeOffset LastAccessUtc,
    string? WriteVersion = null,
    string? TenantScope = null)
{
    public bool IsExplicitlyExpired { get; init; }
}

public static class TileCacheQuotaPolicy
{
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
