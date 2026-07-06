// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using Honua.Core.Features.Caching;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Microsoft.Extensions.Options;

namespace Honua.Core.Features.Metadata.Caching;

/// <summary>
/// Process-wide, per-environment cache of the current Metadata v2 graph snapshot. Registered as a
/// singleton so a resolved snapshot survives across request scopes; the backing store is scoped and
/// otherwise re-reads the full catalog document (a whole-JSONB <c>SELECT</c> plus a full deserialize
/// and index rebuild) on every call. The snapshot is immutable for its revision, so sharing the
/// object reference across threads and requests is safe.
/// </summary>
/// <remarks>
/// Staleness is bounded by a short, configurable TTL (<see cref="CacheOptions.MetadataGraphTtl"/>);
/// this is what keeps multi-node deployments correct because a catalog write on one node does not
/// reach another node's in-process cache. Same-node writes call <see cref="Invalidate(string)"/> for
/// immediate freshness. Concurrent misses are coalesced (single-flight) so a TTL expiry under load
/// triggers a single reload rather than a stampede.
/// </remarks>
public sealed class MetadataV2GraphSnapshotCache : IMetadataV2GraphCacheInvalidator
{
    private readonly ConcurrentDictionary<string, CacheSlot> _slots = new(StringComparer.Ordinal);
    private readonly IOptions<CacheOptions> _options;
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Initializes a new instance of the <see cref="MetadataV2GraphSnapshotCache"/> class.
    /// </summary>
    /// <param name="options">Cache options supplying the enablement flag and TTL.</param>
    /// <param name="timeProvider">Time source used for TTL expiry (injected for testability).</param>
    public MetadataV2GraphSnapshotCache(IOptions<CacheOptions> options, TimeProvider? timeProvider = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Returns the cached snapshot for the environment when fresh, otherwise loads it through
    /// <paramref name="load"/>, caches it for the configured TTL, and returns it. When caching is
    /// disabled or the TTL is non-positive the loader is invoked directly with no caching.
    /// </summary>
    /// <param name="environment">The metadata environment key.</param>
    /// <param name="load">Loader that reads the current snapshot from the backing store.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
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
            return await fresh.Snapshot.ConfigureAwait(false);
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
                snapshotTask = LoadAndCacheAsync(environment, slot, load, cancellationToken);
                slot.State = new SlotState(snapshotTask, expiresAt);
            }
        }

        return await snapshotTask.ConfigureAwait(false);
    }

    /// <inheritdoc />
    public void Invalidate(string environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        _slots.TryRemove(environment, out _);
    }

    /// <inheritdoc />
    public void InvalidateAll() => _slots.Clear();

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
        Func<CancellationToken, ValueTask<MetadataV2GraphSnapshot>> load,
        CancellationToken cancellationToken)
    {
        try
        {
            return await load(cancellationToken).ConfigureAwait(false);
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

    private sealed class CacheSlot
    {
        public object Gate { get; } = new();

        // Immutable state swapped atomically; reads on the fast path see either the old or new
        // reference, never a torn value.
        public volatile SlotState? State;
    }

    private sealed record SlotState(Task<MetadataV2GraphSnapshot> Snapshot, long ExpiresAtTicks);
}
