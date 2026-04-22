// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Honua.Postgres.Features.Admin;

/// <summary>
/// Performance benchmarking utilities for database operations.
/// Validates the effectiveness of performance optimizations.
/// </summary>
internal static class PerformanceBenchmark
{
    /// <summary>
    /// Benchmarks query performance with specified feature count threshold.
    /// Validates that queries complete within acceptable time limits.
    /// </summary>
    public static async Task<BenchmarkResult> BenchmarkFeatureQueryAsync(
        string connectionString,
        int layerId,
        int featureCount,
        TimeSpan maxAllowedTime,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        // Test basic feature query performance
        const string countSql = """
            SELECT COUNT(*)
            FROM features
            WHERE layer_id = $1;
            """;

        await using var countCommand = new NpgsqlCommand(countSql, connection);
        countCommand.Parameters.AddWithValue(layerId);
        var actualCount = (long)(await countCommand.ExecuteScalarAsync(cancellationToken) ?? 0);

        stopwatch.Stop();
        var countTime = stopwatch.Elapsed;

        // Test spatial query performance if geometry exists
        stopwatch.Restart();

        const string spatialSql = """
            SELECT COUNT(*)
            FROM features
            WHERE layer_id = $1
              AND geometry IS NOT NULL
              AND ST_IsValid(geometry);
            """;

        await using var spatialCommand = new NpgsqlCommand(spatialSql, connection);
        spatialCommand.Parameters.AddWithValue(layerId);
        var validGeometryCount = (long)(await spatialCommand.ExecuteScalarAsync(cancellationToken) ?? 0);

        stopwatch.Stop();
        var spatialTime = stopwatch.Elapsed;

        // Test attribute query performance
        stopwatch.Restart();

        const string attributeSql = """
            SELECT COUNT(*)
            FROM features
            WHERE layer_id = $1
              AND attributes IS NOT NULL
              AND jsonb_typeof(attributes) = 'object';
            """;

        await using var attributeCommand = new NpgsqlCommand(attributeSql, connection);
        attributeCommand.Parameters.AddWithValue(layerId);
        var validAttributeCount = (long)(await attributeCommand.ExecuteScalarAsync(cancellationToken) ?? 0);

        stopwatch.Stop();
        var attributeTime = stopwatch.Elapsed;

        var maxTime = new[] { countTime, spatialTime, attributeTime }.Max();
        var isPerformant = maxTime <= maxAllowedTime;

        var result = new BenchmarkResult
        {
            LayerId = layerId,
            ExpectedFeatureCount = featureCount,
            ActualFeatureCount = actualCount,
            ValidGeometryCount = validGeometryCount,
            ValidAttributeCount = validAttributeCount,
            CountQueryTime = countTime,
            SpatialQueryTime = spatialTime,
            AttributeQueryTime = attributeTime,
            MaxQueryTime = maxTime,
            IsPerformant = isPerformant,
            AllowedTime = maxAllowedTime
        };

        LogBenchmarkResult(logger, result);
        return result;
    }

    /// <summary>
    /// Benchmarks index utilization for a specific layer.
    /// Verifies that queries are using indexes effectively.
    /// </summary>
    public static async Task<IndexUtilizationResult> BenchmarkIndexUtilizationAsync(
        string connectionString,
        int layerId,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        // Check spatial index usage
        const string spatialIndexSql = """
            SELECT
                schemaname,
                tablename,
                indexname,
                idx_scan,
                idx_tup_read,
                idx_tup_fetch
            FROM pg_stat_user_indexes
            WHERE tablename = 'features'
              AND indexname LIKE '%geometry%'
            ORDER BY idx_scan DESC;
            """;

        var spatialIndexes = new List<IndexUsageInfo>();
        await using var spatialCommand = new NpgsqlCommand(spatialIndexSql, connection);
        await using var spatialReader = await spatialCommand.ExecuteReaderAsync(cancellationToken);

        while (await spatialReader.ReadAsync(cancellationToken))
        {
            spatialIndexes.Add(new IndexUsageInfo
            {
                SchemaName = spatialReader.GetString(0),
                TableName = spatialReader.GetString(1),
                IndexName = spatialReader.GetString(2),
                IndexScans = spatialReader.GetInt64(3),
                TuplesRead = spatialReader.GetInt64(4),
                TuplesFetched = spatialReader.GetInt64(5)
            });
        }

        spatialReader.Close();

        // Check attribute index usage
        const string attributeIndexSql = """
            SELECT
                schemaname,
                tablename,
                indexname,
                idx_scan,
                idx_tup_read,
                idx_tup_fetch
            FROM pg_stat_user_indexes
            WHERE tablename = 'features'
              AND indexname LIKE '%attributes%'
            ORDER BY idx_scan DESC;
            """;

        var attributeIndexes = new List<IndexUsageInfo>();
        await using var attributeCommand = new NpgsqlCommand(attributeIndexSql, connection);
        await using var attributeReader = await attributeCommand.ExecuteReaderAsync(cancellationToken);

        while (await attributeReader.ReadAsync(cancellationToken))
        {
            attributeIndexes.Add(new IndexUsageInfo
            {
                SchemaName = attributeReader.GetString(0),
                TableName = attributeReader.GetString(1),
                IndexName = attributeReader.GetString(2),
                IndexScans = attributeReader.GetInt64(3),
                TuplesRead = attributeReader.GetInt64(4),
                TuplesFetched = attributeReader.GetInt64(5)
            });
        }

        var result = new IndexUtilizationResult
        {
            LayerId = layerId,
            SpatialIndexes = spatialIndexes,
            AttributeIndexes = attributeIndexes,
            TotalSpatialScans = spatialIndexes.Sum(i => i.IndexScans),
            TotalAttributeScans = attributeIndexes.Sum(i => i.IndexScans)
        };

        LogIndexUtilization(logger, result);
        return result;
    }

    private static void LogBenchmarkResult(ILogger logger, BenchmarkResult result)
    {
        PerformanceBenchmarkLog.BenchmarkResult(
            logger,
            result.LayerId,
            result.ActualFeatureCount,
            result.ExpectedFeatureCount,
            result.CountQueryTime.TotalMilliseconds,
            result.SpatialQueryTime.TotalMilliseconds,
            result.AttributeQueryTime.TotalMilliseconds,
            result.MaxQueryTime.TotalMilliseconds,
            result.AllowedTime.TotalMilliseconds,
            result.IsPerformant);
    }

    private static void LogIndexUtilization(ILogger logger, IndexUtilizationResult result)
    {
        PerformanceBenchmarkLog.IndexUtilization(
            logger,
            result.LayerId,
            result.TotalSpatialScans,
            result.TotalAttributeScans,
            result.SpatialIndexes.Count,
            result.AttributeIndexes.Count);
    }
}

/// <summary>
/// Results of performance benchmarking for a specific layer.
/// </summary>
internal sealed record BenchmarkResult
{
    public int LayerId { get; init; }
    public int ExpectedFeatureCount { get; init; }
    public long ActualFeatureCount { get; init; }
    public long ValidGeometryCount { get; init; }
    public long ValidAttributeCount { get; init; }
    public TimeSpan CountQueryTime { get; init; }
    public TimeSpan SpatialQueryTime { get; init; }
    public TimeSpan AttributeQueryTime { get; init; }
    public TimeSpan MaxQueryTime { get; init; }
    public TimeSpan AllowedTime { get; init; }
    public bool IsPerformant { get; init; }
}

/// <summary>
/// Results of index utilization analysis.
/// </summary>
internal sealed record IndexUtilizationResult
{
    public int LayerId { get; init; }
    public IReadOnlyList<IndexUsageInfo> SpatialIndexes { get; init; } = [];
    public IReadOnlyList<IndexUsageInfo> AttributeIndexes { get; init; } = [];
    public long TotalSpatialScans { get; init; }
    public long TotalAttributeScans { get; init; }
}

/// <summary>
/// Information about database index usage.
/// </summary>
internal sealed record IndexUsageInfo
{
    public string SchemaName { get; init; } = string.Empty;
    public string TableName { get; init; } = string.Empty;
    public string IndexName { get; init; } = string.Empty;
    public long IndexScans { get; init; }
    public long TuplesRead { get; init; }
    public long TuplesFetched { get; init; }
}
