// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.Infrastructure.Caching;
using Honua.Core.Features.Infrastructure.Monitoring;

namespace Honua.Postgres.Features.Infrastructure.Monitoring;

/// <summary>
/// Provides database performance metrics backed by Postgres feature store instrumentation.
/// </summary>
internal sealed class PostgresDatabasePerformanceMetricsProvider : IDatabasePerformanceMetricsProvider
{
    private readonly IFeatureCacheManager _cacheManager;
    private readonly IPreparedStatementCacheStatisticsProvider _statementCacheProvider;

    public PostgresDatabasePerformanceMetricsProvider(
        IFeatureCacheManager cacheManager,
        IPreparedStatementCacheStatisticsProvider statementCacheProvider)
    {
        _cacheManager = cacheManager ?? throw new ArgumentNullException(nameof(cacheManager));
        _statementCacheProvider = statementCacheProvider ?? throw new ArgumentNullException(nameof(statementCacheProvider));
    }

    public DatabasePerformanceMetricsSnapshot GetMetrics()
    {
        var operations = _cacheManager.GetPerformanceStatistics();
        var cacheStats = _statementCacheProvider.GetStatistics();

        return new DatabasePerformanceMetricsSnapshot
        {
            CacheHitRate = cacheStats.HitRatio,
            CacheHits = cacheStats.CacheHits,
            CacheMisses = cacheStats.CacheMisses,
            Operations = new Dictionary<string, DatabaseOperationMetricsSnapshot>(operations, StringComparer.OrdinalIgnoreCase)
        };
    }
}
