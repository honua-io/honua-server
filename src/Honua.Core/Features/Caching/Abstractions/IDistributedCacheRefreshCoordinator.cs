// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Caching.Abstractions;

/// <summary>
/// Distributed cache refresh coordinator that manages cross-instance coordination via Redis.
/// </summary>
public interface IDistributedCacheRefreshCoordinator : ICacheRefreshCoordinator
{
    /// <summary>
    /// Returns true if this instance is using distributed coordination (Redis available).
    /// </summary>
    bool IsDistributed { get; }

    /// <summary>
    /// Invalidate a cache key across all instances in the cluster.
    /// </summary>
    /// <param name="key">Cache key to invalidate cluster-wide</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task NotifyInvalidationClusterWideAsync(string key, CancellationToken cancellationToken = default);
}