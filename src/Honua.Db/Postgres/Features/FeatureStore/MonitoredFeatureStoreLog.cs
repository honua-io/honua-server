// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Microsoft.Extensions.Logging;

namespace Honua.Db.Postgres.Features.FeatureStore;

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

    #endregion

    #region Count Operations (7100-7109)

    #endregion

    #region Get Operations (7110-7119)

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

    #endregion

    #region SRID Operations (7150-7159)

    /// <summary>
    /// Logs when SRID lookup fails.
    /// </summary>
    /// <param name="logger">The logger instance</param>
    /// <param name="layerId">The layer identifier</param>
    /// <param name="exception">Exception that occurred</param>
    [LoggerMessage(
        EventId = 7151,
        Level = LogLevel.Warning,
        Message = "Failed to lookup SRID for layer {LayerId}")]
    public static partial void SridLookupFailed(ILogger logger, int layerId, Exception exception);

    /// <summary>
    /// Logs when layer catalog availability check fails.
    /// </summary>
    /// <param name="logger">The logger instance</param>
    /// <param name="exception">Exception that occurred</param>
    [LoggerMessage(
        EventId = 7152,
        Level = LogLevel.Warning,
        Message = "Failed to check layer catalog availability")]
    public static partial void LayerCatalogCheckFailed(ILogger logger, Exception exception);

    #endregion

    #region Performance Monitoring (7200-7299)

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
