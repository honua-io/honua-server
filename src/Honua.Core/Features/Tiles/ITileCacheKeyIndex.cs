// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Honua.Core.Features.Tiles;

/// <summary>
/// A cache-index snapshot together with whether the backing index was actually readable.
/// An available empty snapshot means no tiles are tracked; an unavailable snapshot must not be
/// interpreted as an empty cache by destructive lifecycle operations.
/// </summary>
/// <param name="Entries">The entries read from the index.</param>
/// <param name="IsAvailable">Whether the backing index was read successfully.</param>
public readonly record struct TileCacheIndexSnapshot(
    IReadOnlyList<TileCacheEntry> Entries,
    bool IsAvailable);

/// <summary>
/// Live tile-cache key index that backs the size-quota / LRU evictor (#1917). Each cached tile on
/// the hot serve path records its key, byte size, and last-access time here so the evictor can later
/// snapshot the index and ask <see cref="TileCacheQuotaPolicy" /> which least-recently-used keys to
/// drop. The canonical binding is a Redis sorted set keyed by last-access (the previous binding relied
/// only on the Redis <c>maxmemory-policy allkeys-lru</c> server setting, which evicts blindly across
/// the whole keyspace rather than honoring Honua's per-tileset quotas).
/// </summary>
/// <remarks>
/// Implementations must be safe to call from the hot tile-serve path. Access records are
/// fire-and-forget bookkeeping, so their failures must never fail the tile request. Write records
/// also advance lifecycle fencing and expiration state, so implementations surface those failures
/// to the cache-write boundary. The <see cref="NullTileCacheKeyIndex" /> no-op is registered when
/// Redis is not configured, keeping the baseline serve path allocation-free and unchanged.
/// </remarks>
public interface ITileCacheKeyIndex
{
    /// <summary>
    /// Whether the index is live (a backing store is available). When
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
    /// Records a newly written tile, advances its write generation, and clears any
    /// explicit-expiration marker for the key. Durable implementations surface failures so the
    /// cache-write boundary can treat an uncommitted lifecycle update as a failed cache write.
    /// </summary>
    /// <param name="key">The tile cache key (the storage object key).</param>
    /// <param name="sizeBytes">The stored tile size in bytes.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    Task RecordWriteAsync(string key, long sizeBytes, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns whether an operator explicitly expired the key. The tile read path treats an
    /// expired key as a miss even while its bytes remain in object storage.
    /// </summary>
    /// <param name="key">The tile cache key (the storage object key).</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    Task<bool> IsExpiredAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>
    /// Marks a key expired without deleting its stored bytes. A subsequent successful cache write
    /// clears the marker through <see cref="RecordWriteAsync"/>.
    /// </summary>
    /// <param name="key">The tile cache key (the storage object key).</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns><see langword="true"/> when this call added a new marker; otherwise <see langword="false"/>.</returns>
    Task<bool> MarkExpiredAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>
    /// Snapshots the current index as a set of <see cref="TileCacheEntry" /> records for the evictor
    /// to feed into <see cref="TileCacheQuotaPolicy.SelectEvictions" />.
    /// </summary>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The current cache key index; empty when the index is disabled or unavailable.</returns>
    Task<IReadOnlyList<TileCacheEntry>> SnapshotAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Snapshots the index while preserving the distinction between an empty index and an
    /// unavailable backing store. Implementations that cannot fail independently of
    /// <see cref="SnapshotAsync"/> inherit the successful default adapter.
    /// </summary>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The snapshot and its availability state.</returns>
    async Task<TileCacheIndexSnapshot> SnapshotWithStatusAsync(
        CancellationToken cancellationToken = default)
        => new(
            await SnapshotAsync(cancellationToken).ConfigureAwait(false),
            IsAvailable: true);

    /// <summary>
    /// Removes a key from the index after the corresponding tile has been deleted from the cache
    /// store, so the index does not retain a phantom entry.
    /// </summary>
    /// <param name="key">The tile cache key to remove.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    Task RemoveAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes the snapshotted index entry only if no successful cache write has replaced it.
    /// This prevents a lifecycle delete from erasing bookkeeping for an object regenerated after
    /// the storage deletion but before the index mutation.
    /// </summary>
    /// <param name="entry">The exact entry observed in the lifecycle snapshot.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns><see langword="true"/> when the observed entry was removed; otherwise false.</returns>
    async Task<bool> TryRemoveAsync(
        TileCacheEntry entry,
        CancellationToken cancellationToken = default)
    {
        await RemoveAsync(entry.Key, cancellationToken).ConfigureAwait(false);
        return true;
    }
}
