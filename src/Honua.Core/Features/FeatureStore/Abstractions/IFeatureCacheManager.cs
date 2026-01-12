// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Monitoring;

namespace Honua.Core.Features.FeatureStore.Abstractions;

/// <summary>
/// Abstraction for caching layer metadata and performance optimization
/// </summary>
internal interface IFeatureCacheManager
{
    /// <summary>
    /// Gets the SRID for a layer, using cache when available
    /// </summary>
    Task<int?> GetLayerSridAsync(int layerId, CancellationToken cancellationToken);

    /// <summary>
    /// Gets the geometry storage type for the feature table
    /// </summary>
    Task<GeometryStorageType> GetGeometryStorageTypeAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Checks if layer catalog is available in the database
    /// </summary>
    Task<bool> IsLayerCatalogAvailableAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Records query performance metrics
    /// </summary>
    void RecordQueryMetrics(string operationType, long executionTimeMs, int? resultCount = null);

    /// <summary>
    /// Gets current performance statistics
    /// </summary>
    Dictionary<string, DatabaseOperationMetricsSnapshot> GetPerformanceStatistics();

    /// <summary>
    /// Clears expired cache entries
    /// </summary>
    void CleanupExpiredCacheEntries();
}

/// <summary>
/// Geometry storage types supported by PostgreSQL
/// </summary>
internal enum GeometryStorageType
{
    /// <summary>PostGIS geometry type</summary>
    Geometry,

    /// <summary>PostGIS geography type</summary>
    Geography,

    /// <summary>Raw bytea storage</summary>
    Bytea
}
