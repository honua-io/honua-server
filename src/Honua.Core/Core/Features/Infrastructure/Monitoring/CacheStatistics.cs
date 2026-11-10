// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Infrastructure.Monitoring;

public sealed class CacheStatistics
{
    public int LayerSridCacheSize { get; init; }

    public int GeometryStorageCacheSize { get; init; }

    public int LayerCatalogCacheSize { get; init; }

    public int ActiveLockCount { get; init; }

    public int MaxLayerSridCacheEntries { get; init; }

    public string? CacheIdentity { get; init; }

    public double CacheUtilizationRatio => MaxLayerSridCacheEntries > 0
        ? (double)LayerSridCacheSize / MaxLayerSridCacheEntries
        : 0.0;

    public bool IsNearCapacity => CacheUtilizationRatio > 0.8;
}
