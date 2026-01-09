// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.Infrastructure.Monitoring;

namespace Honua.Postgres.Features.Infrastructure.Monitoring;

/// <summary>
/// Provides database performance metrics backed by Postgres feature store instrumentation.
/// </summary>
internal sealed class PostgresDatabasePerformanceMetricsProvider : IDatabasePerformanceMetricsProvider
{
    private readonly IFeatureCacheManager _cacheManager;

    public PostgresDatabasePerformanceMetricsProvider(IFeatureCacheManager cacheManager)
    {
        _cacheManager = cacheManager ?? throw new ArgumentNullException(nameof(cacheManager));
    }

    public DatabasePerformanceMetricsSnapshot GetMetrics()
    {
        var operations = _cacheManager.GetPerformanceStatistics();

        return new DatabasePerformanceMetricsSnapshot
        {
            CacheHitRate = 0,
            CacheHits = 0,
            CacheMisses = 0,
            Operations = new Dictionary<string, DatabaseOperationMetricsSnapshot>(operations, StringComparer.OrdinalIgnoreCase)
        };
    }
}
