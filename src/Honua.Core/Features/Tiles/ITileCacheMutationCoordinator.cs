// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Tiles;

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
}
