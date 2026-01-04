// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using Honua.Core.Features.Infrastructure.Caching;
using Honua.Core.Features.Infrastructure.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Honua.Postgres.Features.Infrastructure.Caching;

/// <summary>
/// Manages prepared statement caching for PostgreSQL connections
/// </summary>
/// <remarks>
/// <para>
/// Provides intelligent caching of frequently-used SQL statements to improve
/// database performance through query plan reuse and reduced parsing overhead.
/// </para>
/// <para>
/// PERFORMANCE FEATURES:
/// - Automatic statement preparation based on execution frequency
/// - LRU eviction with configurable cache size limits
/// - Connection-aware caching (prepared statements are per-connection)
/// - Background cleanup of expired statements
/// - Comprehensive performance metrics and logging
/// </para>
/// <para>
/// SECURITY NOTICE: This cache only works with properly parameterized queries.
/// It does not cache queries with inline values to prevent SQL injection risks.
/// </para>
/// </remarks>
internal sealed class PreparedStatementCache : IPreparedStatementCacheStatisticsProvider, IDisposable
{
    private readonly QueryCacheOptions _options;
    private readonly ILogger<PreparedStatementCache> _logger;
    private readonly ConcurrentDictionary<string, StatementMetrics> _executionCounts = new();
    private readonly ConcurrentDictionary<(string ConnectionId, string StatementHash), CachedStatement> _cache = new();
    private readonly Timer _cleanupTimer;
    private bool? _prepareSupported;
    private bool _disposed;

    /// <summary>
    /// Metrics for tracking statement execution patterns
    /// </summary>
    private sealed class StatementMetrics
    {
        public int ExecutionCount { get; set; }
        public DateTime FirstSeen { get; init; } = DateTime.UtcNow;
        public DateTime LastUsed { get; set; } = DateTime.UtcNow;
    }

    /// <summary>
    /// Represents a cached prepared statement with metadata
    /// </summary>
    private sealed class CachedStatement : IDisposable
    {
        public required string StatementName { get; init; }
        public required NpgsqlCommand Command { get; init; }
        public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
        public DateTime LastUsed { get; set; } = DateTime.UtcNow;
        public int HitCount { get; set; }

        public void Dispose()
        {
            Command.Dispose();
        }
    }

    public PreparedStatementCache(IOptions<QueryCacheOptions> options, ILogger<PreparedStatementCache> logger)
    {
        _options = options.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        // Start background cleanup timer
        _cleanupTimer = new Timer(CleanupExpiredStatements, null,
            TimeSpan.FromMinutes(_options.CleanupIntervalMinutes),
            TimeSpan.FromMinutes(_options.CleanupIntervalMinutes));
    }

    /// <summary>
    /// Attempts to get a prepared statement from cache, creating one if beneficial
    /// </summary>
    /// <param name="connection">The database connection</param>
    /// <param name="sql">The SQL statement</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>A prepared command if caching is beneficial, null otherwise</returns>
    public Task<NpgsqlCommand?> GetOrCreatePreparedCommandAsync(
        NpgsqlConnection connection,
        string sql,
        CancellationToken cancellationToken = default)
    {
        return GetOrCreatePreparedCommandAsync(connection, sql, configureParameters: null, cancellationToken);
    }

    public async Task<NpgsqlCommand?> GetOrCreatePreparedCommandAsync(
        NpgsqlConnection connection,
        string sql,
        Action<NpgsqlCommand>? configureParameters,
        CancellationToken cancellationToken = default)
    {
        if (!_options.EnableAutomaticCaching || string.IsNullOrEmpty(sql))
            return null;

        if (!IsPreparationSupported(connection))
            return null;

        if (configureParameters == null && sql.Contains('$', StringComparison.Ordinal))
            return null;

        var statementHash = GetStatementHash(sql);
        var connectionId = GetConnectionId(connection);
        var cacheKey = (connectionId, statementHash);

        // Update execution metrics
        var metrics = _executionCounts.AddOrUpdate(statementHash,
            _ => new StatementMetrics
            {
                ExecutionCount = 1,
                LastUsed = DateTime.UtcNow
            },
            (_, existing) =>
            {
                existing.ExecutionCount++;
                existing.LastUsed = DateTime.UtcNow;
                return existing;
            });

        // Check if we have a cached prepared statement
        if (_cache.TryGetValue(cacheKey, out var cached))
        {
            cached.LastUsed = DateTime.UtcNow;
            cached.HitCount++;

            if (_options.EnablePerformanceLogging)
            {
                PreparedStatementCacheLog.CacheHit(_logger, statementHash);
            }

            // Clone the command to avoid parameter conflicts
            return CloneCommand(cached.Command, connection);
        }

        // Check if this statement should be prepared
        if (metrics.ExecutionCount >= _options.MinExecutionsForCaching)
        {
            try
            {
                var preparedCommand = await CreatePreparedStatementAsync(connection, sql, statementHash, configureParameters, cancellationToken);

                // Add to cache if we have space
                if (_cache.Count < _options.MaxCachedStatements)
                {
                    var cachedStatement = new CachedStatement
                    {
                        StatementName = $"stmt_{statementHash}",
                        Command = preparedCommand
                    };

                    _cache.TryAdd(cacheKey, cachedStatement);

                    if (_options.EnablePerformanceLogging)
                    {
                        PreparedStatementCacheLog.CreatedStatement(_logger, statementHash);
                    }

                    return CloneCommand(preparedCommand, connection);
                }
                else
                {
                    // Cache is full, need to evict LRU item
                    EvictLeastRecentlyUsed(connectionId);

                    var cachedStatement = new CachedStatement
                    {
                        StatementName = $"stmt_{statementHash}",
                        Command = preparedCommand
                    };

                    _cache.TryAdd(cacheKey, cachedStatement);
                    return CloneCommand(preparedCommand, connection);
                }
            }
            catch (Exception ex)
            {
                PreparedStatementCacheLog.PrepareFailed(_logger, statementHash, ex);
                return null;
            }
        }

        if (_options.EnablePerformanceLogging)
        {
            PreparedStatementCacheLog.CacheMiss(_logger, statementHash, metrics.ExecutionCount);
        }

        return null;
    }

    /// <summary>
    /// Manually prepares and caches a high-priority statement
    /// </summary>
    /// <param name="connection">The database connection</param>
    /// <param name="sql">The SQL statement</param>
    /// <param name="statementName">Unique name for the statement</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The prepared command, or null if preparation is disabled or unsupported</returns>
    public Task<NpgsqlCommand?> PreparePriorityStatementAsync(
        NpgsqlConnection connection,
        string sql,
        string statementName,
        CancellationToken cancellationToken = default)
    {
        return PreparePriorityStatementAsync(connection, sql, statementName, configureParameters: null, cancellationToken);
    }

    public async Task<NpgsqlCommand?> PreparePriorityStatementAsync(
        NpgsqlConnection connection,
        string sql,
        string statementName,
        Action<NpgsqlCommand>? configureParameters,
        CancellationToken cancellationToken = default)
    {
        if (!IsPreparationSupported(connection))
        {
            return null;
        }

        if (configureParameters == null && sql.Contains('$', StringComparison.Ordinal))
        {
            return null;
        }

        var connectionId = GetConnectionId(connection);
        var statementHash = GetStatementHash(sql);
        var cacheKey = (connectionId, statementHash);

        // Check if already cached
        if (_cache.TryGetValue(cacheKey, out var existing))
        {
            existing.LastUsed = DateTime.UtcNow;
            existing.HitCount++;
            return CloneCommand(existing.Command, connection);
        }

        // Create prepared statement
        var command = await CreatePreparedStatementAsync(connection, sql, statementName, configureParameters, cancellationToken);

        var cached = new CachedStatement
        {
            StatementName = statementName,
            Command = command
        };

        // Force cache if at capacity by evicting LRU
        if (_cache.Count >= _options.MaxCachedStatements)
        {
            EvictLeastRecentlyUsed(connectionId);
        }

        _cache.TryAdd(cacheKey, cached);

        PreparedStatementCacheLog.PriorityStatementPrepared(_logger, statementName);

        return CloneCommand(command, connection);
    }

    /// <summary>
    /// Gets current cache performance statistics
    /// </summary>
    public PreparedStatementCacheStatistics GetStatistics()
    {
        var totalHits = _cache.Values.Sum(s => s.HitCount);
        var totalMisses = _executionCounts.Values
            .Where(m => m.ExecutionCount >= _options.MinExecutionsForCaching)
            .Sum(m => m.ExecutionCount) - totalHits;

        return new PreparedStatementCacheStatistics
        {
            TotalStatements = _executionCounts.Count,
            CacheHits = totalHits,
            CacheMisses = Math.Max(0, totalMisses),
            PreparedStatements = _cache.Count
        };
    }

    /// <summary>
    /// Clears all cached statements for a specific connection
    /// </summary>
    /// <param name="connection">The connection to clear cache for</param>
    public void ClearConnectionCache(NpgsqlConnection connection)
    {
        var connectionId = GetConnectionId(connection);
        var keysToRemove = _cache.Keys.Where(k => k.ConnectionId == connectionId).ToList();

        foreach (var key in keysToRemove)
        {
            if (_cache.TryRemove(key, out var statement))
            {
                statement.Dispose();
            }
        }

        PreparedStatementCacheLog.ConnectionCacheCleared(_logger, keysToRemove.Count, connectionId);
    }

    private bool IsPreparationSupported(NpgsqlConnection connection)
    {
        if (_prepareSupported.HasValue)
        {
            return _prepareSupported.Value;
        }

        try
        {
            var builder = new NpgsqlConnectionStringBuilder(connection.ConnectionString);
            _prepareSupported = !builder.Multiplexing;
        }
        catch
        {
            _prepareSupported = false;
        }

        return _prepareSupported.Value;
    }

    private async Task<NpgsqlCommand> CreatePreparedStatementAsync(
        NpgsqlConnection connection,
        string sql,
        string statementName,
        Action<NpgsqlCommand>? configureParameters,
        CancellationToken cancellationToken)
    {
        var command = new NpgsqlCommand(sql, connection);
        configureParameters?.Invoke(command);

        // Prepare the statement
        await command.PrepareAsync(cancellationToken);

        return command;
    }

    private static NpgsqlCommand CloneCommand(NpgsqlCommand original, NpgsqlConnection connection)
    {
        var cloned = new NpgsqlCommand(original.CommandText, connection)
        {
            CommandType = original.CommandType,
            CommandTimeout = original.CommandTimeout
        };

        // Clone parameters structure but not values (will be set by caller)
        foreach (NpgsqlParameter param in original.Parameters)
        {
            var clonedParam = new NpgsqlParameter
            {
                ParameterName = param.ParameterName,
                NpgsqlDbType = param.NpgsqlDbType,
                DbType = param.DbType,
                Direction = param.Direction,
                Size = param.Size,
                Precision = param.Precision,
                Scale = param.Scale
            };
            cloned.Parameters.Add(clonedParam);
        }

        return cloned;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static string GetConnectionId(NpgsqlConnection connection)
    {
        return connection.ProcessID.ToString(CultureInfo.InvariantCulture);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static string GetStatementHash(string sql)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(sql));
        return Convert.ToHexString(hash);
    }

    private void EvictLeastRecentlyUsed(string connectionId)
    {
        var lruKey = _cache
            .Where(kvp => kvp.Key.ConnectionId == connectionId)
            .OrderBy(kvp => kvp.Value.LastUsed)
            .Select(kvp => kvp.Key)
            .FirstOrDefault();

        if (lruKey != default && _cache.TryRemove(lruKey, out var removed))
        {
            removed.Dispose();

            if (_options.EnablePerformanceLogging)
            {
                PreparedStatementCacheLog.EvictedStatement(_logger, removed.StatementName);
            }
        }
    }

    private void CleanupExpiredStatements(object? state)
    {
        try
        {
            var cutoff = DateTime.UtcNow.AddMinutes(-_options.StatementLifetimeMinutes);
            var expiredKeys = _cache
                .Where(kvp => kvp.Value.CreatedAt < cutoff)
                .Select(kvp => kvp.Key)
                .ToList();

            foreach (var key in expiredKeys)
            {
                if (_cache.TryRemove(key, out var expired))
                {
                    expired.Dispose();
                }
            }

            // Also cleanup execution metrics for old statements
            var oldMetrics = _executionCounts
                .Where(kvp => kvp.Value.LastUsed < cutoff)
                .Select(kvp => kvp.Key)
                .ToList();

            foreach (var key in oldMetrics)
            {
                _executionCounts.TryRemove(key, out _);
            }

            if (expiredKeys.Count > 0 || oldMetrics.Count > 0)
            {
                PreparedStatementCacheLog.CleanupCompleted(_logger, expiredKeys.Count, oldMetrics.Count);
            }
        }
        catch (Exception ex)
        {
            PreparedStatementCacheLog.CleanupFailed(_logger, ex);
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _cleanupTimer?.Dispose();

        // Dispose all cached statements
        foreach (var statement in _cache.Values)
        {
            statement.Dispose();
        }

        _cache.Clear();
        _executionCounts.Clear();
    }
}

internal static partial class PreparedStatementCacheLog
{
    [LoggerMessage(
        EventId = 8701,
        Level = LogLevel.Debug,
        Message = "Cache HIT for statement: {StatementHash}")]
    public static partial void CacheHit(ILogger logger, string statementHash);

    [LoggerMessage(
        EventId = 8702,
        Level = LogLevel.Debug,
        Message = "Created and cached prepared statement: {StatementHash}")]
    public static partial void CreatedStatement(ILogger logger, string statementHash);

    [LoggerMessage(
        EventId = 8703,
        Level = LogLevel.Warning,
        Message = "Failed to create prepared statement for: {StatementHash}")]
    public static partial void PrepareFailed(ILogger logger, string statementHash, Exception exception);

    [LoggerMessage(
        EventId = 8704,
        Level = LogLevel.Debug,
        Message = "Cache MISS for statement: {StatementHash} (executions: {Count})")]
    public static partial void CacheMiss(ILogger logger, string statementHash, int count);

    [LoggerMessage(
        EventId = 8705,
        Level = LogLevel.Information,
        Message = "Prepared priority statement: {StatementName}")]
    public static partial void PriorityStatementPrepared(ILogger logger, string statementName);

    [LoggerMessage(
        EventId = 8706,
        Level = LogLevel.Debug,
        Message = "Cleared {Count} cached statements for connection {ConnectionId}")]
    public static partial void ConnectionCacheCleared(ILogger logger, int count, string connectionId);

    [LoggerMessage(
        EventId = 8707,
        Level = LogLevel.Debug,
        Message = "Evicted LRU statement: {StatementName}")]
    public static partial void EvictedStatement(ILogger logger, string statementName);

    [LoggerMessage(
        EventId = 8708,
        Level = LogLevel.Debug,
        Message = "Cleaned up {StatementCount} expired statements and {MetricCount} old metrics")]
    public static partial void CleanupCompleted(ILogger logger, int statementCount, int metricCount);

    [LoggerMessage(
        EventId = 8709,
        Level = LogLevel.Error,
        Message = "Error during cache cleanup")]
    public static partial void CleanupFailed(ILogger logger, Exception exception);
}
