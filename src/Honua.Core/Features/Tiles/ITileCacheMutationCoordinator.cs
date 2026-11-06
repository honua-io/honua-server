// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Tiles;

public enum TileCacheExpirationMarkResult
{
    NotCurrent,

    AlreadyMarked,

    Added
}

public readonly record struct TileCacheWriteRegistration(
    long SizeBytes,
    DateTimeOffset ExpiresAt,
    string? TenantScope,
    string WriteVersion);

// Compensation and write registration must use the lease-owned callbacks. An uncancelled token does not prove this mutation still owns the distributed lease.
public readonly record struct TileCacheMutationContext(
    CancellationToken CancellationToken,
    CancellationToken LeaseLostToken,
    Func<CancellationToken, Task<bool>>? TryRemoveIndexIfLeaseOwnedAsync = null,
    Func<TileCacheWriteRegistration, CancellationToken, Task<bool>>? TryRecordWriteIfLeaseOwnedAsync = null);

// The same fence covers the storage mutation and its cache-index update.
public interface ITileCacheMutationCoordinator
{
    Task ExecuteSerializedAsync(
        string key,
        Func<TileCacheMutationContext, Task> mutation,
        CancellationToken cancellationToken = default);

    Task<bool> IsCurrentAsync(
        TileCacheEntry entry,
        CancellationToken cancellationToken = default);

    // The expiration marker is added only while this write is still current, atomically with that check, so pruning cannot leave an orphan marker.
    Task<TileCacheExpirationMarkResult> TryMarkExpiredIfCurrentAsync(
        TileCacheEntry entry,
        CancellationToken cancellationToken = default);
}
