// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Protocols.GeoServices.FeatureServer;

internal static partial class FeatureServerLog
{
    // Service metadata events (2000-2099)

    [LoggerMessage(
        EventId = 2001,
        Level = LogLevel.Debug,
        Message = "Service metadata requested: {ServiceId}")]
    public static partial void ServiceMetadataRequested(ILogger logger, string serviceId);

    [LoggerMessage(
        EventId = 2002,
        Level = LogLevel.Information,
        Message = "Service metadata returned: {ServiceId} with {LayerCount} layers")]
    public static partial void ServiceMetadataReturned(ILogger logger, string serviceId, int layerCount);

    [LoggerMessage(
        EventId = 2003,
        Level = LogLevel.Warning,
        Message = "Service not found: {ServiceId}")]
    public static partial void ServiceNotFound(ILogger logger, string serviceId);

    [LoggerMessage(
        EventId = 2004,
        Level = LogLevel.Error,
        Message = "Service metadata request failed for {ServiceId}: {ErrorMessage}")]
    public static partial void ServiceMetadataFailed(ILogger logger, string serviceId, string errorMessage, Exception? exception = null);

    // Layer metadata events (2100-2199)

    [LoggerMessage(
        EventId = 2101,
        Level = LogLevel.Debug,
        Message = "Layer metadata requested: {ServiceId}/FeatureServer/{LayerId}")]
    public static partial void LayerMetadataRequested(ILogger logger, string serviceId, int layerId);

    [LoggerMessage(
        EventId = 2102,
        Level = LogLevel.Information,
        Message = "Layer metadata returned: {ServiceId}/FeatureServer/{LayerId} ({LayerName})")]
    public static partial void LayerMetadataReturned(ILogger logger, string serviceId, int layerId, string layerName);

    [LoggerMessage(
        EventId = 2103,
        Level = LogLevel.Warning,
        Message = "Layer not found: {ServiceId}/FeatureServer/{LayerId}")]
    public static partial void LayerNotFound(ILogger logger, string serviceId, int layerId);

    [LoggerMessage(
        EventId = 2104,
        Level = LogLevel.Error,
        Message = "Layer metadata request failed for {ServiceId}/FeatureServer/{LayerId}: {ErrorMessage}")]
    public static partial void LayerMetadataFailed(ILogger logger, string serviceId, int layerId, string errorMessage, Exception? exception = null);

    // Query events (2200-2299)

    [LoggerMessage(
        EventId = 2201,
        Level = LogLevel.Debug,
        Message = "Query requested: {ServiceId}/FeatureServer/{LayerId}/query (WHERE clause present: {HasWhereClause})")]
    public static partial void QueryRequested(ILogger logger, string serviceId, int layerId, bool hasWhereClause);

    [LoggerMessage(
        EventId = 2202,
        Level = LogLevel.Information,
        Message = "Query completed: {ServiceId}/FeatureServer/{LayerId} returned {FeatureCount} of {TotalCount} features")]
    public static partial void QueryCompleted(ILogger logger, string serviceId, int layerId, int featureCount, long totalCount);

    [LoggerMessage(
        EventId = 2203,
        Level = LogLevel.Error,
        Message = "Query failed for {ServiceId}/FeatureServer/{LayerId}: {ErrorMessage}")]
    public static partial void QueryFailed(ILogger logger, string serviceId, int layerId, string errorMessage, Exception? exception = null);

    [LoggerMessage(
        EventId = 2204,
        Level = LogLevel.Warning,
        Message = "Query limit exceeded: {Parameter} value {ActualValue} exceeds limit {LimitValue}")]
    public static partial void QueryLimitExceeded(ILogger logger, string parameter, int actualValue, int limitValue);

    [LoggerMessage(
        EventId = 2205,
        Level = LogLevel.Warning,
        Message = "Query parameter invalid: {Parameter} value {ActualValue}")]
    public static partial void QueryParameterInvalid(ILogger logger, string parameter, int actualValue);

    [LoggerMessage(
        EventId = 2206,
        Level = LogLevel.Information,
        Message = "Query {Operation} executed: {ServiceId}/FeatureServer/{LayerId} in {ElapsedMilliseconds} ms")]
    public static partial void QueryExecuted(ILogger logger, string operation, string serviceId, int layerId, double elapsedMilliseconds);

    [LoggerMessage(
        EventId = 2207,
        Level = LogLevel.Debug,
        Message = "GeoParquet conversion failed for field '{FieldName}' (value type: {ValueType}), value treated as null")]
    public static partial void GeoParquetConversionFailed(ILogger logger, string fieldName, string valueType, Exception exception);

    // Unparseable geometry WKB is logged and exported as null rather than failing the GeoParquet row.
    [LoggerMessage(
        EventId = 2208,
        Level = LogLevel.Debug,
        Message = "GeoParquet export: corrupt WKB for feature {FeatureId} ({WkbLength} bytes), geometry treated as null")]
    public static partial void GeoParquetCorruptGeometry(ILogger logger, long featureId, int wkbLength, Exception exception);

    // ApplyEdits events (2300-2399)

    [LoggerMessage(Level = LogLevel.Debug, Message = "ApplyEdits requested for service '{ServiceId}' layer {LayerId} with {AddCount} adds, {UpdateCount} updates, {DeleteCount} deletes")]
    public static partial void ApplyEditsRequested(ILogger logger, string serviceId, int layerId, int addCount, int updateCount, int deleteCount);

    [LoggerMessage(Level = LogLevel.Information, Message = "ApplyEdits completed for service '{ServiceId}' layer {LayerId} with success: {Success}")]
    public static partial void ApplyEditsCompleted(ILogger logger, string serviceId, int layerId, bool success);

    [LoggerMessage(Level = LogLevel.Error, Message = "ApplyEdits failed for service '{ServiceId}' layer {LayerId}: {ErrorMessage}")]
    public static partial void ApplyEditsFailed(ILogger logger, string serviceId, int layerId, string errorMessage, Exception exception);

    [LoggerMessage(EventId = 2406, Level = LogLevel.Information, Message = "ApplyEdits replayed from idempotency store for service '{ServiceId}' layer {LayerId}")]
    public static partial void ApplyEditsReplayed(ILogger logger, string serviceId, int layerId);

    [LoggerMessage(EventId = 2407, Level = LogLevel.Warning, Message = "ApplyEdits idempotency store unavailable for service '{ServiceId}' layer {LayerId}; retry deduplication is degraded")]
    public static partial void ApplyEditsIdempotencyStoreUnavailable(ILogger logger, string serviceId, int layerId, Exception exception);

    [LoggerMessage(EventId = 2408, Level = LogLevel.Warning, Message = "ApplyEdits idempotency conflict for service '{ServiceId}' layer {LayerId}: concurrent request with same key")]
    public static partial void ApplyEditsIdempotencyConflict(ILogger logger, string serviceId, int layerId);

    [LoggerMessage(EventId = 2409, Level = LogLevel.Warning, Message = "Replica upload idempotency store unavailable for service '{ServiceId}' replica '{ReplicaId}'; retry deduplication is degraded")]
    public static partial void ReplicaUploadIdempotencyStoreUnavailable(ILogger logger, string serviceId, string replicaId, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to add feature {Index}: {ErrorMessage}")]
    public static partial void FeatureAddFailed(ILogger logger, int index, string errorMessage, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to update feature {Index}: {ErrorMessage}")]
    public static partial void FeatureUpdateFailed(ILogger logger, int index, string errorMessage, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to delete feature {Index}: {ErrorMessage}")]
    public static partial void FeatureDeleteFailed(ILogger logger, int index, string errorMessage, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to read pre-delete snapshot for layer {LayerId} objectId {ObjectId}; delete events will omit the feature payload")]
    public static partial void DeleteSnapshotReadFailed(ILogger logger, int layerId, long objectId, Exception exception);

    [LoggerMessage(
        EventId = 2310,
        Level = LogLevel.Warning,
        Message = "Attribute rule '{RuleName}' ({RuleType}) on {ServiceId}/FeatureServer/{LayerId} uses an unsupported Arcade expression; routed out of scope (skipped)")]
    public static partial void AttributeRuleExpressionUnsupported(ILogger logger, string serviceId, int layerId, string ruleName, string ruleType);

    // Related records query events (2400-2499)

    [LoggerMessage(
        EventId = 2401,
        Level = LogLevel.Debug,
        Message = "Related records query requested: {ServiceId}/FeatureServer/{LayerId}/queryRelatedRecords with objectIdCount: {ObjectIdCount}, objectIdSample: {ObjectIdSample}, relationshipId: {RelationshipId}")]
    public static partial void RelatedRecordsQueryRequested(
        ILogger logger,
        string serviceId,
        int layerId,
        int objectIdCount,
        string? objectIdSample,
        int? relationshipId);

    [LoggerMessage(
        EventId = 2402,
        Level = LogLevel.Information,
        Message = "Related records query completed: {ServiceId}/FeatureServer/{LayerId} returned {RelatedFeatureCount} related features in {GroupCount} groups")]
    public static partial void RelatedRecordsQueryCompleted(ILogger logger, string serviceId, int layerId, int relatedFeatureCount, int groupCount);

    [LoggerMessage(
        EventId = 2403,
        Level = LogLevel.Error,
        Message = "Related records query failed for {ServiceId}/FeatureServer/{LayerId}: {ErrorMessage}")]
    public static partial void RelatedRecordsQueryFailed(ILogger logger, string serviceId, int layerId, string errorMessage, Exception? exception = null);

    [LoggerMessage(
        EventId = 2404,
        Level = LogLevel.Warning,
        Message = "Relationship not found: LayerId {LayerId}, RelationshipId {RelationshipId}")]
    public static partial void RelationshipNotFound(ILogger logger, int layerId, int relationshipId);

    [LoggerMessage(
        EventId = 2405,
        Level = LogLevel.Information,
        Message = "datumTransformation honored ({DatumTransformation}); applying selected datum pipeline")]
    public static partial void DatumTransformationRequested(ILogger logger, string datumTransformation);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Post-commit side-effects timed out for service {ServiceId} layer {LayerId}; data was committed successfully.")]
    public static partial void PostCommitSideEffectsTimedOut(ILogger logger, string serviceId, int layerId);

    [LoggerMessage(EventId = 2410, Level = LogLevel.Warning, Message = "Calculate post-commit hook failed after the edit transaction committed")]
    public static partial void CalculatePostCommitHookFailed(ILogger logger, Exception exception);
}
