// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Monitoring;
using Honua.Postgres.Features.FeatureStore;

namespace Honua.Postgres.Features.Infrastructure.Monitoring;

/// <summary>
/// Provides database performance metrics backed by Postgres feature store instrumentation.
/// </summary>
internal sealed class PostgresDatabasePerformanceMetricsProvider : IDatabasePerformanceMetricsProvider
{
    private const string CountSuffix = "_count";
    private const string AvgSuffix = "_avg_ms";
    private const string MaxSuffix = "_max_ms";
    private const string TotalSuffix = "_total_ms";

    public DatabasePerformanceMetricsSnapshot GetMetrics()
    {
        var metrics = PostgresFeatureStore.PerformanceMetrics.GetMetrics();
        var operations = new Dictionary<string, DatabaseOperationMetricsSnapshot>(StringComparer.OrdinalIgnoreCase);

        foreach (var (key, _) in metrics)
        {
            if (!key.EndsWith(CountSuffix, StringComparison.Ordinal))
            {
                continue;
            }

            var operationType = key[..^CountSuffix.Length];

            operations[operationType] = new DatabaseOperationMetricsSnapshot
            {
                Count = GetLong(metrics, key),
                TotalTimeMs = GetLong(metrics, $"{operationType}{TotalSuffix}"),
                MaxTimeMs = GetLong(metrics, $"{operationType}{MaxSuffix}"),
                AvgTimeMs = GetDouble(metrics, $"{operationType}{AvgSuffix}")
            };
        }

        return new DatabasePerformanceMetricsSnapshot
        {
            CacheHitRate = GetDouble(metrics, "cache_hit_rate"),
            CacheHits = GetLong(metrics, "cache_hits"),
            CacheMisses = GetLong(metrics, "cache_misses"),
            Operations = operations
        };
    }

    private static long GetLong(Dictionary<string, object> metrics, string key)
    {
        if (!metrics.TryGetValue(key, out var value) || value == null)
        {
            return 0L;
        }

        return value switch
        {
            long longValue => longValue,
            int intValue => intValue,
            double doubleValue => (long)doubleValue,
            float floatValue => (long)floatValue,
            _ => 0L
        };
    }

    private static double GetDouble(Dictionary<string, object> metrics, string key)
    {
        if (!metrics.TryGetValue(key, out var value) || value == null)
        {
            return 0.0;
        }

        return value switch
        {
            double doubleValue => doubleValue,
            float floatValue => floatValue,
            long longValue => longValue,
            int intValue => intValue,
            _ => 0.0
        };
    }
}
