// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Honua.Core.Features.Tiles;

/// <summary>
/// Live tile-cache key index that backs the size-quota / LRU evictor (#1917). Each cached tile on
/// the hot serve path records its key, byte size, and last-access time here so the evictor can later
/// snapshot the index and ask <see cref="TileCacheQuotaPolicy" /> which least-recently-used keys to
/// drop. The canonical binding is a Redis sorted set keyed by last-access (the previous binding relied
/// only on the Redis <c>maxmemory-policy allkeys-lru</c> server setting, which evicts blindly across
/// the whole keyspace rather than honoring Honua's per-tileset quotas).
/// </summary>
/// <remarks>
/// Implementations must be safe to call from the hot tile-serve path: index updates are
/// fire-and-forget bookkeeping, so failures (e.g. a Redis outage) must never fail the tile request.
/// The <see cref="NullTileCacheKeyIndex" /> no-op is registered when eviction is disabled or Redis is
/// not configured, keeping the baseline serve path allocation-free and unchanged.
/// </remarks>
public interface ITileCacheKeyIndex
{
    /// <summary>
    /// Whether the index is live (eviction enabled and a backing store is available). When
    /// <see langword="false" /> the hot path can skip building access records entirely.
    /// </summary>
    bool IsEnabled { get; }

    /// <summary>
    /// Records a tile-cache access: upserts the tile key with its current byte size and refreshes its
    /// last-access timestamp to now. Called on both cache writes (after a freshly rendered tile is
    /// stored) and cache hits (so reads keep hot tiles from being evicted).
    /// </summary>
    /// <param name="key">The tile cache key (the storage object key).</param>
    /// <param name="sizeBytes">The stored tile size in bytes.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    Task RecordAccessAsync(string key, long sizeBytes, CancellationToken cancellationToken = default);

    /// <summary>
    /// Snapshots the current index as a set of <see cref="TileCacheEntry" /> records for the evictor
    /// to feed into <see cref="TileCacheQuotaPolicy.SelectEvictions" />.
    /// </summary>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The current cache key index; empty when the index is disabled or unavailable.</returns>
    Task<IReadOnlyList<TileCacheEntry>> SnapshotAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes a key from the index after the corresponding tile has been deleted from the cache
    /// store, so the index does not retain a phantom entry.
    /// </summary>
    /// <param name="key">The tile cache key to remove.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    Task RemoveAsync(string key, CancellationToken cancellationToken = default);
}
