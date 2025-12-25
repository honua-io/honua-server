// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Features.FeatureServer;

/// <summary>
/// Structured logging for FeatureServer endpoints with source generation (AOT compatible)
/// </summary>
internal static partial class FeatureServerLog
{
    // Service metadata events (2000-2099)

    [LoggerMessage(
        EventId = 2001,
        Level = LogLevel.Information,
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
        Level = LogLevel.Information,
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
        Level = LogLevel.Information,
        Message = "Query requested: {ServiceId}/FeatureServer/{LayerId}/query with WHERE: {WhereClause}")]
    public static partial void QueryRequested(ILogger logger, string serviceId, int layerId, string? whereClause);

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

    // ApplyEdits events (2300-2399)

    [LoggerMessage(Level = LogLevel.Information, Message = "ApplyEdits requested for service '{ServiceId}' layer {LayerId} with {AddCount} adds, {UpdateCount} updates, {DeleteCount} deletes")]
    public static partial void ApplyEditsRequested(ILogger logger, string serviceId, int layerId, int addCount, int updateCount, int deleteCount);

    [LoggerMessage(Level = LogLevel.Information, Message = "ApplyEdits completed for service '{ServiceId}' layer {LayerId} with success: {Success}")]
    public static partial void ApplyEditsCompleted(ILogger logger, string serviceId, int layerId, bool success);

    [LoggerMessage(Level = LogLevel.Error, Message = "ApplyEdits failed for service '{ServiceId}' layer {LayerId}: {ErrorMessage}")]
    public static partial void ApplyEditsFailed(ILogger logger, string serviceId, int layerId, string errorMessage, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to add feature {Index}: {ErrorMessage}")]
    public static partial void FeatureAddFailed(ILogger logger, int index, string errorMessage, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to update feature {Index}: {ErrorMessage}")]
    public static partial void FeatureUpdateFailed(ILogger logger, int index, string errorMessage, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to delete feature {Index}: {ErrorMessage}")]
    public static partial void FeatureDeleteFailed(ILogger logger, int index, string errorMessage, Exception exception);

    // Related records query events (2400-2499)

    [LoggerMessage(
        EventId = 2401,
        Level = LogLevel.Information,
        Message = "Related records query requested: {ServiceId}/FeatureServer/{LayerId}/queryRelatedRecords with objectIds: {ObjectIds}, relationshipId: {RelationshipId}")]
    public static partial void RelatedRecordsQueryRequested(ILogger logger, string serviceId, int layerId, string? objectIds, int? relationshipId);

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
}
