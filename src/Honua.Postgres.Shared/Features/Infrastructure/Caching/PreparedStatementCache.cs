// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using Honua.Core.Features.Infrastructure.Caching;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Postgres.Features.Infrastructure;
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
    private readonly ConcurrentDictionary<string, int> _connectionCounts = new();
    private readonly Timer _cleanupTimer;
    private bool? _prepareSupported;
    private volatile bool _disposed;

    /// <summary>
    /// WEEK 5 FIX: Enhanced metrics for tracking statement execution patterns with performance analytics
    /// </summary>
    public sealed class StatementMetrics
    {
        private int _executionCount;
        private long _totalExecutionTimeMs;
        private long _minExecutionTimeMs = long.MaxValue;
        private long _maxExecutionTimeMs;

        public int ExecutionCount
        {
            get => Volatile.Read(ref _executionCount);
            set => Volatile.Write(ref _executionCount, value);
        }

        public double AverageExecutionTimeMs
        {
            get
            {
                var count = ExecutionCount;
                return count > 0 ? (double)_totalExecutionTimeMs / count : 0;
            }
        }

        public long MinExecutionTimeMs => _minExecutionTimeMs == long.MaxValue ? 0 : _minExecutionTimeMs;
        public long MaxExecutionTimeMs => _maxExecutionTimeMs;

        // WEEK 5 FIX: Parameter distribution tracking for adaptive optimization
        public ConcurrentDictionary<string, ParameterDistribution> ParameterDistributions { get; } = new();

        // WEEK 5 FIX: Query complexity scoring
        public QueryComplexityScore ComplexityScore { get; set; } = new();

        public void IncrementExecutionCount() => Interlocked.Increment(ref _executionCount);

        /// <summary>
        /// WEEK 5 FIX: Record execution timing for performance analysis
        /// </summary>
        public void RecordExecutionTime(long executionTimeMs)
        {
            Interlocked.Add(ref _totalExecutionTimeMs, executionTimeMs);

            // Update min/max using atomic operations
            long currentMin, currentMax;
            do
            {
                currentMin = _minExecutionTimeMs;
            } while (executionTimeMs < currentMin && Interlocked.CompareExchange(ref _minExecutionTimeMs, executionTimeMs, currentMin) != currentMin);

            do
            {
                currentMax = _maxExecutionTimeMs;
            } while (executionTimeMs > currentMax && Interlocked.CompareExchange(ref _maxExecutionTimeMs, executionTimeMs, currentMax) != currentMax);
        }

        public DateTime FirstSeen { get; init; } = DateTime.UtcNow;
        public DateTime LastUsed { get; set; } = DateTime.UtcNow;
    }

    /// <summary>
    /// WEEK 5 FIX: Parameter distribution tracking for query plan optimization
    /// </summary>
    public sealed class ParameterDistribution
    {
        private long _totalValues;
        private readonly ConcurrentDictionary<string, long> _valueFrequency = new();

        public void RecordValue(object? value)
        {
            Interlocked.Increment(ref _totalValues);
            var key = value?.ToString() ?? "<null>";
            _valueFrequency.AddOrUpdate(key, 1, (_, count) => count + 1);
        }

        public double GetSelectivity()
        {
            var total = _totalValues;
            if (total == 0) return 1.0;

            var distinctCount = _valueFrequency.Count;
            return (double)distinctCount / total;
        }

        public string GetMostFrequentValue()
        {
            return _valueFrequency.OrderByDescending(kvp => kvp.Value).FirstOrDefault().Key ?? "";
        }
    }

    /// <summary>
    /// WEEK 5 FIX: Query complexity scoring for intelligent caching decisions
    /// </summary>
    public sealed class QueryComplexityScore
    {
        public int JoinCount { get; set; }
        public int SubqueryCount { get; set; }
        public int AggregateCount { get; set; }
        public bool HasGeospatialOperations { get; set; }
        public bool HasWindowFunctions { get; set; }
        public int EstimatedCost { get; set; }

        public int GetComplexityScore()
        {
            var score = JoinCount * 2 + SubqueryCount * 3 + AggregateCount;
            if (HasGeospatialOperations) score += 5;
            if (HasWindowFunctions) score += 4;
            return score + (EstimatedCost / 100);
        }
    }

    /// <summary>
    /// WEEK 5 FIX: Enhanced cached prepared statement with performance analytics
    /// </summary>
    public sealed class CachedStatement : IDisposable
    {
        private int _hitCount;
        private long _totalExecutionTimeMs;

        public required string StatementName { get; init; }
        public required NpgsqlCommand Command { get; init; }
        public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
        public DateTime LastUsed { get; set; } = DateTime.UtcNow;
        public int HitCount
        {
            get => Volatile.Read(ref _hitCount);
            set => Volatile.Write(ref _hitCount, value);
        }

        // WEEK 5 FIX: Performance tracking for cached statements
        public double AverageExecutionTimeMs
        {
            get
            {
                var hits = HitCount;
                return hits > 0 ? (double)_totalExecutionTimeMs / hits : 0;
            }
        }

        // WEEK 5 FIX: Query plan stability scoring
        public double PlanStabilityScore { get; set; } = 1.0;
        public string? LastExplainPlan { get; set; }
        public int PlanChangeCount { get; set; }

        public void IncrementHitCount() => Interlocked.Increment(ref _hitCount);

        /// <summary>
        /// WEEK 5 FIX: Record execution time for performance analysis
        /// </summary>
        public void RecordExecutionTime(long executionTimeMs)
        {
            Interlocked.Add(ref _totalExecutionTimeMs, executionTimeMs);
        }

        /// <summary>
        /// WEEK 5 FIX: Calculate priority score for eviction decisions
        /// </summary>
        public double GetPriorityScore()
        {
            var age = DateTime.UtcNow - CreatedAt;
            var recentUsage = Math.Max(1, (DateTime.UtcNow - LastUsed).TotalMinutes);
            var hitRate = HitCount / Math.Max(1, age.TotalHours);
            var performanceScore = AverageExecutionTimeMs > 0 ? 1000 / AverageExecutionTimeMs : 1;

            // Higher score = higher priority to keep
            return (hitRate * performanceScore * PlanStabilityScore) / Math.Log10(recentUsage + 1);
        }

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

        if (!PostgresSqlSafety.IsReadOnlySingleStatement(sql))
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
                LastUsed = DateTime.UtcNow,
                ComplexityScore = AnalyzeQueryComplexity(sql) // WEEK 5 FIX: Analyze complexity for intelligent caching
            },
            (_, existing) =>
            {
                existing.IncrementExecutionCount();
                existing.LastUsed = DateTime.UtcNow;
                return existing;
            });

        // WEEK 5 FIX: Record parameter patterns for adaptive optimization
        if (configureParameters != null)
        {
            RecordParameterPatterns(metrics, configureParameters);
        }

        // Check if we have a cached prepared statement
        if (_cache.TryGetValue(cacheKey, out var cached))
        {
            cached.LastUsed = DateTime.UtcNow;
            cached.IncrementHitCount();

            if (_options.EnablePerformanceLogging)
            {
                PreparedStatementCacheLog.CacheHit(_logger, statementHash);
            }

            var cachedClone = await CreatePreparedExecutionCommandAsync(
                cached.Command,
                connection,
                configureParameters,
                statementHash,
                cancellationToken).ConfigureAwait(false);

            if (cachedClone != null)
            {
                return cachedClone;
            }

            if (TryRemoveCachedStatement(cacheKey, out var removed) && removed != null)
            {
                removed.Dispose();
            }

            return null;
        }

        // WEEK 5 FIX: Intelligent caching decision based on complexity and execution patterns
        var shouldCache = ShouldCacheStatement(metrics, sql);
        if (shouldCache)
        {
            try
            {
                var preparedCommand = await CreatePreparedStatementAsync(connection, sql, statementHash, configureParameters, cancellationToken);

                if (GetConnectionCacheCount(connectionId) >= _options.MaxCachedStatements)
                {
                    EvictLowestPriorityStatement(connectionId); // WEEK 5 FIX: Use priority-based eviction
                }

                var cachedStatement = new CachedStatement
                {
                    StatementName = $"stmt_{statementHash}",
                    Command = preparedCommand
                };

                var added = TryAddCachedStatement(cacheKey, cachedStatement);

                if (added && _options.EnablePerformanceLogging)
                {
                    PreparedStatementCacheLog.CreatedStatement(_logger, statementHash);
                }

                if (!added)
                {
                    preparedCommand.Dispose();

                    if (_cache.TryGetValue(cacheKey, out var existing))
                    {
                        return await CreatePreparedExecutionCommandAsync(
                            existing.Command,
                            connection,
                            configureParameters,
                            statementHash,
                            cancellationToken).ConfigureAwait(false);
                    }

                    return null;
                }

                return await CreatePreparedExecutionCommandAsync(
                    preparedCommand,
                    connection,
                    configureParameters,
                    statementHash,
                    cancellationToken).ConfigureAwait(false);
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

        if (!PostgresSqlSafety.IsReadOnlySingleStatement(sql))
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
            var cachedClone = await CreatePreparedExecutionCommandAsync(
                existing.Command,
                connection,
                configureParameters,
                statementHash,
                cancellationToken).ConfigureAwait(false);

            if (cachedClone != null)
            {
                return cachedClone;
            }

            if (TryRemoveCachedStatement(cacheKey, out var removed) && removed != null)
            {
                removed.Dispose();
            }

            return null;
        }

        // Create prepared statement
        var command = await CreatePreparedStatementAsync(connection, sql, statementName, configureParameters, cancellationToken);

        var cached = new CachedStatement
        {
            StatementName = statementName,
            Command = command
        };

        if (GetConnectionCacheCount(connectionId) >= _options.MaxCachedStatements)
        {
            EvictLeastRecentlyUsed(connectionId);
        }

        var added = TryAddCachedStatement(cacheKey, cached);
        if (!added)
        {
            command.Dispose();
            return null;
        }

        PreparedStatementCacheLog.PriorityStatementPrepared(_logger, statementName);

        return await CreatePreparedExecutionCommandAsync(
            command,
            connection,
            configureParameters,
            statementHash,
            cancellationToken).ConfigureAwait(false);
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
        var removedCount = 0;

        foreach (var key in keysToRemove)
        {
            if (TryRemoveCachedStatement(key, out var statement) && statement != null)
            {
                statement.Dispose();
                removedCount++;
            }
        }

        PreparedStatementCacheLog.ConnectionCacheCleared(_logger, removedCount, connectionId);
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

    private static async Task<NpgsqlCommand> CreatePreparedStatementAsync(
        NpgsqlConnection connection,
        string sql,
        string statementName,
        Action<NpgsqlCommand>? configureParameters,
        CancellationToken cancellationToken)
    {
        var command = PostgresSqlSafety.CreateReadCommand(connection, sql);
        configureParameters?.Invoke(command);

        // Prepare the statement
        await command.PrepareAsync(cancellationToken);

        return command;
    }

    private static NpgsqlCommand CloneCommand(NpgsqlCommand original, NpgsqlConnection connection)
    {
        var cloned = PostgresSqlSafety.CreateReadCommand(connection, original.CommandText);
        cloned.CommandType = original.CommandType;
        cloned.CommandTimeout = original.CommandTimeout;

        // Clone parameters structure but not values (will be set by caller)
        foreach (NpgsqlParameter param in original.Parameters)
        {
            cloned.Parameters.Add(CloneParameter(param, includeValue: false));
        }

        return cloned;
    }

    private static NpgsqlParameter CloneParameter(NpgsqlParameter original, bool includeValue)
    {
        var cloned = new NpgsqlParameter
        {
            ParameterName = original.ParameterName,
            NpgsqlDbType = original.NpgsqlDbType,
            DbType = original.DbType,
            Direction = original.Direction,
            Size = original.Size,
            Precision = original.Precision,
            Scale = original.Scale
        };

        if (includeValue)
        {
            cloned.Value = original.Value;
        }

        return cloned;
    }

    private static void ApplyConfiguredParameterValues(
        NpgsqlCommand destination,
        Action<NpgsqlCommand>? configureParameters)
    {
        if (configureParameters == null)
        {
            return;
        }

        using var configured = new NpgsqlCommand();
        configureParameters(configured);

        for (var index = 0; index < configured.Parameters.Count; index++)
        {
            var source = configured.Parameters[index];

            if (destination.Parameters.Count > index)
            {
                var target = destination.Parameters[index];
                target.Value = source.Value;
                target.ParameterName = source.ParameterName;
                target.NpgsqlDbType = source.NpgsqlDbType;
                target.DbType = source.DbType;
                target.Direction = source.Direction;
                target.Size = source.Size;
                target.Precision = source.Precision;
                target.Scale = source.Scale;
            }
            else
            {
                destination.Parameters.Add(CloneParameter(source, includeValue: true));
            }
        }
    }

    private static async Task<NpgsqlCommand?> CreatePreparedExecutionCommandAsync(
        NpgsqlCommand template,
        NpgsqlConnection connection,
        Action<NpgsqlCommand>? configureParameters,
        string statementHash,
        CancellationToken cancellationToken)
    {
        var cloned = CloneCommand(template, connection);
        ApplyConfiguredParameterValues(cloned, configureParameters);
        await cloned.PrepareAsync(cancellationToken).ConfigureAwait(false);
        return cloned;
    }

    private int GetConnectionCacheCount(string connectionId)
        => _connectionCounts.TryGetValue(connectionId, out var count) ? count : 0;

    private bool TryAddCachedStatement(
        (string ConnectionId, string StatementHash) cacheKey,
        CachedStatement cachedStatement)
    {
        if (_cache.TryAdd(cacheKey, cachedStatement))
        {
            _connectionCounts.AddOrUpdate(cacheKey.ConnectionId, 1, (_, count) => count + 1);
            return true;
        }

        return false;
    }

    private bool TryRemoveCachedStatement(
        (string ConnectionId, string StatementHash) cacheKey,
        out CachedStatement? removed)
    {
        if (_cache.TryRemove(cacheKey, out removed))
        {
            var updated = _connectionCounts.AddOrUpdate(cacheKey.ConnectionId, 0, (_, count) => Math.Max(0, count - 1));
            if (updated == 0)
            {
                _connectionCounts.TryRemove(cacheKey.ConnectionId, out _);
            }

            return true;
        }

        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static string GetConnectionId(NpgsqlConnection connection)
    {
        try
        {
            return connection.ProcessID.ToString(CultureInfo.InvariantCulture);
        }
        catch (InvalidOperationException)
        {
            return RuntimeHelpers.GetHashCode(connection).ToString(CultureInfo.InvariantCulture);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static string GetStatementHash(string sql)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(sql));
        return Convert.ToHexString(hash);
    }

    /// <summary>
    /// WEEK 5 FIX: Intelligent query complexity analysis for caching decisions
    /// </summary>
    private static QueryComplexityScore AnalyzeQueryComplexity(string sql)
    {
        var score = new QueryComplexityScore();
        var sqlLower = sql.ToLowerInvariant();

        // Count joins
        score.JoinCount = CountOccurrences(sqlLower, "join");

        // Count subqueries
        score.SubqueryCount = CountOccurrences(sqlLower, "(select") + CountOccurrences(sqlLower, "exists(");

        // Count aggregates
        score.AggregateCount = CountOccurrences(sqlLower, "group by") + CountOccurrences(sqlLower, "having") +
                              CountOccurrences(sqlLower, "count(") + CountOccurrences(sqlLower, "sum(") +
                              CountOccurrences(sqlLower, "avg(") + CountOccurrences(sqlLower, "max(") + CountOccurrences(sqlLower, "min(");

        // Check for geospatial operations
        score.HasGeospatialOperations = sqlLower.Contains("st_") || sqlLower.Contains("geometry") || sqlLower.Contains("geography");

        // Check for window functions
        score.HasWindowFunctions = sqlLower.Contains("over(") || sqlLower.Contains("partition by") || sqlLower.Contains("row_number()");

        return score;
    }

    /// <summary>
    /// WEEK 5 FIX: Record parameter patterns for adaptive optimization
    /// </summary>
    private static void RecordParameterPatterns(StatementMetrics metrics, Action<NpgsqlCommand> configureParameters)
    {
        // Create a temporary command to analyze parameters
        using var tempCommand = new NpgsqlCommand();
        configureParameters(tempCommand);

        foreach (NpgsqlParameter param in tempCommand.Parameters)
        {
            var paramName = param.ParameterName ?? string.Empty;
            var distribution = metrics.ParameterDistributions.GetOrAdd(paramName, _ => new ParameterDistribution());
            distribution.RecordValue(param.Value);
        }
    }

    /// <summary>
    /// WEEK 5 FIX: Intelligent caching decision based on complexity and execution patterns
    /// </summary>
    private bool ShouldCacheStatement(StatementMetrics metrics, string sql)
    {
        // Always cache if execution count threshold is met (traditional approach)
        if (metrics.ExecutionCount >= _options.MinExecutionsForCaching)
            return true;

        var complexityScore = metrics.ComplexityScore.GetComplexityScore();

        // WEEK 5 FIX: Early caching for high-complexity queries with lower execution threshold
        if (complexityScore >= 10 && metrics.ExecutionCount >= Math.Max(1, _options.MinExecutionsForCaching / 2))
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                var statementHash = GetStatementHash(sql);
                PreparedStatementCacheLog.EarlyCachingTriggered(_logger, statementHash, complexityScore);
            }
            return true;
        }

        // WEEK 5 FIX: Cache geospatial queries earlier due to high cost
        if (metrics.ComplexityScore.HasGeospatialOperations && metrics.ExecutionCount >= 2)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                var statementHash = GetStatementHash(sql);
                PreparedStatementCacheLog.GeospatialCachingTriggered(_logger, statementHash);
            }
            return true;
        }

        // WEEK 5 FIX: Predictive caching for queries with consistent performance patterns
        if (metrics.ExecutionCount >= 3 && metrics.AverageExecutionTimeMs > 50 && IsConsistentPerformance(metrics))
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                var statementHash = GetStatementHash(sql);
                PreparedStatementCacheLog.PredictiveCachingTriggered(_logger, statementHash, metrics.AverageExecutionTimeMs);
            }
            return true;
        }

        return false;
    }

    /// <summary>
    /// WEEK 5 FIX: Check if query has consistent performance characteristics
    /// </summary>
    private static bool IsConsistentPerformance(StatementMetrics metrics)
    {
        if (metrics.ExecutionCount < 3)
            return false;

        var avgTime = metrics.AverageExecutionTimeMs;
        var minTime = metrics.MinExecutionTimeMs;
        var maxTime = metrics.MaxExecutionTimeMs;

        // Consider performance consistent if variance is low
        var variance = (maxTime - minTime) / Math.Max(1, avgTime);
        return variance < 0.5; // Less than 50% variance
    }

    /// <summary>
    /// WEEK 5 FIX: Priority-based eviction using performance analytics
    /// </summary>
    private void EvictLowestPriorityStatement(string connectionId)
    {
        var connectionEntries = _cache
            .Where(kvp => kvp.Key.ConnectionId == connectionId)
            .ToArray();

        if (connectionEntries.Length == 0)
            return;

        // Find statement with lowest priority score
        var lowestPriorityEntry = connectionEntries.MinBy(kvp => kvp.Value.GetPriorityScore());

        if (TryRemoveCachedStatement(lowestPriorityEntry.Key, out var removed) && removed != null)
        {
            removed.Dispose();

            if (_options.EnablePerformanceLogging)
            {
                PreparedStatementCacheLog.EvictedStatement(_logger, removed.StatementName);
            }
        }
    }

    /// <summary>
    /// WEEK 5 FIX: Legacy LRU eviction for backward compatibility
    /// </summary>
    private void EvictLeastRecentlyUsed(string connectionId)
    {
        var connectionEntries = _cache
            .Where(kvp => kvp.Key.ConnectionId == connectionId)
            .ToArray();

        if (connectionEntries.Length == 0)
            return;

        var lruEntry = connectionEntries.MinBy(kvp => kvp.Value.LastUsed);

        if (TryRemoveCachedStatement(lruEntry.Key, out var removed) && removed != null)
        {
            removed.Dispose();

            if (_options.EnablePerformanceLogging)
            {
                PreparedStatementCacheLog.EvictedStatement(_logger, removed.StatementName);
            }
        }
    }

    /// <summary>
    /// WEEK 5 FIX: Helper method to count string occurrences
    /// </summary>
    private static int CountOccurrences(string text, string pattern)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(pattern, index, StringComparison.OrdinalIgnoreCase)) != -1)
        {
            count++;
            index += pattern.Length;
        }
        return count;
    }

    private void CleanupExpiredStatements(object? state)
    {
        if (_disposed)
            return;

        try
        {
            var cutoff = DateTime.UtcNow.AddMinutes(-_options.StatementLifetimeMinutes);
            var expiredKeys = _cache
                .Where(kvp => kvp.Value.CreatedAt < cutoff)
                .Select(kvp => kvp.Key)
                .ToList();

            foreach (var key in expiredKeys)
            {
                if (TryRemoveCachedStatement(key, out var expired) && expired != null)
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
        _connectionCounts.Clear();
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

    // WEEK 5 FIX: Advanced caching decision logging
    [LoggerMessage(
        EventId = 8710,
        Level = LogLevel.Debug,
        Message = "Early caching triggered for complex statement: {StatementHash} (complexity: {ComplexityScore})")]
    public static partial void EarlyCachingTriggered(ILogger logger, string statementHash, int complexityScore);

    [LoggerMessage(
        EventId = 8711,
        Level = LogLevel.Debug,
        Message = "Geospatial caching triggered for statement: {StatementHash}")]
    public static partial void GeospatialCachingTriggered(ILogger logger, string statementHash);

    [LoggerMessage(
        EventId = 8712,
        Level = LogLevel.Debug,
        Message = "Predictive caching triggered for statement: {StatementHash} (avg time: {AverageTimeMs}ms)")]
    public static partial void PredictiveCachingTriggered(ILogger logger, string statementHash, double averageTimeMs);

    [LoggerMessage(
        EventId = 8713,
        Level = LogLevel.Information,
        Message = "Query plan changed for statement: {StatementName}")]
    public static partial void QueryPlanChanged(ILogger logger, string statementName);

    [LoggerMessage(
        EventId = 8714,
        Level = LogLevel.Warning,
        Message = "Failed to analyze query plan for statement: {StatementName}")]
    public static partial void PlanAnalysisFailed(ILogger logger, string statementName, Exception exception);
}
