// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Domain;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Honua.Postgres.Features.Infrastructure.Caching;

/// <summary>
/// Background service that pre-prepares known high-frequency queries
/// </summary>
/// <remarks>
/// <para>
/// This service identifies and pre-prepares SQL statements that are known to be
/// executed frequently across the application. By preparing these statements
/// early, we ensure optimal performance from the first execution rather than
/// waiting for the automatic caching threshold to be reached.
/// </para>
/// <para>
/// PERFORMANCE IMPACT:
/// - Reduces initial query latency for common operations
/// - Improves cache hit ratios by ensuring important queries are always prepared
/// - Provides predictable performance characteristics for critical paths
/// </para>
/// <para>
/// SAFETY: Only prepares parameterized queries that have been verified safe.
/// All queries maintain existing security patterns and parameter binding.
/// </para>
/// </remarks>
internal sealed class HighFrequencyQueryPreparationService : BackgroundService
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly PreparedStatementCache _statementCache;
    private readonly ILogger<HighFrequencyQueryPreparationService> _logger;
    private readonly QueryCacheOptions _options;
    private readonly HighPriorityQuery[] _highPriorityQueries;

    /// <summary>
    /// High-priority queries that should be prepared immediately
    /// </summary>
    private static HighPriorityQuery[] BuildHighPriorityQueries(string featuresTableName)
    {
        return new[]
        {
            // Layer catalog queries - executed on every layer metadata request
            new HighPriorityQuery("layer_by_id", """
                SELECT
                    l.layer_id,
                    l.layer_name,
                    l.description,
                    l.geometry_type,
                    l.srid,
                    l.min_scale,
                    l.max_scale,
                    l.default_visibility,
                    ST_XMin(l.extent) as xmin,
                    ST_YMin(l.extent) as ymin,
                    ST_XMax(l.extent) as xmax,
                    ST_YMax(l.extent) as ymax,
                    ST_SRID(l.extent) as extent_srid
                FROM honua.layers l
                WHERE l.layer_id = $1
                  AND l.enabled = TRUE
                """),

            // Layer existence check - used for validation
            new HighPriorityQuery("layer_exists", "SELECT 1 FROM honua.layers WHERE layer_id = $1 AND enabled = TRUE"),

            // Service existence check - used for validation
            new HighPriorityQuery("service_exists", "SELECT 1 FROM honua.services WHERE LOWER(service_name) = LOWER($1)"),

            // Layer SRID lookup - used for spatial queries
            new HighPriorityQuery("layer_srid", "SELECT srid FROM honua.layers WHERE layer_id = $1 AND enabled = TRUE"),

            // Feature count query - used for metadata and pagination
            new HighPriorityQuery("feature_count", $"SELECT COUNT(*) FROM {featuresTableName} WHERE layer_id = $1"),

            // Feature extent query - used for spatial metadata
            new HighPriorityQuery("feature_extent", $"""
                SELECT
                    ST_XMin(extent) as xmin,
                    ST_YMin(extent) as ymin,
                    ST_XMax(extent) as xmax,
                    ST_YMax(extent) as ymax
                FROM (
                    SELECT ST_Extent(geometry) as extent
                    FROM {featuresTableName}
                    WHERE layer_id = $1
                ) e
                """),

            // Health check query - executed regularly by monitoring
            new HighPriorityQuery("health_check", "SELECT 1"),

            // Attachment count by feature - used for feature metadata
            new HighPriorityQuery("attachment_count", """
                SELECT COUNT(*)
                FROM honua.attachments
                WHERE feature_id = $1 AND layer_id = $2
                """),

            // Layer fields metadata - used for feature schema
            new HighPriorityQuery("layer_fields", """
                SELECT
                    field_name,
                    field_type,
                    field_length,
                    is_nullable,
                    default_value,
                    field_alias
                FROM honua.layer_fields
                WHERE layer_id = $1
                ORDER BY field_order
                """)
        };
    }

    private sealed record HighPriorityQuery(string Name, string Sql);

    public HighFrequencyQueryPreparationService(
        NpgsqlDataSource dataSource,
        PreparedStatementCache statementCache,
        IOptions<QueryCacheOptions> options,
        IConfiguration configuration,
        ILogger<HighFrequencyQueryPreparationService> logger)
    {
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        _statementCache = statementCache ?? throw new ArgumentNullException(nameof(statementCache));
        _options = options.Value ?? throw new ArgumentNullException(nameof(options));
        ArgumentNullException.ThrowIfNull(configuration);
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        var featuresTableName = DatabaseSchema.GetFeaturesTableName(configuration["Database:Schema"]);
        _highPriorityQueries = BuildHighPriorityQueries(featuresTableName);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.EnableAutomaticCaching)
        {
            HighFrequencyQueryPreparationLog.QueryCachingDisabled(_logger);
            return;
        }

        HighFrequencyQueryPreparationLog.PreparationStarting(_logger, _highPriorityQueries.Length);

        try
        {
            // Wait a short time for the application to fully start
            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);

            await PrepareHighPriorityQueriesAsync(stoppingToken);

            // Log cache statistics after preparation
            var stats = _statementCache.GetStatistics();
            HighFrequencyQueryPreparationLog.PreparationCompleted(_logger, stats.PreparedStatements);
        }
        catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
        {
            HighFrequencyQueryPreparationLog.PreparationFailed(_logger, ex);
        }
    }

    private async Task PrepareHighPriorityQueriesAsync(CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);

        var successCount = 0;
        var failureCount = 0;
        var skippedCount = 0;

        foreach (var query in _highPriorityQueries)
        {
            try
            {
                var stopwatch = System.Diagnostics.Stopwatch.StartNew();

                var prepared = await _statementCache.PreparePriorityStatementAsync(
                    (NpgsqlConnection)connection,
                    query.Sql,
                    query.Name,
                    cancellationToken);

                if (prepared == null)
                {
                    stopwatch.Stop();
                    skippedCount++;
                    HighFrequencyQueryPreparationLog.PreparedHighPriorityStatementSkipped(_logger, query.Name);
                    continue;
                }

                stopwatch.Stop();
                successCount++;

                prepared.Dispose();

                HighFrequencyQueryPreparationLog.PreparedHighPriorityStatement(
                    _logger,
                    query.Name,
                    stopwatch.ElapsedMilliseconds);
            }
            catch (Exception ex)
            {
                failureCount++;
                HighFrequencyQueryPreparationLog.PreparedHighPriorityStatementFailed(
                    _logger,
                    query.Name,
                    ex);
            }
        }

        HighFrequencyQueryPreparationLog.PreparationSummary(_logger, successCount, failureCount, skippedCount);
    }
}

internal static partial class HighFrequencyQueryPreparationLog
{
    [LoggerMessage(
        EventId = 8601,
        Level = LogLevel.Information,
        Message = "Query caching disabled, skipping high-frequency query preparation")]
    public static partial void QueryCachingDisabled(ILogger logger);

    [LoggerMessage(
        EventId = 8602,
        Level = LogLevel.Information,
        Message = "Starting high-frequency query preparation for {QueryCount} queries")]
    public static partial void PreparationStarting(ILogger logger, int queryCount);

    [LoggerMessage(
        EventId = 8603,
        Level = LogLevel.Information,
        Message = "High-frequency query preparation completed. Cache contains {PreparedCount} prepared statements")]
    public static partial void PreparationCompleted(ILogger logger, int preparedCount);

    [LoggerMessage(
        EventId = 8604,
        Level = LogLevel.Error,
        Message = "Error during high-frequency query preparation")]
    public static partial void PreparationFailed(ILogger logger, Exception exception);

    [LoggerMessage(
        EventId = 8605,
        Level = LogLevel.Debug,
        Message = "Prepared high-priority statement: {StatementName} in {ElapsedMs}ms")]
    public static partial void PreparedHighPriorityStatement(ILogger logger, string statementName, long elapsedMs);

    [LoggerMessage(
        EventId = 8606,
        Level = LogLevel.Warning,
        Message = "Failed to prepare high-priority statement: {StatementName}. Query will use regular execution.")]
    public static partial void PreparedHighPriorityStatementFailed(ILogger logger, string statementName, Exception exception);

    [LoggerMessage(
        EventId = 8607,
        Level = LogLevel.Information,
        Message = "High-frequency query preparation completed: {SuccessCount} successful, {FailureCount} failed, {SkippedCount} skipped")]
    public static partial void PreparationSummary(ILogger logger, int successCount, int failureCount, int skippedCount);

    [LoggerMessage(
        EventId = 8608,
        Level = LogLevel.Debug,
        Message = "Skipped high-priority statement preparation: {StatementName}")]
    public static partial void PreparedHighPriorityStatementSkipped(ILogger logger, string statementName);
}
