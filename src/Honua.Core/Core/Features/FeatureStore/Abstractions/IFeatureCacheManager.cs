// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Monitoring;

namespace Honua.Core.Features.FeatureStore.Abstractions;

public interface IFeatureCacheManager
{
    Task<int?> GetLayerSridAsync(int layerId, CancellationToken cancellationToken);

    Task<GeometryStorageType> GetGeometryStorageTypeAsync(CancellationToken cancellationToken);

    Task<bool> IsLayerCatalogAvailableAsync(CancellationToken cancellationToken);

    void RecordQueryMetrics(string operationType, long executionTimeMs, int? resultCount = null);

    Dictionary<string, DatabaseOperationMetricsSnapshot> GetPerformanceStatistics();

    void CleanupExpiredCacheEntries();

    void InvalidateLayerCache(int layerId);
}

public enum GeometryStorageType
{
    Geometry,

    Geography,

    Bytea
}
