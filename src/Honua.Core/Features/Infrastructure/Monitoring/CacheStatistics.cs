// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Infrastructure.Monitoring;

/// <summary>
/// Cache statistics for monitoring memory usage and performance
/// </summary>
public sealed class CacheStatistics
{
    /// <summary>
    /// Current number of entries in the layer SRID cache
    /// </summary>
    public int LayerSridCacheSize { get; init; }

    /// <summary>
    /// Current number of entries in the geometry storage type cache
    /// </summary>
    public int GeometryStorageCacheSize { get; init; }

    /// <summary>
    /// Current number of entries in the layer catalog availability cache
    /// </summary>
    public int LayerCatalogCacheSize { get; init; }

    /// <summary>
    /// Current number of active locks (for cache stampede prevention)
    /// </summary>
    public int ActiveLockCount { get; init; }

    /// <summary>
    /// Maximum allowed entries in the layer SRID cache
    /// </summary>
    public int MaxLayerSridCacheEntries { get; init; }

    /// <summary>
    /// Cache identity for multi-schema deployments
    /// </summary>
    public string? CacheIdentity { get; init; }

    /// <summary>
    /// Cache utilization ratio (0.0 to 1.0)
    /// </summary>
    public double CacheUtilizationRatio => MaxLayerSridCacheEntries > 0
        ? (double)LayerSridCacheSize / MaxLayerSridCacheEntries
        : 0.0;

    /// <summary>
    /// Whether the cache is approaching its memory limits
    /// </summary>
    public bool IsNearCapacity => CacheUtilizationRatio > 0.8;
}