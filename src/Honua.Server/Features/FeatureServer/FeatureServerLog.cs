// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Features.FeatureServer;

/// <summary>
/// Structured logging for FeatureServer endpoints with source generation (AOT compatible)
/// </summary>
internal static partial class FeatureServerLog
{
    // Service metadata events (2000-2099)

    /// <summary>
    /// Logs when service metadata is requested.
    /// </summary>
    /// <param name="logger">The logger instance.</param>
    /// <param name="serviceId">The service identifier.</param>
    [LoggerMessage(
        EventId = 2001,
        Level = LogLevel.Information,
        Message = "Service metadata requested: {ServiceId}")]
    public static partial void ServiceMetadataRequested(ILogger logger, string serviceId);

    /// <summary>
    /// Logs when service metadata is successfully returned.
    /// </summary>
    /// <param name="logger">The logger instance.</param>
    /// <param name="serviceId">The service identifier.</param>
    /// <param name="layerCount">The number of layers in the service.</param>
    [LoggerMessage(
        EventId = 2002,
        Level = LogLevel.Information,
        Message = "Service metadata returned: {ServiceId} with {LayerCount} layers")]
    public static partial void ServiceMetadataReturned(ILogger logger, string serviceId, int layerCount);

    /// <summary>
    /// Logs when a requested service is not found.
    /// </summary>
    /// <param name="logger">The logger instance.</param>
    /// <param name="serviceId">The service identifier that was not found.</param>
    [LoggerMessage(
        EventId = 2003,
        Level = LogLevel.Warning,
        Message = "Service not found: {ServiceId}")]
    public static partial void ServiceNotFound(ILogger logger, string serviceId);

    /// <summary>
    /// Logs when service metadata request fails.
    /// </summary>
    /// <param name="logger">The logger instance.</param>
    /// <param name="serviceId">The service identifier.</param>
    /// <param name="errorMessage">The error message.</param>
    /// <param name="exception">The optional exception that caused the failure.</param>
    [LoggerMessage(
        EventId = 2004,
        Level = LogLevel.Error,
        Message = "Service metadata request failed for {ServiceId}: {ErrorMessage}")]
    public static partial void ServiceMetadataFailed(ILogger logger, string serviceId, string errorMessage, Exception? exception = null);

    // Layer metadata events (2100-2199)

    /// <summary>
    /// Logs when layer metadata is requested.
    /// </summary>
    /// <param name="logger">The logger instance.</param>
    /// <param name="serviceId">The service identifier.</param>
    /// <param name="layerId">The layer identifier.</param>
    [LoggerMessage(
        EventId = 2101,
        Level = LogLevel.Information,
        Message = "Layer metadata requested: {ServiceId}/FeatureServer/{LayerId}")]
    public static partial void LayerMetadataRequested(ILogger logger, string serviceId, int layerId);

    /// <summary>
    /// Logs when layer metadata is successfully returned.
    /// </summary>
    /// <param name="logger">The logger instance.</param>
    /// <param name="serviceId">The service identifier.</param>
    /// <param name="layerId">The layer identifier.</param>
    /// <param name="layerName">The layer name.</param>
    [LoggerMessage(
        EventId = 2102,
        Level = LogLevel.Information,
        Message = "Layer metadata returned: {ServiceId}/FeatureServer/{LayerId} ({LayerName})")]
    public static partial void LayerMetadataReturned(ILogger logger, string serviceId, int layerId, string layerName);

    /// <summary>
    /// Logs when a requested layer is not found.
    /// </summary>
    /// <param name="logger">The logger instance.</param>
    /// <param name="serviceId">The service identifier.</param>
    /// <param name="layerId">The layer identifier that was not found.</param>
    [LoggerMessage(
        EventId = 2103,
        Level = LogLevel.Warning,
        Message = "Layer not found: {ServiceId}/FeatureServer/{LayerId}")]
    public static partial void LayerNotFound(ILogger logger, string serviceId, int layerId);

    /// <summary>
    /// Logs when layer metadata request fails.
    /// </summary>
    /// <param name="logger">The logger instance.</param>
    /// <param name="serviceId">The service identifier.</param>
    /// <param name="layerId">The layer identifier.</param>
    /// <param name="errorMessage">The error message.</param>
    /// <param name="exception">The optional exception that caused the failure.</param>
    [LoggerMessage(
        EventId = 2104,
        Level = LogLevel.Error,
        Message = "Layer metadata request failed for {ServiceId}/FeatureServer/{LayerId}: {ErrorMessage}")]
    public static partial void LayerMetadataFailed(ILogger logger, string serviceId, int layerId, string errorMessage, Exception? exception = null);

    // Query events (2200-2299)

    /// <summary>
    /// Logs when a query is requested.
    /// </summary>
    /// <param name="logger">The logger instance.</param>
    /// <param name="serviceId">The service identifier.</param>
    /// <param name="layerId">The layer identifier.</param>
    /// <param name="hasWhereClause">Whether the request supplied a WHERE clause.</param>
    [LoggerMessage(
        EventId = 2201,
        Level = LogLevel.Information,
        Message = "Query requested: {ServiceId}/FeatureServer/{LayerId}/query (WHERE clause present: {HasWhereClause})")]
    public static partial void QueryRequested(ILogger logger, string serviceId, int layerId, bool hasWhereClause);

    /// <summary>
    /// Logs when a query is successfully completed.
    /// </summary>
    /// <param name="logger">The logger instance.</param>
    /// <param name="serviceId">The service identifier.</param>
    /// <param name="layerId">The layer identifier.</param>
    /// <param name="featureCount">The number of features returned.</param>
    /// <param name="totalCount">The total number of features matching the query.</param>
    [LoggerMessage(
        EventId = 2202,
        Level = LogLevel.Information,
        Message = "Query completed: {ServiceId}/FeatureServer/{LayerId} returned {FeatureCount} of {TotalCount} features")]
    public static partial void QueryCompleted(ILogger logger, string serviceId, int layerId, int featureCount, long totalCount);

    /// <summary>
    /// Logs when a query fails.
    /// </summary>
    /// <param name="logger">The logger instance.</param>
    /// <param name="serviceId">The service identifier.</param>
    /// <param name="layerId">The layer identifier.</param>
    /// <param name="errorMessage">The error message.</param>
    /// <param name="exception">The optional exception that caused the failure.</param>
    [LoggerMessage(
        EventId = 2203,
        Level = LogLevel.Error,
        Message = "Query failed for {ServiceId}/FeatureServer/{LayerId}: {ErrorMessage}")]
    public static partial void QueryFailed(ILogger logger, string serviceId, int layerId, string errorMessage, Exception? exception = null);

    /// <summary>
    /// Logs when a query parameter exceeds its limit.
    /// </summary>
    /// <param name="logger">The logger instance.</param>
    /// <param name="parameter">The parameter name that exceeded the limit.</param>
    /// <param name="actualValue">The actual value provided.</param>
    /// <param name="limitValue">The maximum allowed value.</param>
    [LoggerMessage(
        EventId = 2204,
        Level = LogLevel.Warning,
        Message = "Query limit exceeded: {Parameter} value {ActualValue} exceeds limit {LimitValue}")]
    public static partial void QueryLimitExceeded(ILogger logger, string parameter, int actualValue, int limitValue);

    /// <summary>
    /// Logs when a query parameter is invalid.
    /// </summary>
    /// <param name="logger">The logger instance.</param>
    /// <param name="parameter">The parameter name that is invalid.</param>
    /// <param name="actualValue">The actual value provided.</param>
    [LoggerMessage(
        EventId = 2205,
        Level = LogLevel.Warning,
        Message = "Query parameter invalid: {Parameter} value {ActualValue}")]
    public static partial void QueryParameterInvalid(ILogger logger, string parameter, int actualValue);

    /// <summary>
    /// Logs query execution performance metrics.
    /// </summary>
    /// <param name="logger">The logger instance.</param>
    /// <param name="operation">The type of operation executed.</param>
    /// <param name="serviceId">The service identifier.</param>
    /// <param name="layerId">The layer identifier.</param>
    /// <param name="elapsedMilliseconds">The execution time in milliseconds.</param>
    [LoggerMessage(
        EventId = 2206,
        Level = LogLevel.Information,
        Message = "Query {Operation} executed: {ServiceId}/FeatureServer/{LayerId} in {ElapsedMilliseconds} ms")]
    public static partial void QueryExecuted(ILogger logger, string operation, string serviceId, int layerId, double elapsedMilliseconds);

    /// <summary>
    /// Logs when a GeoParquet attribute conversion fails and the value is treated as null.
    /// </summary>
    /// <param name="logger">The logger instance.</param>
    /// <param name="fieldName">The field whose value could not be converted.</param>
    /// <param name="valueType">The CLR type name of the unconvertible value.</param>
    /// <param name="exception">The conversion exception.</param>
    [LoggerMessage(
        EventId = 2207,
        Level = LogLevel.Debug,
        Message = "GeoParquet conversion failed for field '{FieldName}' (value type: {ValueType}), value treated as null")]
    public static partial void GeoParquetConversionFailed(ILogger logger, string fieldName, string valueType, Exception exception);

    /// <summary>
    /// Logs when a geometry WKB value cannot be parsed during GeoParquet export and is treated as null.
    /// </summary>
    /// <param name="logger">The logger instance.</param>
    /// <param name="featureId">The object ID of the feature with corrupt geometry.</param>
    /// <param name="wkbLength">The byte length of the unparseable WKB payload.</param>
    /// <param name="exception">The parse exception.</param>
    [LoggerMessage(
        EventId = 2208,
        Level = LogLevel.Debug,
        Message = "GeoParquet export: corrupt WKB for feature {FeatureId} ({WkbLength} bytes), geometry treated as null")]
    public static partial void GeoParquetCorruptGeometry(ILogger logger, long featureId, int wkbLength, Exception exception);

    // ApplyEdits events (2300-2399)

    /// <summary>
    /// Logs when an ApplyEdits request is received.
    /// </summary>
    /// <param name="logger">The logger instance.</param>
    /// <param name="serviceId">The service identifier.</param>
    /// <param name="layerId">The layer identifier.</param>
    /// <param name="addCount">The number of features to add.</param>
    /// <param name="updateCount">The number of features to update.</param>
    /// <param name="deleteCount">The number of features to delete.</param>
    [LoggerMessage(Level = LogLevel.Information, Message = "ApplyEdits requested for service '{ServiceId}' layer {LayerId} with {AddCount} adds, {UpdateCount} updates, {DeleteCount} deletes")]
    public static partial void ApplyEditsRequested(ILogger logger, string serviceId, int layerId, int addCount, int updateCount, int deleteCount);

    /// <summary>
    /// Logs when an ApplyEdits operation is completed.
    /// </summary>
    /// <param name="logger">The logger instance.</param>
    /// <param name="serviceId">The service identifier.</param>
    /// <param name="layerId">The layer identifier.</param>
    /// <param name="success">Whether the operation was successful.</param>
    [LoggerMessage(Level = LogLevel.Information, Message = "ApplyEdits completed for service '{ServiceId}' layer {LayerId} with success: {Success}")]
    public static partial void ApplyEditsCompleted(ILogger logger, string serviceId, int layerId, bool success);

    /// <summary>
    /// Logs when an ApplyEdits operation fails.
    /// </summary>
    /// <param name="logger">The logger instance.</param>
    /// <param name="serviceId">The service identifier.</param>
    /// <param name="layerId">The layer identifier.</param>
    /// <param name="errorMessage">The error message.</param>
    /// <param name="exception">The exception that caused the failure.</param>
    [LoggerMessage(Level = LogLevel.Error, Message = "ApplyEdits failed for service '{ServiceId}' layer {LayerId}: {ErrorMessage}")]
    public static partial void ApplyEditsFailed(ILogger logger, string serviceId, int layerId, string errorMessage, Exception exception);

    /// <summary>
    /// Logs when adding a feature fails.
    /// </summary>
    /// <param name="logger">The logger instance.</param>
    /// <param name="index">The index of the feature that failed to add.</param>
    /// <param name="errorMessage">The error message.</param>
    /// <param name="exception">The exception that caused the failure.</param>
    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to add feature {Index}: {ErrorMessage}")]
    public static partial void FeatureAddFailed(ILogger logger, int index, string errorMessage, Exception exception);

    /// <summary>
    /// Logs when updating a feature fails.
    /// </summary>
    /// <param name="logger">The logger instance.</param>
    /// <param name="index">The index of the feature that failed to update.</param>
    /// <param name="errorMessage">The error message.</param>
    /// <param name="exception">The exception that caused the failure.</param>
    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to update feature {Index}: {ErrorMessage}")]
    public static partial void FeatureUpdateFailed(ILogger logger, int index, string errorMessage, Exception exception);

    /// <summary>
    /// Logs when deleting a feature fails.
    /// </summary>
    /// <param name="logger">The logger instance.</param>
    /// <param name="index">The index of the feature that failed to delete.</param>
    /// <param name="errorMessage">The error message.</param>
    /// <param name="exception">The exception that caused the failure.</param>
    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to delete feature {Index}: {ErrorMessage}")]
    public static partial void FeatureDeleteFailed(ILogger logger, int index, string errorMessage, Exception exception);

    // Related records query events (2400-2499)

    /// <summary>
    /// Logs when a related records query is requested.
    /// </summary>
    /// <param name="logger">The logger instance.</param>
    /// <param name="serviceId">The service identifier.</param>
    /// <param name="layerId">The layer identifier.</param>
    /// <param name="objectIdCount">The number of object IDs requested.</param>
    /// <param name="objectIdSample">A bounded sample of object IDs for diagnostics.</param>
    /// <param name="relationshipId">The relationship identifier.</param>
    [LoggerMessage(
        EventId = 2401,
        Level = LogLevel.Information,
        Message = "Related records query requested: {ServiceId}/FeatureServer/{LayerId}/queryRelatedRecords with objectIdCount: {ObjectIdCount}, objectIdSample: {ObjectIdSample}, relationshipId: {RelationshipId}")]
    public static partial void RelatedRecordsQueryRequested(
        ILogger logger,
        string serviceId,
        int layerId,
        int objectIdCount,
        string? objectIdSample,
        int? relationshipId);

    /// <summary>
    /// Logs when a related records query is successfully completed.
    /// </summary>
    /// <param name="logger">The logger instance.</param>
    /// <param name="serviceId">The service identifier.</param>
    /// <param name="layerId">The layer identifier.</param>
    /// <param name="relatedFeatureCount">The number of related features returned.</param>
    /// <param name="groupCount">The number of groups in the result.</param>
    [LoggerMessage(
        EventId = 2402,
        Level = LogLevel.Information,
        Message = "Related records query completed: {ServiceId}/FeatureServer/{LayerId} returned {RelatedFeatureCount} related features in {GroupCount} groups")]
    public static partial void RelatedRecordsQueryCompleted(ILogger logger, string serviceId, int layerId, int relatedFeatureCount, int groupCount);

    /// <summary>
    /// Logs when a related records query fails.
    /// </summary>
    /// <param name="logger">The logger instance.</param>
    /// <param name="serviceId">The service identifier.</param>
    /// <param name="layerId">The layer identifier.</param>
    /// <param name="errorMessage">The error message.</param>
    /// <param name="exception">The optional exception that caused the failure.</param>
    [LoggerMessage(
        EventId = 2403,
        Level = LogLevel.Error,
        Message = "Related records query failed for {ServiceId}/FeatureServer/{LayerId}: {ErrorMessage}")]
    public static partial void RelatedRecordsQueryFailed(ILogger logger, string serviceId, int layerId, string errorMessage, Exception? exception = null);

    /// <summary>
    /// Logs when a relationship is not found.
    /// </summary>
    /// <param name="logger">The logger instance.</param>
    /// <param name="layerId">The layer identifier.</param>
    /// <param name="relationshipId">The relationship identifier that was not found.</param>
    [LoggerMessage(
        EventId = 2404,
        Level = LogLevel.Warning,
        Message = "Relationship not found: LayerId {LayerId}, RelationshipId {RelationshipId}")]
    public static partial void RelationshipNotFound(ILogger logger, int layerId, int relationshipId);

    /// <summary>
    /// Logs when datumTransformation parameter is specified. PostGIS default pipeline is used.
    /// </summary>
    /// <param name="logger">The logger instance.</param>
    /// <param name="datumTransformation">The requested datum transformation value.</param>
    [LoggerMessage(
        EventId = 2405,
        Level = LogLevel.Information,
        Message = "datumTransformation parameter specified ({DatumTransformation}); using default PostGIS datum pipeline")]
    public static partial void DatumTransformationRequested(ILogger logger, string datumTransformation);
}
