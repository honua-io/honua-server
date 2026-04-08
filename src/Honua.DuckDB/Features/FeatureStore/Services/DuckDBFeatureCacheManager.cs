// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.Infrastructure.Monitoring;
using Honua.DuckDB.Features.Infrastructure;

namespace Honua.DuckDB.Features.FeatureStore.Services;

/// <summary>
/// DuckDB implementation of the feature cache manager.
/// DuckDB uses native geometry type (not Geography/Bytea), so storage type is always Geometry.
/// Since data is read-only, caches are populated on first access and never evicted.
/// </summary>
internal sealed class DuckDBFeatureCacheManager : IFeatureCacheManager
{
    private readonly DuckDBLayerRegistry _layerRegistry;

    public DuckDBFeatureCacheManager(DuckDBLayerRegistry layerRegistry)
    {
        _layerRegistry = layerRegistry ?? throw new ArgumentNullException(nameof(layerRegistry));
    }

    /// <inheritdoc />
    public Task<int?> GetLayerSridAsync(int layerId, CancellationToken cancellationToken)
    {
        var mapping = _layerRegistry.GetMapping(layerId);
        return Task.FromResult<int?>(mapping?.Srid);
    }

    /// <inheritdoc />
    public Task<GeometryStorageType> GetGeometryStorageTypeAsync(CancellationToken cancellationToken)
    {
        // DuckDB spatial extension uses native GEOMETRY type
        return Task.FromResult(GeometryStorageType.Geometry);
    }

    /// <inheritdoc />
    public Task<bool> IsLayerCatalogAvailableAsync(CancellationToken cancellationToken)
    {
        return Task.FromResult(true);
    }

    /// <inheritdoc />
    public void RecordQueryMetrics(string operationType, long executionTimeMs, int? resultCount = null)
    {
        // Telemetry is handled via IPerformanceMonitor at the data access layer
    }

    /// <inheritdoc />
    public Dictionary<string, DatabaseOperationMetricsSnapshot> GetPerformanceStatistics()
    {
        return new Dictionary<string, DatabaseOperationMetricsSnapshot>();
    }

    /// <inheritdoc />
    public void CleanupExpiredCacheEntries()
    {
        // Read-only provider; no cache eviction needed
    }

    /// <inheritdoc />
    public void InvalidateLayerCache(int layerId)
    {
        // Read-only provider; configuration-driven catalog does not change
    }
}
