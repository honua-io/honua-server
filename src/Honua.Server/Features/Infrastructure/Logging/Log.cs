// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Infrastructure.Logging;

internal static partial class Log
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
        EventId = 4016,
        Level = LogLevel.Error,
        Message = "No database connection string configured. The application will not be able to serve data requests.")]
    public static partial void DatabaseConnectionStringMissingInProduction(ILogger logger);

    [LoggerMessage(
        EventId = 4015,
        Level = LogLevel.Information,
        Message = "Database migrations skipped")]
    public static partial void DatabaseMigrationsSkipped(ILogger logger);

    [LoggerMessage(
        EventId = 4031,
        Level = LogLevel.Information,
        Message = "Adopted raster provider baseline {ScriptName} into public.schema_versions without executing the migration")]
    public static partial void RasterProviderBaselineAdopted(ILogger logger, string scriptName);

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
        EventId = 4021,
        Level = LogLevel.Debug,
        Message = "Health check executed: Redis durability not attested ({Cause}); informational, the control plane and job runner remain available")]
    public static partial void DurableJobSubstrateDegraded(
        ILogger logger, Honua.Core.Features.Capabilities.DurableJobSubstrateCause cause);

    [LoggerMessage(
        EventId = 4030,
        Level = LogLevel.Debug,
        Message = "Correlation ID established: {CorrelationId} for {RequestPath}")]
    public static partial void CorrelationIdEstablished(
        ILogger logger, string correlationId, string requestPath);

    [LoggerMessage(
        EventId = 4040,
        Level = LogLevel.Warning,
        Message = "Database connection retry attempt {Attempt}: {ErrorMessage}")]
    public static partial void ConnectionRetry(
        ILogger logger, int attempt, string errorMessage);

    public static void RequestProcessingStarted(
        ILogger logger, string method, string path, long contentLength, double timeoutSeconds)
        => Honua.Infrastructure.Logging.ProtocolRequestLog.RequestProcessingStarted(logger, method, path, contentLength, timeoutSeconds);

    public static void RequestTimedOut(
        ILogger logger, string path, double timeoutSeconds)
        => Honua.Infrastructure.Logging.ProtocolRequestLog.RequestTimedOut(logger, path, timeoutSeconds);

    public static void PayloadSizeExceeded(
        ILogger logger, string path, long actualSize, long maxSize)
        => Honua.Infrastructure.Logging.ProtocolRequestLog.PayloadSizeExceeded(logger, path, actualSize, maxSize);

    public static void PayloadSizeValidationSkipped(
        ILogger logger, string path)
        => Honua.Infrastructure.Logging.ProtocolRequestLog.PayloadSizeValidationSkipped(logger, path);

    public static void RequestProcessingError(
        ILogger logger, string path, string exceptionType, string errorMessage, Exception exception)
        => Honua.Infrastructure.Logging.ProtocolRequestLog.RequestProcessingError(logger, path, exceptionType, errorMessage, exception);

    [LoggerMessage(
        EventId = 4055,
        Level = LogLevel.Information,
        Message = "PostGIS preflight check starting")]
    public static partial void PostGisPreflightCheckStarting(ILogger logger);

    [LoggerMessage(
        EventId = 4056,
        Level = LogLevel.Information,
        Message = "PostGIS preflight check passed: engine={EngineVersion}, PostGIS={PostGisVersion}")]
    public static partial void PostGisPreflightCheckPassed(ILogger logger, string engineVersion, string postGisVersion);

    [LoggerMessage(
        EventId = 4057,
        Level = LogLevel.Warning,
        Message = "PostGIS preflight check failed: {ErrorMessage}. Continuing in Development mode.")]
    public static partial void PostGisPreflightCheckFailedDevelopment(ILogger logger, string errorMessage);

    [LoggerMessage(
        EventId = 4058,
        Level = LogLevel.Information,
        Message = "PostGIS preflight check skipped: no database compatibility checker is registered.")]
    public static partial void PostGisPreflightCheckSkipped(ILogger logger);

    [LoggerMessage(
        EventId = 4059,
        Level = LogLevel.Information,
        Message = "Server instance identity resolved: {InstanceId} (source={InstanceIdSource}, startedAt={InstanceStartedAt:O})")]
    public static partial void ServerInstanceIdentityResolved(
        ILogger logger, string instanceId, string instanceIdSource, DateTimeOffset instanceStartedAt);

    #endregion

    #region Tracing Operations (6000-6999)

    [LoggerMessage(
        EventId = 6001,
        Level = LogLevel.Debug,
        Message = "Trace started: {OperationName} [TraceId: {TraceId}, SpanId: {SpanId}]")]
    public static partial void TraceStarted(
        ILogger logger, string operationName, string traceId, string spanId);

    [LoggerMessage(
        EventId = 6002,
        Level = LogLevel.Debug,
        Message = "Trace completed: {OperationName} in {ElapsedMs:F2}ms [TraceId: {TraceId}]")]
    public static partial void TraceCompleted(
        ILogger logger, string operationName, double elapsedMs, string traceId);

    [LoggerMessage(
        EventId = 6003,
        Level = LogLevel.Warning,
        Message = "Trace error: {OperationName} failed with {ErrorType} [TraceId: {TraceId}]")]
    public static partial void TraceError(
        ILogger logger, string operationName, string errorType, string traceId, Exception exception);

    [LoggerMessage(
        EventId = 6010,
        Level = LogLevel.Debug,
        Message = "Protocol detected: {Protocol} for path {RequestPath}")]
    public static partial void ProtocolDetected(
        ILogger logger, string protocol, string requestPath);

    [LoggerMessage(
        EventId = 6020,
        Level = LogLevel.Debug,
        Message = "Feature operation: {Operation} on layer {LayerId}, {FeatureCount} features [Protocol: {Protocol}]")]
    public static partial void FeatureOperation(
        ILogger logger, string operation, string layerId, int featureCount, string protocol);

    [LoggerMessage(
        EventId = 6030,
        Level = LogLevel.Debug,
        Message = "Tile generated: z/{Z}/x/{X}/y/{Y} for layer {LayerId} in {ElapsedMs:F2}ms")]
    public static partial void TileGenerated(
        ILogger logger, int z, int x, int y, string layerId, double elapsedMs);

    [LoggerMessage(
        EventId = 6040,
        Level = LogLevel.Information,
        Message = "OpenTelemetry configured: OTLP export {OtlpEnabled}, sampling rate {SamplingRate}")]
    public static partial void OpenTelemetryConfigured(
        ILogger logger, bool otlpEnabled, double samplingRate);

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

    public static void UnhandledException(
        ILogger logger, string requestPath, string correlationId, string errorMessage, Exception exception)
        => Honua.Infrastructure.Logging.ProtocolRequestLog.UnhandledException(logger, requestPath, correlationId, errorMessage, exception);

    [LoggerMessage(
        EventId = 5010,
        Level = LogLevel.Critical,
        Message = "Application startup failed: {ErrorMessage}")]
    public static partial void ApplicationStartupFailed(
        ILogger logger, string errorMessage, Exception exception);

    [LoggerMessage(
        EventId = 5011,
        Level = LogLevel.Critical,
        Message = "PostGIS preflight check failed ({CauseClass}): {ErrorMessage}. Startup aborted.")]
    public static partial void PostGisPreflightCheckFailedCritical(ILogger logger, string causeClass, string errorMessage);

    [LoggerMessage(
        EventId = 5012,
        Level = LogLevel.Warning,
        Message = "{Message}")]
    public static partial void DatabaseStartupDegraded(ILogger logger, string message);

    #endregion
}
