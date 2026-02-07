// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using System.Globalization;
using Honua.Core.Configuration;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Monitoring;
using Honua.Postgres.Features.Infrastructure.Caching;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.ObjectPool;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Honua.Postgres.Features.FeatureStore.Services;

internal sealed record FeatureDataAccessDependencies(
    IDatabaseConnectionProvider ConnectionProvider,
    IGeometryProcessor GeometryProcessor,
    IFeatureCacheManager CacheManager,
    ObjectPool<Dictionary<string, object?>> DictionaryPool,
    PreparedStatementCache? StatementCache,
    ILogger<FeatureDataAccess> Logger,
    IOptions<PerformanceMonitoringOptions>? PerformanceOptions,
    IOptions<LimitsOptions>? LimitsOptions,
    IPerformanceMonitor? PerformanceMonitor,
    string? SchemaName);

/// <summary>
/// Handles database data access operations for PostgreSQL feature store
/// </summary>
internal sealed partial class FeatureDataAccess : IFeatureDataAccess
{
    private readonly IDatabaseConnectionProvider _connectionProvider;
    private readonly IGeometryProcessor _geometryProcessor;
    private readonly IFeatureCacheManager _cacheManager;
    private readonly ObjectPool<Dictionary<string, object?>> _dictionaryPool;
    private readonly PreparedStatementCache? _statementCache;
    private readonly IPerformanceMonitor? _performanceMonitor;
    private readonly ILogger<FeatureDataAccess> _logger;
    private readonly double _slowQueryThresholdMs;
    private readonly int _queryTimeoutSeconds;
    private readonly int _tileTimeoutSeconds;
    private readonly string _tableName;

    public FeatureDataAccess(FeatureDataAccessDependencies dependencies)
    {
        ArgumentNullException.ThrowIfNull(dependencies);

        _connectionProvider = dependencies.ConnectionProvider ?? throw new ArgumentNullException(nameof(dependencies), "ConnectionProvider is required.");
        _geometryProcessor = dependencies.GeometryProcessor ?? throw new ArgumentNullException(nameof(dependencies), "GeometryProcessor is required.");
        _cacheManager = dependencies.CacheManager ?? throw new ArgumentNullException(nameof(dependencies), "CacheManager is required.");
        _dictionaryPool = dependencies.DictionaryPool ?? throw new ArgumentNullException(nameof(dependencies), "DictionaryPool is required.");
        _statementCache = dependencies.StatementCache;
        _performanceMonitor = dependencies.PerformanceMonitor;
        _logger = dependencies.Logger ?? throw new ArgumentNullException(nameof(dependencies), "Logger is required.");
        _slowQueryThresholdMs = (dependencies.PerformanceOptions?.Value.SlowRequestThreshold ?? TimeSpan.FromSeconds(1))
            .TotalMilliseconds;

        var limits = dependencies.LimitsOptions?.Value ?? new LimitsOptions();
        _queryTimeoutSeconds = GetTimeoutSeconds(limits.Query.QueryTimeout, TimeConstants.ThirtySeconds);
        _tileTimeoutSeconds = GetTimeoutSeconds(limits.Tiles.TileTimeout, TimeConstants.TenSeconds);

        var tableNameOnly = "features";
        _tableName = string.IsNullOrEmpty(dependencies.SchemaName)
            ? tableNameOnly
            : $"{Infrastructure.SchemaSearchPath.ValidateAndQuote(dependencies.SchemaName)}.{tableNameOnly}";
    }

    private static partial class Log
    {
        [LoggerMessage(
            EventId = 6120,
            Level = LogLevel.Warning,
            Message = "Slow {QueryType} query for layer {LayerId} (protocol {Protocol}) took {ElapsedMs}ms (rows: {RowCount}).")]
        public static partial void SlowQuery(
            ILogger logger,
            string queryType,
            int layerId,
            long elapsedMs,
            int? rowCount,
            string? protocol);

        [LoggerMessage(
            EventId = 6121,
            Level = LogLevel.Error,
            Message = "ApplyEdits failed for layer {LayerId} with {OperationCount} operations.")]
        public static partial void ApplyEditsFailed(ILogger logger, int layerId, int operationCount, Exception exception);
    }

    private void LogSlowQuery(string queryType, long elapsedMs, int layerId, int? rowCount)
    {
        if (_slowQueryThresholdMs <= 0 || elapsedMs < _slowQueryThresholdMs)
        {
            return;
        }

        var protocol = Activity.Current?.GetTagItem("honua.protocol")?.ToString();
        Log.SlowQuery(_logger, queryType, layerId, elapsedMs, rowCount, protocol);
    }

    private void RecordPerformanceQuery(string queryType, int layerId, long elapsedMs, int recordCount)
    {
        if (_performanceMonitor == null)
        {
            return;
        }

        _performanceMonitor.RecordDatabaseQuery(
            queryType,
            layerId.ToString(CultureInfo.InvariantCulture),
            TimeSpan.FromMilliseconds(elapsedMs),
            recordCount);
    }

    private static int GetTimeoutSeconds(TimeSpan timeout, int fallbackSeconds)
    {
        if (timeout <= TimeSpan.Zero)
        {
            return fallbackSeconds;
        }

        return (int)Math.Ceiling(timeout.TotalSeconds);
    }

    private static void ApplyCommandTimeout(NpgsqlCommand command, int timeoutSeconds)
    {
        if (timeoutSeconds > 0)
        {
            command.CommandTimeout = timeoutSeconds;
        }
    }

    private static NpgsqlCommand CreateSafeCommand(NpgsqlConnection connection, string sql)
    {
        // codeql[cs/sql-injection] SQL is built by vetted query builders with validated identifiers and parameters.
        return new NpgsqlCommand(sql, connection);
    }

    private async Task<NpgsqlCommand> CreateCommandAsync(
        NpgsqlConnection connection,
        string sql,
        Action<NpgsqlCommand> configureParameters,
        CancellationToken cancellationToken)
    {
        if (_statementCache == null)
        {
            var command = CreateSafeCommand(connection, sql);
            configureParameters(command);
            ApplyCommandTimeout(command, _queryTimeoutSeconds);
            return command;
        }

        var prepared = await _statementCache.GetOrCreatePreparedCommandAsync(
            connection,
            sql,
            configureParameters,
            cancellationToken).ConfigureAwait(false);

        if (prepared != null)
        {
            configureParameters(prepared);
            ApplyCommandTimeout(prepared, _queryTimeoutSeconds);
            return prepared;
        }

        var fallback = CreateSafeCommand(connection, sql);
        configureParameters(fallback);
        ApplyCommandTimeout(fallback, _queryTimeoutSeconds);
        return fallback;
    }
}
