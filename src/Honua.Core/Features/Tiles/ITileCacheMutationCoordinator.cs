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

/// <summary>Lifecycle state for a storage generation committed while holding a mutation lease.</summary>
/// <param name="SizeBytes">The stored tile size in bytes.</param>
/// <param name="ExpiresAt">The storage object's absolute expiration time.</param>
/// <param name="TenantScope">Tenant/schema scope that owns the cached object.</param>
/// <param name="WriteVersion">The authoritative storage generation, such as its provider ETag.</param>
public readonly record struct TileCacheWriteRegistration(
    long SizeBytes,
    DateTimeOffset ExpiresAt,
    string? TenantScope,
    string WriteVersion);

/// <summary>Cancellation state for one fenced tile-cache mutation.</summary>
/// <param name="CancellationToken">
/// Cancels when the caller cancels or the distributed mutation lease is lost.
/// </param>
/// <param name="LeaseLostToken">
/// Cancels only when the distributed mutation lease is no longer owned. Compensating storage
/// mutations must use this token so caller cancellation cannot interrupt cleanup, while lease
/// loss prevents cleanup from racing a newer owner.
/// </param>
/// <param name="TryRemoveIndexIfLeaseOwnedAsync">
/// Atomically removes the current key's lifecycle index state only while this mutation still owns
/// its distributed lease. Compensation must use this callback rather than treating an uncancelled
/// token as proof of ownership.
/// </param>
/// <param name="TryRecordWriteIfLeaseOwnedAsync">
/// Atomically records the uploaded storage generation only while this mutation still owns its
/// distributed lease. Storage-backed writers must prefer this callback when it is available.
/// </param>
public readonly record struct TileCacheMutationContext(
    CancellationToken CancellationToken,
    CancellationToken LeaseLostToken,
    Func<CancellationToken, Task<bool>>? TryRemoveIndexIfLeaseOwnedAsync = null,
    Func<TileCacheWriteRegistration, CancellationToken, Task<bool>>? TryRecordWriteIfLeaseOwnedAsync = null);

/// <summary>
/// Serializes object-storage mutations for one generated tile key across replicas. Implementations
/// must hold the same fence around both the storage mutation and its cache-index update.
/// </summary>
public interface ITileCacheMutationCoordinator
{
    /// <summary>Runs one mutation while holding the exclusive fence for <paramref name="key"/>.</summary>
    Task ExecuteSerializedAsync(
        string key,
        Func<TileCacheMutationContext, Task> mutation,
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
