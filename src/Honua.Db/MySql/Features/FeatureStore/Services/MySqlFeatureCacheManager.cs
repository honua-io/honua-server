// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.Infrastructure.Monitoring;
using Honua.MySql.Features.Infrastructure;

namespace Honua.MySql.Features.FeatureStore.Services;

/// <summary>
/// MySQL/MariaDB cache manager. Provides per-layer SRID lookup from the registry;
/// no runtime caching is performed because the configuration-driven catalog is static.
/// </summary>
internal sealed class MySqlFeatureCacheManager : IFeatureCacheManager
{
    private readonly MySqlLayerMappingRegistry _layerRegistry;

    public MySqlFeatureCacheManager(MySqlLayerMappingRegistry layerRegistry)
    {
        _layerRegistry = layerRegistry ?? throw new ArgumentNullException(nameof(layerRegistry));
    }

    /// <inheritdoc />
    public Task<int?> GetLayerSridAsync(int layerId, CancellationToken cancellationToken)
        => Task.FromResult<int?>(_layerRegistry.GetMapping(layerId)?.Srid);

    /// <inheritdoc />
    public Task<GeometryStorageType> GetGeometryStorageTypeAsync(CancellationToken cancellationToken)
        => Task.FromResult(GeometryStorageType.Geometry);

    /// <inheritdoc />
    public Task<bool> IsLayerCatalogAvailableAsync(CancellationToken cancellationToken)
        => Task.FromResult(true);

    /// <inheritdoc />
    public void RecordQueryMetrics(string operationType, long executionTimeMs, int? resultCount = null)
    {
        // Telemetry is handled via IPerformanceMonitor at the data access layer.
    }

    /// <inheritdoc />
    public Dictionary<string, DatabaseOperationMetricsSnapshot> GetPerformanceStatistics()
        => new();

    /// <inheritdoc />
    public void CleanupExpiredCacheEntries()
    {
        // Read-only provider; no runtime cache eviction needed.
    }

    /// <inheritdoc />
    public void InvalidateLayerCache(int layerId)
    {
        // Read-only provider; configuration-driven catalog does not change.
    }
}
