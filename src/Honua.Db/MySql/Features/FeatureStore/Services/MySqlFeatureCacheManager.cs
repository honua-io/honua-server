// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.Infrastructure.Monitoring;
using Honua.Db.MySql.Features.Infrastructure;

namespace Honua.Db.MySql.Features.FeatureStore.Services;

internal sealed class MySqlFeatureCacheManager : IFeatureCacheManager
{
    private readonly MySqlLayerMappingRegistry _layerRegistry;

    public MySqlFeatureCacheManager(MySqlLayerMappingRegistry layerRegistry)
    {
        _layerRegistry = layerRegistry ?? throw new ArgumentNullException(nameof(layerRegistry));
    }

    public Task<int?> GetLayerSridAsync(int layerId, CancellationToken cancellationToken)
        => Task.FromResult<int?>(_layerRegistry.GetMapping(layerId)?.Srid);

    public Task<GeometryStorageType> GetGeometryStorageTypeAsync(CancellationToken cancellationToken)
        => Task.FromResult(GeometryStorageType.Geometry);

    public Task<bool> IsLayerCatalogAvailableAsync(CancellationToken cancellationToken)
        => Task.FromResult(true);

    public void RecordQueryMetrics(string operationType, long executionTimeMs, int? resultCount = null)
    {
        // Telemetry is handled via IPerformanceMonitor at the data access layer.
    }

    public Dictionary<string, DatabaseOperationMetricsSnapshot> GetPerformanceStatistics()
        => new();

    public void CleanupExpiredCacheEntries()
    {
        // Read-only provider; no runtime cache eviction needed.
    }

    public void InvalidateLayerCache(int layerId)
    {
        // Read-only provider; configuration-driven catalog does not change.
    }
}
