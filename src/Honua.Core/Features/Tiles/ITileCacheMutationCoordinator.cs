// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Tiles;

/// <summary>Outcome of an atomic generation-checked expiration marker write.</summary>
public enum TileCacheExpirationMarkResult
{
    /// <summary>The snapshotted entry no longer exists or was replaced.</summary>
    NotCurrent,

    /// <summary>The entry is current and already had an expiration marker.</summary>
    AlreadyMarked,

    /// <summary>The entry is current and this call added its expiration marker.</summary>
    Added
}

/// <summary>
/// Serializes object-storage mutations for one generated tile key across replicas. Implementations
/// must hold the same fence around both the storage mutation and its cache-index update.
/// </summary>
public interface ITileCacheMutationCoordinator
{
    /// <summary>Runs one mutation while holding the exclusive fence for <paramref name="key"/>.</summary>
    Task ExecuteSerializedAsync(
        string key,
        Func<CancellationToken, Task> mutation,
        CancellationToken cancellationToken = default);

    /// <summary>Returns whether a snapshotted entry is still the current successful write.</summary>
    Task<bool> IsCurrentAsync(
        TileCacheEntry entry,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds an explicit expiration marker only if <paramref name="entry"/> is still the current
    /// indexed write. Durable implementations must perform the existence/generation check and
    /// marker addition atomically so concurrent index pruning cannot create an orphan marker.
    /// </summary>
    Task<TileCacheExpirationMarkResult> TryMarkExpiredIfCurrentAsync(
        TileCacheEntry entry,
        CancellationToken cancellationToken = default);
}
