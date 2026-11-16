// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.Infrastructure.Monitoring;
using Honua.Db.DuckDB.Features.Infrastructure;

namespace Honua.Db.DuckDB.Features.FeatureStore.Services;

internal sealed class DuckDBFeatureCacheManager : IFeatureCacheManager
{
    private readonly DuckDBLayerRegistry _layerRegistry;

    public DuckDBFeatureCacheManager(DuckDBLayerRegistry layerRegistry)
    {
        _layerRegistry = layerRegistry ?? throw new ArgumentNullException(nameof(layerRegistry));
    }

    public Task<int?> GetLayerSridAsync(int layerId, CancellationToken cancellationToken)
    {
        var mapping = _layerRegistry.GetMapping(layerId);
        return Task.FromResult<int?>(mapping?.Srid);
    }

    public Task<GeometryStorageType> GetGeometryStorageTypeAsync(CancellationToken cancellationToken)
    {
        // DuckDB spatial extension uses native GEOMETRY type
        return Task.FromResult(GeometryStorageType.Geometry);
    }

    public Task<bool> IsLayerCatalogAvailableAsync(CancellationToken cancellationToken)
    {
        return Task.FromResult(true);
    }

    public void RecordQueryMetrics(string operationType, long executionTimeMs, int? resultCount = null)
    {
        // Telemetry is handled via IPerformanceMonitor at the data access layer
    }

    public Dictionary<string, DatabaseOperationMetricsSnapshot> GetPerformanceStatistics()
    {
        return new Dictionary<string, DatabaseOperationMetricsSnapshot>();
    }

    public void CleanupExpiredCacheEntries()
    {
        // Read-only provider; no cache eviction needed
    }

    public void InvalidateLayerCache(int layerId)
    {
        // Read-only provider; configuration-driven catalog does not change
    }
}
