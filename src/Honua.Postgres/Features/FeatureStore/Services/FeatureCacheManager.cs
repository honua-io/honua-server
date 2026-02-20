// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Diagnostics;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Monitoring;
using Honua.Postgres.Features.FeatureStore.Internal;
using Microsoft.Extensions.Logging;
using Npgsql;
using CoreGeometryStorageType = Honua.Core.Features.FeatureStore.Abstractions.GeometryStorageType;

namespace Honua.Postgres.Features.FeatureStore.Services;

/// <summary>
/// Manages caching of layer metadata and performance monitoring for feature store operations
/// </summary>
internal sealed class FeatureCacheManager : IFeatureCacheManager
{
    private static readonly TimeSpan _layerSridCacheRetention = TimeSpan.FromHours(24);
    private const int MaxLayerSridCacheEntries = 10000;

    // Cache state is static so it persists across scoped instances (FeatureCacheManager is registered
    // as scoped because it depends on scoped IDatabaseConnectionProvider, but the cached values are
    // stable for the lifetime of the application).
    private static readonly ConcurrentDictionary<int, LayerSridCacheEntry> _layerSridCache = new();
    private static readonly SemaphoreSlim _geometryStorageInitLock = new(1, 1);
    private static readonly SemaphoreSlim _layerCatalogInitLock = new(1, 1);
    // Use int sentinels for thread-safe double-checked locking (volatile Nullable<T> is not supported)
    // -1 = uninitialized, >= 0 = initialized value
    private static volatile int _geometryStorageTypeValue = -1;
    private static volatile int _hasLayerCatalogValue = -1;

    private readonly IDatabaseConnectionProvider _connectionProvider;
    private readonly ILogger<FeatureCacheManager> _logger;
    private readonly string? _tableSchema;

    /// <summary>
    /// Performance metrics tracking
    /// </summary>
    private static class PerformanceMetrics
    {
        private const int MaxMetricEntries = 500;

        private static readonly ConcurrentDictionary<string, long> _executionCounts = new();
        private static readonly ConcurrentDictionary<string, long> _totalExecutionTimeMs = new();
        private static readonly ConcurrentDictionary<string, long> _maxExecutionTimeMs = new();
        private static readonly ConcurrentDictionary<string, long> _totalResultCounts = new();

        public static void RecordQueryExecution(string operationType, long executionTimeMs, int? resultCount = null)
        {
            if (_executionCounts.Count >= MaxMetricEntries && !_executionCounts.ContainsKey(operationType))
            {
                // Best-effort eviction: clear all dictionaries. Non-atomic across dictionaries
                // is acceptable here since metrics are advisory, not transactional.
                _totalResultCounts.Clear();
                _maxExecutionTimeMs.Clear();
                _totalExecutionTimeMs.Clear();
                _executionCounts.Clear();
            }

            _executionCounts.AddOrUpdate(operationType, 1, (key, value) => value + 1);
            _totalExecutionTimeMs.AddOrUpdate(operationType, executionTimeMs, (key, value) => value + executionTimeMs);
            _maxExecutionTimeMs.AddOrUpdate(operationType, executionTimeMs, (key, value) => Math.Max(value, executionTimeMs));

            if (resultCount.HasValue)
            {
                _totalResultCounts.AddOrUpdate(operationType, resultCount.Value, (key, value) => value + resultCount.Value);
            }
        }

        public static Dictionary<string, DatabaseOperationMetricsSnapshot> GetStatistics()
        {
            var stats = new Dictionary<string, DatabaseOperationMetricsSnapshot>(StringComparer.OrdinalIgnoreCase);

            foreach (var (operationType, count) in _executionCounts)
            {
                var totalTime = _totalExecutionTimeMs.GetValueOrDefault(operationType, 0);
                var maxTime = _maxExecutionTimeMs.GetValueOrDefault(operationType, 0);

                stats[operationType] = new DatabaseOperationMetricsSnapshot
                {
                    Count = count,
                    TotalTimeMs = totalTime,
                    AvgTimeMs = count > 0 ? totalTime / (double)count : 0,
                    MaxTimeMs = maxTime
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
        var now = DateTimeOffset.UtcNow;

        // Check cache first
        if (_layerSridCache.TryGetValue(layerId, out var cachedEntry) &&
            !IsLayerSridCacheExpired(cachedEntry, now))
        {
            return cachedEntry.Srid;
        }

        await using var connection = (NpgsqlConnection)await _connectionProvider
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        if (!await IsLayerCatalogAvailableAsync(connection, cancellationToken).ConfigureAwait(false))
        {
            _layerSridCache[layerId] = new LayerSridCacheEntry(null, now);
            CleanupCacheIfNeeded();
            return null;
        }

        var stopwatch = Stopwatch.StartNew();
        try
        {
            const string sql = """
                SELECT srid
                FROM honua.layers
                WHERE layer_id = $1
                """;

            await using var command = new NpgsqlCommand(sql, connection);
            command.Parameters.AddWithValue(layerId);

            var result = await command.ExecuteScalarAsync(cancellationToken);
            var srid = result is int sridValue ? sridValue : (int?)null;

            // Update cache
            _layerSridCache[layerId] = new LayerSridCacheEntry(srid, now);

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

    public async Task<CoreGeometryStorageType> GetGeometryStorageTypeAsync(CancellationToken cancellationToken)
    {
        var cached = _geometryStorageTypeValue;
        if (cached >= 0)
        {
            return (CoreGeometryStorageType)cached;
        }

        await _geometryStorageInitLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cached = _geometryStorageTypeValue;
            if (cached >= 0)
            {
                return (CoreGeometryStorageType)cached;
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

            CoreGeometryStorageType result;
            if (await reader.ReadAsync(cancellationToken))
            {
                var dataTypeOrdinal = reader.GetOrdinal("data_type");
                var udtNameOrdinal = reader.GetOrdinal("udt_name");
                var dataType = reader.GetString(dataTypeOrdinal);
                var udtName = reader.GetString(udtNameOrdinal);
                result = ResolveGeometryStorageType(dataType, udtName);
            }
            else
            {
                result = CoreGeometryStorageType.Bytea; // Default fallback
            }

            _geometryStorageTypeValue = (int)result;
            return result;
        }
        finally
        {
            _geometryStorageInitLock.Release();
        }
    }

    public async Task<bool> IsLayerCatalogAvailableAsync(CancellationToken cancellationToken)
    {
        var cached = _hasLayerCatalogValue;
        if (cached >= 0)
        {
            return cached == 1;
        }

        await _layerCatalogInitLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cached = _hasLayerCatalogValue;
            if (cached >= 0)
            {
                return cached == 1;
            }

            await using var connection = (NpgsqlConnection)await _connectionProvider
                .OpenConnectionAsync(cancellationToken)
                .ConfigureAwait(false);

            return await IsLayerCatalogAvailableInternalAsync(connection, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _layerCatalogInitLock.Release();
        }
    }

    private async Task<bool> IsLayerCatalogAvailableAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        var cached = _hasLayerCatalogValue;
        if (cached >= 0)
        {
            return cached == 1;
        }

        await _layerCatalogInitLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cached = _hasLayerCatalogValue;
            if (cached >= 0)
            {
                return cached == 1;
            }

            return await IsLayerCatalogAvailableInternalAsync(connection, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _layerCatalogInitLock.Release();
        }
    }

    private async Task<bool> IsLayerCatalogAvailableInternalAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        try
        {
            const string sql = """
                SELECT EXISTS (
                    SELECT 1
                    FROM information_schema.tables
                    WHERE table_schema = 'honua'
                      AND table_name = 'layers'
                )
                """;

            await using var command = new NpgsqlCommand(sql, connection);

            var result = await command.ExecuteScalarAsync(cancellationToken);
            var available = (bool)result!;
            _hasLayerCatalogValue = available ? 1 : 0;

            return available;
        }
        catch
        {
            return false;
        }
    }

    public void RecordQueryMetrics(string operationType, long executionTimeMs, int? resultCount = null)
    {
        PerformanceMetrics.RecordQueryExecution(operationType, executionTimeMs, resultCount);
    }

    public Dictionary<string, DatabaseOperationMetricsSnapshot> GetPerformanceStatistics()
    {
        return PerformanceMetrics.GetStatistics();
    }

    public void CleanupExpiredCacheEntries()
    {
        CleanupCacheIfNeeded();
    }

    public void InvalidateLayerCache(int layerId)
    {
        if (layerId <= 0)
        {
            return;
        }

        _layerSridCache.TryRemove(layerId, out _);
    }

    private static bool IsLayerSridCacheExpired(Internal.LayerSridCacheEntry entry, DateTimeOffset now)
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

        // Remove oldest entries if cache is too large (best-effort, snapshot-based)
        var overflow = _layerSridCache.Count - MaxLayerSridCacheEntries;
        if (overflow > 0)
        {
            var entriesToRemove = _layerSridCache
                .OrderBy(kvp => kvp.Value.CreatedAt)
                .Take(overflow)
                .Select(kvp => kvp.Key)
                .ToArray();

            foreach (var key in entriesToRemove)
            {
                _layerSridCache.TryRemove(key, out _);
            }
        }
    }

    private static CoreGeometryStorageType ResolveGeometryStorageType(string dataType, string udtName)
    {
        return dataType.ToLowerInvariant() switch
        {
            "user-defined" when udtName.Equals("geometry", StringComparison.OrdinalIgnoreCase) => CoreGeometryStorageType.Geometry,
            "user-defined" when udtName.Equals("geography", StringComparison.OrdinalIgnoreCase) => CoreGeometryStorageType.Geography,
            "bytea" => CoreGeometryStorageType.Bytea,
            _ => CoreGeometryStorageType.Bytea
        };
    }
}
