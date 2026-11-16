// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Microsoft.Extensions.Logging;

namespace Honua.Db.Postgres.Features.FeatureStore;

internal static partial class MonitoredFeatureStoreLog
{
    #region Query Operations (7000-7099)

    [LoggerMessage(
        EventId = 7001,
        Level = LogLevel.Debug,
        Message = "Query completed for layer {LayerId} in {ElapsedMs:F2}ms")]
    public static partial void QueryCompleted(ILogger logger, string layerId, double elapsedMs);

    [LoggerMessage(
        EventId = 7002,
        Level = LogLevel.Error,
        Message = "Query failed for layer {LayerId}: {ErrorMessage}")]
    public static partial void QueryFailed(ILogger logger, string layerId, string errorMessage, Exception exception);

    [LoggerMessage(
        EventId = 7003,
        Level = LogLevel.Information,
        Message = "Streaming {QueryType} completed for layer {LayerId}: {RecordCount} records in {ElapsedMs:F2}ms")]
    public static partial void StreamingQueryCompleted(ILogger logger, string layerId, string queryType, int recordCount, double elapsedMs);

    #endregion

    #region Count Operations (7100-7109)

    [LoggerMessage(
        EventId = 7101,
        Level = LogLevel.Debug,
        Message = "Count completed for layer {LayerId}: {Count} records in {ElapsedMs:F2}ms")]
    public static partial void CountCompleted(ILogger logger, string layerId, long count, double elapsedMs);

    [LoggerMessage(
        EventId = 7102,
        Level = LogLevel.Error,
        Message = "Count failed for layer {LayerId}: {ErrorMessage}")]
    public static partial void CountFailed(ILogger logger, string layerId, string errorMessage, Exception exception);

    #endregion

    #region Get Operations (7110-7119)

    [LoggerMessage(
        EventId = 7111,
        Level = LogLevel.Debug,
        Message = "Get completed for layer {LayerId}, feature {FeatureId}: found={Found} in {ElapsedMs:F2}ms")]
    public static partial void GetCompleted(ILogger logger, string layerId, string featureId, bool found, double elapsedMs);

    [LoggerMessage(
        EventId = 7112,
        Level = LogLevel.Error,
        Message = "Get failed for layer {LayerId}, feature {FeatureId}: {ErrorMessage}")]
    public static partial void GetFailed(ILogger logger, string layerId, string featureId, string errorMessage, Exception exception);

    #endregion

    #region Edit Operations (7120-7139)

    [LoggerMessage(
        EventId = 7121,
        Level = LogLevel.Information,
        Message = "ApplyEdits completed for layer {LayerId}: +{Adds} ~{Updates} -{Deletes} in {ElapsedMs:F2}ms")]
    public static partial void ApplyEditsCompleted(ILogger logger, string layerId, int adds, int updates, int deletes, double elapsedMs);

    [LoggerMessage(
        EventId = 7122,
        Level = LogLevel.Error,
        Message = "ApplyEdits failed for layer {LayerId}: {ErrorMessage}")]
    public static partial void ApplyEditsFailed(ILogger logger, string layerId, string errorMessage, Exception exception);

    [LoggerMessage(
        EventId = 7131,
        Level = LogLevel.Debug,
        Message = "Create completed for layer {LayerId} in {ElapsedMs:F2}ms")]
    public static partial void CreateCompleted(ILogger logger, string layerId, double elapsedMs);

    [LoggerMessage(
        EventId = 7132,
        Level = LogLevel.Error,
        Message = "Create failed for layer {LayerId}: {ErrorMessage}")]
    public static partial void CreateFailed(ILogger logger, string layerId, string errorMessage, Exception exception);

    [LoggerMessage(
        EventId = 7133,
        Level = LogLevel.Debug,
        Message = "Update completed for layer {LayerId} in {ElapsedMs:F2}ms")]
    public static partial void UpdateCompleted(ILogger logger, string layerId, double elapsedMs);

    [LoggerMessage(
        EventId = 7134,
        Level = LogLevel.Error,
        Message = "Update failed for layer {LayerId}: {ErrorMessage}")]
    public static partial void UpdateFailed(ILogger logger, string layerId, string errorMessage, Exception exception);

    [LoggerMessage(
        EventId = 7135,
        Level = LogLevel.Debug,
        Message = "Delete completed for layer {LayerId}, feature {FeatureId}: deleted={Deleted} in {ElapsedMs:F2}ms")]
    public static partial void DeleteCompleted(ILogger logger, string layerId, string featureId, bool deleted, double elapsedMs);

    [LoggerMessage(
        EventId = 7136,
        Level = LogLevel.Error,
        Message = "Delete failed for layer {LayerId}, feature {FeatureId}: {ErrorMessage}")]
    public static partial void DeleteFailed(ILogger logger, string layerId, string featureId, string errorMessage, Exception exception);

    #endregion

    #region Tile Operations (7140-7149)

    [LoggerMessage(
        EventId = 7141,
        Level = LogLevel.Debug,
        Message = "MVT tile completed for layer {LayerId} at {Z}/{X}/{Y}: {SizeBytes} bytes in {ElapsedMs:F2}ms")]
    public static partial void MvtTileCompleted(ILogger logger, string layerId, int z, int x, int y, int sizeBytes, double elapsedMs);

    [LoggerMessage(
        EventId = 7142,
        Level = LogLevel.Error,
        Message = "MVT tile failed for layer {LayerId} at {Z}/{X}/{Y}: {ErrorMessage}")]
    public static partial void MvtTileFailed(ILogger logger, string layerId, int z, int x, int y, string errorMessage, Exception exception);

    #endregion

    #region SRID Operations (7150-7159)

    [LoggerMessage(
        EventId = 7151,
        Level = LogLevel.Warning,
        Message = "Failed to lookup SRID for layer {LayerId}")]
    public static partial void SridLookupFailed(ILogger logger, int layerId, Exception exception);

    [LoggerMessage(
        EventId = 7152,
        Level = LogLevel.Warning,
        Message = "Failed to check layer catalog availability")]
    public static partial void LayerCatalogCheckFailed(ILogger logger, Exception exception);

    #endregion

    #region Performance Monitoring (7200-7299)

    [LoggerMessage(
        EventId = 7201,
        Level = LogLevel.Warning,
        Message = "Slow database {Operation} detected for layer {LayerId}: {ElapsedMs:F2}ms (threshold: {ThresholdMs:F0}ms)")]
    public static partial void SlowDatabaseOperation(ILogger logger, string operation, string layerId, double elapsedMs, double thresholdMs);

    [LoggerMessage(
        EventId = 7202,
        Level = LogLevel.Information,
        Message = "Database {Operation} metrics for layer {LayerId}: {RecordCount} records in {ElapsedMs:F2}ms ({RecordsPerSecond:F0} records/sec)")]
    public static partial void DatabaseOperationMetrics(ILogger logger, string operation, string layerId, int recordCount, double elapsedMs, double recordsPerSecond);

    #endregion
}
