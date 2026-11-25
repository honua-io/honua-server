// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Infrastructure.RateLimiting;

internal readonly record struct CounterHitOutcome
{
    public required RateLimitResult Result { get; init; }

    public required bool Evicted { get; init; }
}

internal sealed class ShardedRateLimitCounterStore
{
    private const int MaxExpiredProbeCount = 16;

    private readonly Shard[] _shards;

    internal ShardedRateLimitCounterStore(int maxEntries, int shardCount = 16)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxEntries, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(shardCount, 1);

        Capacity = maxEntries;
        var effectiveShards = Math.Min(shardCount, maxEntries);
        _shards = new Shard[effectiveShards];
        var baseCapacity = maxEntries / effectiveShards;
        var remainder = maxEntries % effectiveShards;
        for (var i = 0; i < effectiveShards; i++)
        {
            _shards[i] = new Shard(baseCapacity + (i < remainder ? 1 : 0));
        }
    }

    internal int Capacity { get; }

    internal int Count
    {
        get
        {
            var total = 0;
            for (var i = 0; i < _shards.Length; i++)
            {
                var shard = _shards[i];
                lock (shard.Gate)
                {
                    total += shard.Entries.Count;
                }
            }

            return total;
        }
    }

    internal void Clear()
    {
        for (var i = 0; i < _shards.Length; i++)
        {
            var shard = _shards[i];
            lock (shard.Gate)
            {
                shard.Entries.Clear();
                shard.Lru.Clear();
            }
        }
    }

    internal CounterHitOutcome RecordHit(
        ReadOnlySpan<char> counterKey,
        ResolvedRateLimit resolved,
        DateTimeOffset now)
    {
        var window = resolved.Window;
        var windowTicks = Math.Max(TimeSpan.TicksPerSecond, window.Ticks);
        var bucketStartTicks = now.UtcTicks / windowTicks * windowTicks;
        var windowStart = new DateTimeOffset(bucketStartTicks, TimeSpan.Zero);

        var hash = ((IAlternateEqualityComparer<ReadOnlySpan<char>, string>)StringComparer.Ordinal).GetHashCode(counterKey);
        var shard = _shards[(uint)hash % (uint)_shards.Length];
        lock (shard.Gate)
        {
            var (node, evicted) = GetOrCreateEntryNode(shard, counterKey, now);
            if (node is null)
            {
                return new CounterHitOutcome
                {
                    Evicted = false,
                    Result = new RateLimitResult
                    {
                        IsAllowed = true,
                        RequestsRemaining = resolved.Limit,
                        WindowReset = windowStart + window,
                        RequestCount = 0,
                        Limit = resolved.Limit
                    }
                };
            }

            var result = IncrementWindowCounter(node.Value, resolved, windowStart);
            return new CounterHitOutcome { Result = result, Evicted = evicted };
        }
    }

    private static (LinkedListNode<CounterEntry>? Node, bool Evicted) GetOrCreateEntryNode(
        Shard shard,
        ReadOnlySpan<char> counterKey,
        DateTimeOffset now)
    {
        var lookup = shard.Entries.GetAlternateLookup<ReadOnlySpan<char>>();
        if (lookup.TryGetValue(counterKey, out var existing))
        {
            if (existing != shard.Lru.Last)
            {
                shard.Lru.Remove(existing);
                shard.Lru.AddLast(existing);
            }

            return (existing, false);
        }

        if (shard.Entries.Count >= shard.Capacity)
        {
            PruneExpiredFromHead(shard, now);
        }

        var evicted = false;
        if (shard.Entries.Count >= shard.Capacity)
        {
            if (shard.Lru.First is not { } oldest)
            {
                return (null, false);
            }

            shard.Lru.RemoveFirst();
            shard.Entries.Remove(oldest.Value.Key);
            evicted = true;
        }

        var materializedKey = counterKey.ToString();
        var node = new LinkedListNode<CounterEntry>(new CounterEntry(materializedKey));
        shard.Lru.AddLast(node);
        shard.Entries[materializedKey] = node;
        return (node, evicted);
    }

    private static RateLimitResult IncrementWindowCounter(
        CounterEntry entry,
        ResolvedRateLimit resolved,
        DateTimeOffset windowStart)
    {
        if (entry.WindowStart != windowStart)
        {
            entry.WindowStart = windowStart;
            entry.Count = 0;
        }

        entry.WindowEnd = windowStart + resolved.Window;
        var currentCount = ++entry.Count;
        var limit = resolved.Limit;

        return new RateLimitResult
        {
            IsAllowed = currentCount <= limit,
            RequestsRemaining = Math.Max(0, limit - currentCount),
            WindowReset = entry.WindowEnd,
            RequestCount = currentCount,
            Limit = limit
        };
    }

    private static void PruneExpiredFromHead(Shard shard, DateTimeOffset now)
    {
        var current = shard.Lru.First;
        var checkedCount = 0;
        while (current is not null && checkedCount < MaxExpiredProbeCount)
        {
            var next = current.Next;
            checkedCount++;
            if (current.Value.WindowEnd <= now)
            {
                shard.Lru.Remove(current);
                shard.Entries.Remove(current.Value.Key);
            }

            current = next;
        }
    }

    private sealed class Shard(int capacity)
    {
        internal readonly object Gate = new();
        internal readonly int Capacity = capacity;
        internal readonly Dictionary<string, LinkedListNode<CounterEntry>> Entries = new(StringComparer.Ordinal);
        internal readonly LinkedList<CounterEntry> Lru = new();
    }

    private sealed class CounterEntry(string key)
    {
        internal string Key { get; } = key;
        internal DateTimeOffset WindowStart;
        internal DateTimeOffset WindowEnd;
        internal int Count;
    }
}
