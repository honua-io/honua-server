namespace Honua.Postgres.Features.FeatureStore;

/// <summary>
/// High-performance logging for monitored feature store operations using source generation for AOT compatibility.
/// </summary>
internal static partial class MonitoredFeatureStoreLog
{
    #region Query Operations (7000-7099)

    /// <summary>
    /// Logs when a query operation completes successfully.
    /// </summary>
    /// <param name="logger">The logger instance</param>
    /// <param name="layerId">The layer identifier</param>
    /// <param name="elapsedMs">Query execution time in milliseconds</param>
    [LoggerMessage(
        EventId = 7001,
        Level = LogLevel.Debug,
        Message = "Query completed for layer {LayerId} in {ElapsedMs:F2}ms")]
    public static partial void QueryCompleted(ILogger logger, string layerId, double elapsedMs);

    /// <summary>
    /// Logs when a query operation fails.
    /// </summary>
    /// <param name="logger">The logger instance</param>
    /// <param name="layerId">The layer identifier</param>
    /// <param name="errorMessage">Error message</param>
    /// <param name="exception">Exception that occurred</param>
    [LoggerMessage(
        EventId = 7002,
        Level = LogLevel.Error,
        Message = "Query failed for layer {LayerId}: {ErrorMessage}")]
    public static partial void QueryFailed(ILogger logger, string layerId, string errorMessage, Exception exception);

    /// <summary>
    /// Logs when a streaming query completes with record count.
    /// </summary>
    /// <param name="logger">The logger instance</param>
    /// <param name="layerId">The layer identifier</param>
    /// <param name="queryType">Type of query executed</param>
    /// <param name="recordCount">Number of records returned</param>
    /// <param name="elapsedMs">Query execution time in milliseconds</param>
    [LoggerMessage(
        EventId = 7003,
        Level = LogLevel.Information,
        Message = "Streaming {QueryType} completed for layer {LayerId}: {RecordCount} records in {ElapsedMs:F2}ms")]
    public static partial void StreamingQueryCompleted(ILogger logger, string layerId, string queryType, int recordCount, double elapsedMs);

    #endregion

    #region Count Operations (7100-7109)

    /// <summary>
    /// Logs when a count operation completes successfully.
    /// </summary>
    /// <param name="logger">The logger instance</param>
    /// <param name="layerId">The layer identifier</param>
    /// <param name="count">Number of records counted</param>
    /// <param name="elapsedMs">Operation execution time in milliseconds</param>
    [LoggerMessage(
        EventId = 7101,
        Level = LogLevel.Debug,
        Message = "Count completed for layer {LayerId}: {Count} records in {ElapsedMs:F2}ms")]
    public static partial void CountCompleted(ILogger logger, string layerId, long count, double elapsedMs);

    /// <summary>
    /// Logs when a count operation fails.
    /// </summary>
    /// <param name="logger">The logger instance</param>
    /// <param name="layerId">The layer identifier</param>
    /// <param name="errorMessage">Error message</param>
    /// <param name="exception">Exception that occurred</param>
    [LoggerMessage(
        EventId = 7102,
        Level = LogLevel.Error,
        Message = "Count failed for layer {LayerId}: {ErrorMessage}")]
    public static partial void CountFailed(ILogger logger, string layerId, string errorMessage, Exception exception);

    #endregion

    #region Get Operations (7110-7119)

    /// <summary>
    /// Logs when a get operation completes successfully.
    /// </summary>
    /// <param name="logger">The logger instance</param>
    /// <param name="layerId">The layer identifier</param>
    /// <param name="featureId">The feature identifier</param>
    /// <param name="found">Whether the feature was found</param>
    /// <param name="elapsedMs">Operation execution time in milliseconds</param>
    [LoggerMessage(
        EventId = 7111,
        Level = LogLevel.Debug,
        Message = "Get completed for layer {LayerId}, feature {FeatureId}: found={Found} in {ElapsedMs:F2}ms")]
    public static partial void GetCompleted(ILogger logger, string layerId, string featureId, bool found, double elapsedMs);

    /// <summary>
    /// Logs when a get operation fails.
    /// </summary>
    /// <param name="logger">The logger instance</param>
    /// <param name="layerId">The layer identifier</param>
    /// <param name="featureId">The feature identifier</param>
    /// <param name="errorMessage">Error message</param>
    /// <param name="exception">Exception that occurred</param>
    [LoggerMessage(
        EventId = 7112,
        Level = LogLevel.Error,
        Message = "Get failed for layer {LayerId}, feature {FeatureId}: {ErrorMessage}")]
    public static partial void GetFailed(ILogger logger, string layerId, string featureId, string errorMessage, Exception exception);

    #endregion

    #region Edit Operations (7120-7139)

    /// <summary>
    /// Logs when apply edits operation completes successfully.
    /// </summary>
    /// <param name="logger">The logger instance</param>
    /// <param name="layerId">The layer identifier</param>
    /// <param name="adds">Number of features added</param>
    /// <param name="updates">Number of features updated</param>
    /// <param name="deletes">Number of features deleted</param>
    /// <param name="elapsedMs">Operation execution time in milliseconds</param>
    [LoggerMessage(
        EventId = 7121,
        Level = LogLevel.Information,
        Message = "ApplyEdits completed for layer {LayerId}: +{Adds} ~{Updates} -{Deletes} in {ElapsedMs:F2}ms")]
    public static partial void ApplyEditsCompleted(ILogger logger, string layerId, int adds, int updates, int deletes, double elapsedMs);

    /// <summary>
    /// Logs when apply edits operation fails.
    /// </summary>
    /// <param name="logger">The logger instance</param>
    /// <param name="layerId">The layer identifier</param>
    /// <param name="errorMessage">Error message</param>
    /// <param name="exception">Exception that occurred</param>
    [LoggerMessage(
        EventId = 7122,
        Level = LogLevel.Error,
        Message = "ApplyEdits failed for layer {LayerId}: {ErrorMessage}")]
    public static partial void ApplyEditsFailed(ILogger logger, string layerId, string errorMessage, Exception exception);

    /// <summary>
    /// Logs when a create operation completes successfully.
    /// </summary>
    /// <param name="logger">The logger instance</param>
    /// <param name="layerId">The layer identifier</param>
    /// <param name="elapsedMs">Operation execution time in milliseconds</param>
    [LoggerMessage(
        EventId = 7131,
        Level = LogLevel.Debug,
        Message = "Create completed for layer {LayerId} in {ElapsedMs:F2}ms")]
    public static partial void CreateCompleted(ILogger logger, string layerId, double elapsedMs);

    /// <summary>
    /// Logs when a create operation fails.
    /// </summary>
    /// <param name="logger">The logger instance</param>
    /// <param name="layerId">The layer identifier</param>
    /// <param name="errorMessage">Error message</param>
    /// <param name="exception">Exception that occurred</param>
    [LoggerMessage(
        EventId = 7132,
        Level = LogLevel.Error,
        Message = "Create failed for layer {LayerId}: {ErrorMessage}")]
    public static partial void CreateFailed(ILogger logger, string layerId, string errorMessage, Exception exception);

    /// <summary>
    /// Logs when an update operation completes successfully.
    /// </summary>
    /// <param name="logger">The logger instance</param>
    /// <param name="layerId">The layer identifier</param>
    /// <param name="elapsedMs">Operation execution time in milliseconds</param>
    [LoggerMessage(
        EventId = 7133,
        Level = LogLevel.Debug,
        Message = "Update completed for layer {LayerId} in {ElapsedMs:F2}ms")]
    public static partial void UpdateCompleted(ILogger logger, string layerId, double elapsedMs);

    /// <summary>
    /// Logs when an update operation fails.
    /// </summary>
    /// <param name="logger">The logger instance</param>
    /// <param name="layerId">The layer identifier</param>
    /// <param name="errorMessage">Error message</param>
    /// <param name="exception">Exception that occurred</param>
    [LoggerMessage(
        EventId = 7134,
        Level = LogLevel.Error,
        Message = "Update failed for layer {LayerId}: {ErrorMessage}")]
    public static partial void UpdateFailed(ILogger logger, string layerId, string errorMessage, Exception exception);

    /// <summary>
    /// Logs when a delete operation completes successfully.
    /// </summary>
    /// <param name="logger">The logger instance</param>
    /// <param name="layerId">The layer identifier</param>
    /// <param name="featureId">The feature identifier</param>
    /// <param name="deleted">Whether the feature was deleted</param>
    /// <param name="elapsedMs">Operation execution time in milliseconds</param>
    [LoggerMessage(
        EventId = 7135,
        Level = LogLevel.Debug,
        Message = "Delete completed for layer {LayerId}, feature {FeatureId}: deleted={Deleted} in {ElapsedMs:F2}ms")]
    public static partial void DeleteCompleted(ILogger logger, string layerId, string featureId, bool deleted, double elapsedMs);

    /// <summary>
    /// Logs when a delete operation fails.
    /// </summary>
    /// <param name="logger">The logger instance</param>
    /// <param name="layerId">The layer identifier</param>
    /// <param name="featureId">The feature identifier</param>
    /// <param name="errorMessage">Error message</param>
    /// <param name="exception">Exception that occurred</param>
    [LoggerMessage(
        EventId = 7136,
        Level = LogLevel.Error,
        Message = "Delete failed for layer {LayerId}, feature {FeatureId}: {ErrorMessage}")]
    public static partial void DeleteFailed(ILogger logger, string layerId, string featureId, string errorMessage, Exception exception);

    #endregion

    #region Tile Operations (7140-7149)

    /// <summary>
    /// Logs when an MVT tile operation completes successfully.
    /// </summary>
    /// <param name="logger">The logger instance</param>
    /// <param name="layerId">The layer identifier</param>
    /// <param name="z">Tile zoom level</param>
    /// <param name="x">Tile X coordinate</param>
    /// <param name="y">Tile Y coordinate</param>
    /// <param name="sizeBytes">Size of generated tile in bytes</param>
    /// <param name="elapsedMs">Operation execution time in milliseconds</param>
    [LoggerMessage(
        EventId = 7141,
        Level = LogLevel.Debug,
        Message = "MVT tile completed for layer {LayerId} at {Z}/{X}/{Y}: {SizeBytes} bytes in {ElapsedMs:F2}ms")]
    public static partial void MvtTileCompleted(ILogger logger, string layerId, int z, int x, int y, int sizeBytes, double elapsedMs);

    /// <summary>
    /// Logs when an MVT tile operation fails.
    /// </summary>
    /// <param name="logger">The logger instance</param>
    /// <param name="layerId">The layer identifier</param>
    /// <param name="z">Tile zoom level</param>
    /// <param name="x">Tile X coordinate</param>
    /// <param name="y">Tile Y coordinate</param>
    /// <param name="errorMessage">Error message</param>
    /// <param name="exception">Exception that occurred</param>
    [LoggerMessage(
        EventId = 7142,
        Level = LogLevel.Error,
        Message = "MVT tile failed for layer {LayerId} at {Z}/{X}/{Y}: {ErrorMessage}")]
    public static partial void MvtTileFailed(ILogger logger, string layerId, int z, int x, int y, string errorMessage, Exception exception);

    #endregion

    #region Performance Monitoring (7200-7299)

    /// <summary>
    /// Logs when performance monitoring detects slow database operations.
    /// </summary>
    /// <param name="logger">The logger instance</param>
    /// <param name="operation">Database operation type</param>
    /// <param name="layerId">The layer identifier</param>
    /// <param name="elapsedMs">Operation execution time in milliseconds</param>
    /// <param name="thresholdMs">Slow operation threshold in milliseconds</param>
    [LoggerMessage(
        EventId = 7201,
        Level = LogLevel.Warning,
        Message = "Slow database {Operation} detected for layer {LayerId}: {ElapsedMs:F2}ms (threshold: {ThresholdMs:F0}ms)")]
    public static partial void SlowDatabaseOperation(ILogger logger, string operation, string layerId, double elapsedMs, double thresholdMs);

    /// <summary>
    /// Logs database operation performance metrics for analysis.
    /// </summary>
    /// <param name="logger">The logger instance</param>
    /// <param name="operation">Database operation type</param>
    /// <param name="layerId">The layer identifier</param>
    /// <param name="recordCount">Number of records processed</param>
    /// <param name="elapsedMs">Operation execution time in milliseconds</param>
    /// <param name="recordsPerSecond">Records processed per second</param>
    [LoggerMessage(
        EventId = 7202,
        Level = LogLevel.Information,
        Message = "Database {Operation} metrics for layer {LayerId}: {RecordCount} records in {ElapsedMs:F2}ms ({RecordsPerSecond:F0} records/sec)")]
    public static partial void DatabaseOperationMetrics(ILogger logger, string operation, string layerId, int recordCount, double elapsedMs, double recordsPerSecond);

    #endregion
}