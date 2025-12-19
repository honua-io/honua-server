// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Features.Infrastructure.Logging;

/// <summary>
/// Source-generated logging methods for AOT compatibility
/// </summary>
/// <remarks>
/// Event ID ranges:
/// - 1000-1999: Query operations
/// - 2000-2999: Edit operations
/// - 3000-3999: Performance warnings
/// - 4000-4999: Infrastructure operations
/// - 5000-5999: Errors
/// </remarks>
public static partial class Log
{
    #region Query Operations (1000-1999)

    [LoggerMessage(
        EventId = 1001,
        Level = LogLevel.Information,
        Message = "Query executed on layer {LayerId}: {FeatureCount} features returned in {ElapsedMs:F2}ms")]
    public static partial void QueryExecuted(
        ILogger logger, string layerId, int featureCount, double elapsedMs);

    [LoggerMessage(
        EventId = 1002,
        Level = LogLevel.Information,
        Message = "Spatial query on layer {LayerId}: {SpatialRel} with {GeometryType}, {FeatureCount} features")]
    public static partial void SpatialQueryExecuted(
        ILogger logger, string layerId, string spatialRel, string geometryType, int featureCount);

    [LoggerMessage(
        EventId = 1003,
        Level = LogLevel.Debug,
        Message = "Query parameters: LayerId={LayerId}, Where='{WhereClause}', OutFields='{OutFields}', ReturnGeometry={ReturnGeometry}")]
    public static partial void QueryParameters(
        ILogger logger, string layerId, string? whereClause, string? outFields, bool returnGeometry);

    #endregion

    #region Edit Operations (2000-2999)

    [LoggerMessage(
        EventId = 2001,
        Level = LogLevel.Information,
        Message = "ApplyEdits on layer {LayerId}: +{Adds} ~{Updates} -{Deletes} in {ElapsedMs:F2}ms")]
    public static partial void ApplyEditsCompleted(
        ILogger logger, string layerId, int adds, int updates, int deletes, double elapsedMs);

    [LoggerMessage(
        EventId = 2002,
        Level = LogLevel.Warning,
        Message = "ApplyEdits partial failure on layer {LayerId}: {SuccessCount} succeeded, {FailureCount} failed")]
    public static partial void ApplyEditsPartialFailure(
        ILogger logger, string layerId, int successCount, int failureCount);

    [LoggerMessage(
        EventId = 2010,
        Level = LogLevel.Debug,
        Message = "Transaction {TransactionId} started for layer {LayerId}")]
    public static partial void TransactionStarted(ILogger logger, string transactionId, string layerId);

    [LoggerMessage(
        EventId = 2011,
        Level = LogLevel.Debug,
        Message = "Transaction {TransactionId} committed: {OperationCount} operations")]
    public static partial void TransactionCommitted(ILogger logger, string transactionId, int operationCount);

    [LoggerMessage(
        EventId = 2012,
        Level = LogLevel.Warning,
        Message = "Transaction {TransactionId} rolled back: {Reason}")]
    public static partial void TransactionRolledBack(ILogger logger, string transactionId, string reason);

    [LoggerMessage(
        EventId = 2013,
        Level = LogLevel.Warning,
        Message = "Transaction retry attempt {Attempt}: {ErrorMessage}")]
    public static partial void TransactionRetry(ILogger logger, int attempt, string errorMessage);

    #endregion

    #region Performance Warnings (3000-3999)

    [LoggerMessage(
        EventId = 3001,
        Level = LogLevel.Warning,
        Message = "Slow query on layer {LayerId}: {ElapsedMs:F2}ms exceeded threshold of {ThresholdMs}ms")]
    public static partial void SlowQuery(
        ILogger logger, string layerId, double elapsedMs, int thresholdMs);

    [LoggerMessage(
        EventId = 3002,
        Level = LogLevel.Warning,
        Message = "Large result set on layer {LayerId}: {FeatureCount} features, consider paging")]
    public static partial void LargeResultSet(
        ILogger logger, string layerId, int featureCount);

    [LoggerMessage(
        EventId = 3003,
        Level = LogLevel.Warning,
        Message = "Database connection pool exhausted: {ActiveConnections} active, {PoolSize} pool size")]
    public static partial void ConnectionPoolExhausted(
        ILogger logger, int activeConnections, int poolSize);

    #endregion

    #region Infrastructure Operations (4000-4999)

    [LoggerMessage(
        EventId = 4001,
        Level = LogLevel.Information,
        Message = "Application starting: Version {Version}, Environment {Environment}")]
    public static partial void ApplicationStarting(
        ILogger logger, string version, string environment);

    [LoggerMessage(
        EventId = 4002,
        Level = LogLevel.Information,
        Message = "Application stopping gracefully")]
    public static partial void ApplicationStopping(ILogger logger);

    [LoggerMessage(
        EventId = 4010,
        Level = LogLevel.Information,
        Message = "Database migrations starting")]
    public static partial void DatabaseMigrationsStarting(ILogger logger);

    [LoggerMessage(
        EventId = 4011,
        Level = LogLevel.Information,
        Message = "Database migrations completed: {ScriptCount} scripts applied")]
    public static partial void DatabaseMigrationsCompleted(ILogger logger, int scriptCount);

    [LoggerMessage(
        EventId = 4012,
        Level = LogLevel.Information,
        Message = "No database migrations to apply")]
    public static partial void NoDatabaseMigrationsToApply(ILogger logger);

    [LoggerMessage(
        EventId = 4013,
        Level = LogLevel.Information,
        Message = "Database connection string 'DefaultConnection' not configured - skipping migrations")]
    public static partial void DatabaseConnectionStringNotConfigured(ILogger logger);

    [LoggerMessage(
        EventId = 4014,
        Level = LogLevel.Debug,
        Message = "Applied migration script: {ScriptName}")]
    public static partial void MigrationScriptApplied(ILogger logger, string scriptName);

    [LoggerMessage(
        EventId = 4020,
        Level = LogLevel.Information,
        Message = "Health check executed: {CheckName} = {Status} in {ElapsedMs:F2}ms")]
    public static partial void HealthCheckExecuted(
        ILogger logger, string checkName, string status, double elapsedMs);

    [LoggerMessage(
        EventId = 4030,
        Level = LogLevel.Debug,
        Message = "Correlation ID established: {CorrelationId} for {RequestPath}")]
    public static partial void CorrelationIdEstablished(
        ILogger logger, string correlationId, string requestPath);

    #endregion

    #region Errors (5000-5999)

    [LoggerMessage(
        EventId = 5001,
        Level = LogLevel.Error,
        Message = "Query failed on layer {LayerId}: {ErrorMessage}")]
    public static partial void QueryFailed(
        ILogger logger, string layerId, string errorMessage, Exception? exception = null);

    [LoggerMessage(
        EventId = 5002,
        Level = LogLevel.Error,
        Message = "Database connection failed: {ErrorMessage}")]
    public static partial void DatabaseConnectionFailed(
        ILogger logger, string errorMessage, Exception exception);

    [LoggerMessage(
        EventId = 5003,
        Level = LogLevel.Error,
        Message = "Database migration failed: {ErrorMessage}")]
    public static partial void DatabaseMigrationFailed(
        ILogger logger, string errorMessage, Exception exception);

    [LoggerMessage(
        EventId = 5004,
        Level = LogLevel.Error,
        Message = "ApplyEdits failed on layer {LayerId}: {ErrorMessage}")]
    public static partial void ApplyEditsFailed(
        ILogger logger, string layerId, string errorMessage, Exception exception);

    [LoggerMessage(
        EventId = 5005,
        Level = LogLevel.Error,
        Message = "Unhandled exception in {RequestPath}: {ErrorMessage}")]
    public static partial void UnhandledException(
        ILogger logger, string requestPath, string errorMessage, Exception exception);

    [LoggerMessage(
        EventId = 5010,
        Level = LogLevel.Critical,
        Message = "Application startup failed: {ErrorMessage}")]
    public static partial void ApplicationStartupFailed(
        ILogger logger, string errorMessage, Exception exception);

    #endregion
}
