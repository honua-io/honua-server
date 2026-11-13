// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Concurrent;
using Honua.Core.Features.Caching;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Microsoft.Extensions.Options;

namespace Honua.Core.Features.Metadata.Caching;

public sealed class MetadataV2GraphSnapshotCache : IMetadataV2GraphCacheInvalidator
{
    private readonly ConcurrentDictionary<string, CacheSlot> _slots = new(StringComparer.Ordinal);
    private readonly IOptions<CacheOptions> _options;
    private readonly object _generationGate = new();
    private readonly Dictionary<string, GenerationPartition> _generations = new(StringComparer.Ordinal);
    private readonly TimeProvider _timeProvider;

    public MetadataV2GraphSnapshotCache(IOptions<CacheOptions> options, TimeProvider? timeProvider = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    // Only the newest partition remains reachable; in-flight readers may finish with their captured generation.
    // A delayed probe cannot move a process back to an older generation or grow the cache without bound.
    public MetadataV2GraphSnapshotCache ForGeneration(string environment, long generation)
    {
        lock (_generationGate)
        {
            if (!_generations.TryGetValue(environment, out var partition) || generation > partition.Generation)
            {
                partition = new GenerationPartition(generation, new MetadataV2GraphSnapshotCache(_options, _timeProvider));
                _generations[environment] = partition;
            }
            return partition.Cache;
        }
    }

    public async ValueTask<MetadataV2GraphSnapshot> GetOrLoadAsync(
        string environment,
        Func<CancellationToken, ValueTask<MetadataV2GraphSnapshot>> load,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(load);

        var options = _options.Value;
        var ttl = options.MetadataGraphTtl;
        if (!options.MetadataGraphCacheEnabled || ttl <= TimeSpan.Zero)
        {
            return await load(cancellationToken).ConfigureAwait(false);
        }

        // Fast path: a single volatile read of the slot state — no lock, torn-read-free.
        if (_slots.TryGetValue(environment, out var existing)
            && existing.State is { } fresh
            && !IsExpired(fresh, _timeProvider.GetTimestamp()))
        {
            return await fresh.Snapshot.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        // Slow path: coalesce concurrent reloads for this environment under a per-slot lock so a
        // TTL expiry under load triggers a single store read instead of a stampede.
        Task<MetadataV2GraphSnapshot> snapshotTask;
        var slot = _slots.GetOrAdd(environment, static _ => new CacheSlot());
        lock (slot.Gate)
        {
            var now = _timeProvider.GetTimestamp();
            if (slot.State is { } current && !IsExpired(current, now))
            {
                snapshotTask = current.Snapshot;
            }
            else
            {
                var expiresAt = now + (long)(ttl.TotalSeconds * _timeProvider.TimestampFrequency);
                snapshotTask = LoadAndCacheAsync(environment, slot, load);
                slot.State = new SlotState(snapshotTask, expiresAt);
            }
        }

        return await snapshotTask.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public void Invalidate(string environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        _slots.TryRemove(environment, out _);
        lock (_generationGate)
        {
            if (_generations.TryGetValue(environment, out var partition)) { partition.Cache.Invalidate(environment); }
        }
    }

    public void InvalidateAll()
    {
        _slots.Clear();
        lock (_generationGate) { _generations.Clear(); }
    }

    public bool IsEnabled
    {
        get
        {
            var options = _options.Value;
            return options.MetadataGraphCacheEnabled && options.MetadataGraphTtl > TimeSpan.Zero;
        }
    }

    public void InvalidateIfCurrent(string environment, string staleEtag)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(staleEtag);
        if (!_slots.TryGetValue(environment, out var slot))
        {
            return;
        }

        lock (slot.Gate)
        {
            if (slot.State is { Snapshot: { IsCompletedSuccessfully: true } snapshot }
                && string.Equals(snapshot.Result.Etag, staleEtag, StringComparison.Ordinal))
            {
                slot.State = null;
            }
        }
    }

    // The last snapshot this node loaded, kept until it is invalidated even after its lifetime ends, so a caller can
    // ask the database whether it is still current instead of reloading it.
    public bool TryGetExpired(string environment, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out MetadataV2GraphSnapshot? snapshot)
    {
        ArgumentNullException.ThrowIfNull(environment);
        snapshot = null;
        if (!_slots.TryGetValue(environment, out var slot)
            || slot.State is not { Snapshot: { IsCompletedSuccessfully: true } loaded } state
            || !IsExpired(state, _timeProvider.GetTimestamp()))
        {
            return false;
        }

        snapshot = loaded.Result;
        return true;
    }

    // The database confirmed this revision is still current: keep serving it for another lifetime.
    public void Renew(string environment, string currentEtag)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(currentEtag);
        var ttl = _options.Value.MetadataGraphTtl;
        if (ttl <= TimeSpan.Zero || !_slots.TryGetValue(environment, out var slot))
        {
            return;
        }

        lock (slot.Gate)
        {
            if (slot.State is { Snapshot: { IsCompletedSuccessfully: true } snapshot } state
                && string.Equals(snapshot.Result.Etag, currentEtag, StringComparison.Ordinal))
            {
                var now = _timeProvider.GetTimestamp();
                slot.State = state with { ExpiresAtTicks = now + (long)(ttl.TotalSeconds * _timeProvider.TimestampFrequency) };
            }
        }
    }

    private static bool IsExpired(SlotState state, long nowTicks)
    {
        if (nowTicks >= state.ExpiresAtTicks)
        {
            return true;
        }

        // A faulted or cancelled load must never be served; treat it as expired so the next
        // access rebuilds it.
        var snapshot = state.Snapshot;
        return snapshot.IsFaulted || snapshot.IsCanceled;
    }

    private async Task<MetadataV2GraphSnapshot> LoadAndCacheAsync(
        string environment,
        CacheSlot slot,
        Func<CancellationToken, ValueTask<MetadataV2GraphSnapshot>> load)
    {
        try
        {
            // A coalesced load belongs to the cache, not to whichever request won the race.
            // Individual callers can cancel their own WaitAsync without cancelling other waiters.
            return await load(CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // Never cache a failed load: drop the slot so the next caller retries the store
            // rather than replaying the exception for the whole TTL window.
            lock (slot.Gate)
            {
                slot.State = null;
            }

            _slots.TryRemove(new KeyValuePair<string, CacheSlot>(environment, slot));
            throw;
        }
    }

    private sealed record GenerationPartition(long Generation, MetadataV2GraphSnapshotCache Cache);

    private sealed class CacheSlot
    {
        public object Gate { get; } = new();

        // Immutable state swapped atomically; reads on the fast path see either the old or new
        // reference, never a torn value.
        public volatile SlotState? State;
    }

    private sealed record SlotState(Task<MetadataV2GraphSnapshot> Snapshot, long ExpiresAtTicks);
}
