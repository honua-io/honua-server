// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Diagnostics;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Postgres.Features.FeatureStore.Internal;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Honua.Postgres.Features.FeatureStore.Services;

/// <summary>
/// Manages caching of layer metadata and performance monitoring for feature store operations
/// </summary>
internal sealed class FeatureCacheManager : IFeatureCacheManager
{
    private static readonly TimeSpan _layerSridCacheRetention = TimeSpan.FromHours(24);
    private const int MaxLayerSridCacheEntries = 10000;

    private readonly IDatabaseConnectionProvider _connectionProvider;
    private readonly ILogger<FeatureCacheManager> _logger;
    private readonly string? _tableSchema;

    private readonly ConcurrentDictionary<int, LayerSridCacheEntry> _layerSridCache = new();
    private GeometryStorageType? _geometryStorageType;
    private bool? _hasLayerCatalog;

    /// <summary>
    /// Performance metrics tracking
    /// </summary>
    private static class PerformanceMetrics
    {
        private static readonly ConcurrentDictionary<string, long> _executionCounts = new();
        private static readonly ConcurrentDictionary<string, long> _totalExecutionTimeMs = new();
        private static readonly ConcurrentDictionary<string, long> _maxExecutionTimeMs = new();
        private static readonly ConcurrentDictionary<string, long> _totalResultCounts = new();

        public static void RecordQueryExecution(string operationType, long executionTimeMs, int? resultCount = null)
        {
            _executionCounts.AddOrUpdate(operationType, 1, (key, value) => value + 1);
            _totalExecutionTimeMs.AddOrUpdate(operationType, executionTimeMs, (key, value) => value + executionTimeMs);
            _maxExecutionTimeMs.AddOrUpdate(operationType, executionTimeMs, (key, value) => Math.Max(value, executionTimeMs));

            if (resultCount.HasValue)
            {
                _totalResultCounts.AddOrUpdate(operationType, resultCount.Value, (key, value) => value + resultCount.Value);
            }
        }

        public static Dictionary<string, object> GetStatistics()
        {
            var stats = new Dictionary<string, object>();

            foreach (var (operationType, count) in _executionCounts)
            {
                var totalTime = _totalExecutionTimeMs.GetValueOrDefault(operationType, 0);
                var maxTime = _maxExecutionTimeMs.GetValueOrDefault(operationType, 0);
                var totalResults = _totalResultCounts.GetValueOrDefault(operationType, 0);

                stats[operationType] = new
                {
                    ExecutionCount = count,
                    TotalExecutionTimeMs = totalTime,
                    AverageExecutionTimeMs = count > 0 ? totalTime / (double)count : 0,
                    MaxExecutionTimeMs = maxTime,
                    TotalResults = totalResults,
                    AverageResultsPerQuery = count > 0 ? totalResults / (double)count : 0
                };
            }

            return stats;
        }
    }

    public FeatureCacheManager(IDatabaseConnectionProvider connectionProvider, ILogger<FeatureCacheManager> logger, string? schemaName = null)
    {
        _connectionProvider = connectionProvider ?? throw new ArgumentNullException(nameof(connectionProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _tableSchema = string.IsNullOrEmpty(schemaName) ? null : schemaName;
    }

    public async Task<int?> GetLayerSridAsync(int layerId, CancellationToken cancellationToken)
    {
        // Check cache first
        if (_layerSridCache.TryGetValue(layerId, out var cachedEntry) &&
            !IsLayerSridCacheExpired(cachedEntry, DateTimeOffset.UtcNow))
        {
            return cachedEntry.Srid;
        }

        await using var connection = (NpgsqlConnection)await _connectionProvider
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        var stopwatch = Stopwatch.StartNew();
        try
        {
            const string sql = """
                SELECT srid
                FROM layer_catalog
                WHERE layer_id = $1
                """;

            await using var command = new NpgsqlCommand(sql, connection);
            command.Parameters.AddWithValue(layerId);

            var result = await command.ExecuteScalarAsync(cancellationToken);
            var srid = result is int sridValue ? sridValue : (int?)null;

            // Update cache
            _layerSridCache[layerId] = new LayerSridCacheEntry(srid, DateTimeOffset.UtcNow);

            stopwatch.Stop();
            PerformanceMetrics.RecordQueryExecution("srid_lookup", stopwatch.ElapsedMilliseconds);

            // Cleanup cache if needed
            CleanupCacheIfNeeded();

            return srid;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            PerformanceMetrics.RecordQueryExecution("srid_lookup_error", stopwatch.ElapsedMilliseconds);
            MonitoredFeatureStoreLog.SridLookupFailed(_logger, layerId, ex);
            return null;
        }
    }

    public async Task<GeometryStorageType> GetGeometryStorageTypeAsync(CancellationToken cancellationToken)
    {
        if (_geometryStorageType.HasValue)
        {
            return _geometryStorageType.Value;
        }

        await using var connection = (NpgsqlConnection)await _connectionProvider
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        const string sql = """
            SELECT data_type, udt_name
            FROM information_schema.columns
            WHERE table_schema = COALESCE(@schema, current_schema())
              AND table_name = @table
              AND column_name = @column
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("schema", (object?)_tableSchema ?? DBNull.Value);
        command.Parameters.AddWithValue("table", "features");
        command.Parameters.AddWithValue("column", "geometry");

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        if (await reader.ReadAsync(cancellationToken))
        {
            var dataTypeOrdinal = reader.GetOrdinal("data_type");
            var udtNameOrdinal = reader.GetOrdinal("udt_name");
            var dataType = reader.GetString(dataTypeOrdinal);
            var udtName = reader.GetString(udtNameOrdinal);
            _geometryStorageType = ResolveGeometryStorageType(dataType, udtName);
        }
        else
        {
            _geometryStorageType = GeometryStorageType.Bytea; // Default fallback
        }

        return _geometryStorageType.Value;
    }

    public async Task<bool> IsLayerCatalogAvailableAsync(CancellationToken cancellationToken)
    {
        if (_hasLayerCatalog.HasValue)
        {
            return _hasLayerCatalog.Value;
        }

        await using var connection = (NpgsqlConnection)await _connectionProvider
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        try
        {
            const string sql = """
                SELECT EXISTS (
                    SELECT 1
                    FROM information_schema.tables
                    WHERE table_schema = COALESCE(@schema, current_schema())
                      AND table_name = 'layer_catalog'
                )
                """;

            await using var command = new NpgsqlCommand(sql, connection);
            command.Parameters.AddWithValue("schema", (object?)_tableSchema ?? DBNull.Value);

            var result = await command.ExecuteScalarAsync(cancellationToken);
            _hasLayerCatalog = (bool)result!;

            return _hasLayerCatalog.Value;
        }
        catch
        {
            _hasLayerCatalog = false;
            return false;
        }
    }

    public void RecordQueryMetrics(string operationType, long executionTimeMs, int? resultCount = null)
    {
        PerformanceMetrics.RecordQueryExecution(operationType, executionTimeMs, resultCount);
    }

    public Dictionary<string, object> GetPerformanceStatistics()
    {
        return PerformanceMetrics.GetStatistics();
    }

    public void CleanupExpiredCacheEntries()
    {
        CleanupCacheIfNeeded();
    }

    private static bool IsLayerSridCacheExpired(LayerSridCacheEntry entry, DateTimeOffset now)
    {
        return now - entry.CreatedAt > _layerSridCacheRetention;
    }

    private void CleanupCacheIfNeeded()
    {
        var now = DateTimeOffset.UtcNow;

        // Remove expired entries
        foreach (var (layerId, entry) in _layerSridCache.ToArray())
        {
            if (IsLayerSridCacheExpired(entry, now))
            {
                _layerSridCache.TryRemove(layerId, out _);
            }
        }

        // Remove oldest entries if cache is too large
        var overflow = _layerSridCache.Count - MaxLayerSridCacheEntries;
        if (overflow > 0)
        {
            var oldestEntries = _layerSridCache
                .OrderBy(kvp => kvp.Value.CreatedAt)
                .Take(overflow)
                .ToArray();

            foreach (var (layerId, _) in oldestEntries)
            {
                _layerSridCache.TryRemove(layerId, out _);
            }
        }
    }

    private static GeometryStorageType ResolveGeometryStorageType(string dataType, string udtName)
    {
        return dataType.ToLowerInvariant() switch
        {
            "user-defined" when udtName.Equals("geometry", StringComparison.OrdinalIgnoreCase) => GeometryStorageType.Geometry,
            "user-defined" when udtName.Equals("geography", StringComparison.OrdinalIgnoreCase) => GeometryStorageType.Geography,
            "bytea" => GeometryStorageType.Bytea,
            _ => GeometryStorageType.Bytea
        };
    }
}
