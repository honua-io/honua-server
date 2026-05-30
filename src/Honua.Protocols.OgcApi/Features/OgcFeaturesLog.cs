// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Features.Protocols.Ogc.Api.Features;

/// <summary>
/// Structured logging for OGC API Features operations.
/// Event ID ranges:
/// - 5200-5299: Basic operations (existing)
/// - 5300-5349: Collections metadata
/// - 5350-5399: Core/conformance/landing
/// - 5400-5449: Query performance and validation
/// - 5450-5499: Parameter validation
/// </summary>
internal static partial class OgcFeaturesLog
{
    // 5200-5299: Basic Operations
    [LoggerMessage(
        EventId = 5200,
        Level = LogLevel.Information,
        Message = "OGC items requested for collection {CollectionId} (limit={Limit}, offset={Offset})")]
    public static partial void ItemsRequested(ILogger logger, string collectionId, int? limit, int? offset);

    [LoggerMessage(
        EventId = 5201,
        Level = LogLevel.Information,
        Message = "OGC item requested for collection {CollectionId} with feature {FeatureId}")]
    public static partial void ItemRequested(ILogger logger, string collectionId, string featureId);

    [LoggerMessage(
        EventId = 5202,
        Level = LogLevel.Information,
        Message = "OGC item created for collection {CollectionId} with feature {FeatureId}")]
    public static partial void ItemCreated(ILogger logger, string collectionId, string featureId);

    [LoggerMessage(
        EventId = 5203,
        Level = LogLevel.Information,
        Message = "OGC item updated for collection {CollectionId} with feature {FeatureId}")]
    public static partial void ItemUpdated(ILogger logger, string collectionId, string featureId);

    [LoggerMessage(
        EventId = 5204,
        Level = LogLevel.Information,
        Message = "OGC item deleted for collection {CollectionId} with feature {FeatureId}")]
    public static partial void ItemDeleted(ILogger logger, string collectionId, string featureId);

    // 5300-5349: Collections Metadata
    [LoggerMessage(
        EventId = 5300,
        Level = LogLevel.Information,
        Message = "OGC collections requested")]
    public static partial void CollectionsRequested(ILogger logger);

    [LoggerMessage(
        EventId = 5301,
        Level = LogLevel.Information,
        Message = "OGC collections returned (count={CollectionCount})")]
    public static partial void CollectionsReturned(ILogger logger, int collectionCount);

    [LoggerMessage(
        EventId = 5302,
        Level = LogLevel.Information,
        Message = "OGC collection requested: {CollectionId}")]
    public static partial void CollectionRequested(ILogger logger, string collectionId);

    [LoggerMessage(
        EventId = 5303,
        Level = LogLevel.Information,
        Message = "OGC collection returned: {CollectionId} ({Name})")]
    public static partial void CollectionReturned(ILogger logger, string collectionId, string name);

    [LoggerMessage(
        EventId = 5304,
        Level = LogLevel.Warning,
        Message = "OGC collection not found: {CollectionId}")]
    public static partial void CollectionNotFound(ILogger logger, string collectionId);

    [LoggerMessage(
        EventId = 5305,
        Level = LogLevel.Error,
        Message = "OGC collections query failed: {Error}")]
    public static partial void CollectionsQueryFailed(ILogger logger, string error, Exception? exception);

    // 5350-5399: Core/Conformance/Landing
    [LoggerMessage(
        EventId = 5350,
        Level = LogLevel.Debug,
        Message = "OGC landing page requested")]
    public static partial void LandingPageRequested(ILogger logger);

    [LoggerMessage(
        EventId = 5351,
        Level = LogLevel.Debug,
        Message = "OGC landing page returned")]
    public static partial void LandingPageReturned(ILogger logger);

    [LoggerMessage(
        EventId = 5352,
        Level = LogLevel.Debug,
        Message = "OGC conformance requested")]
    public static partial void ConformanceRequested(ILogger logger);

    [LoggerMessage(
        EventId = 5353,
        Level = LogLevel.Information,
        Message = "OGC conformance returned (classes={ClassCount})")]
    public static partial void ConformanceReturned(ILogger logger, int classCount);

    // 5400-5449: Query Performance and Validation
    [LoggerMessage(
        EventId = 5400,
        Level = LogLevel.Debug,
        Message = "OGC items query started for collection {CollectionId} (limit={Limit}, offset={Offset})")]
    public static partial void ItemsQueryStarted(ILogger logger, string collectionId, int? limit, int? offset);

    [LoggerMessage(
        EventId = 5401,
        Level = LogLevel.Information,
        Message = "OGC items query completed for collection {CollectionId} (returned={Count}, total={TotalCount}, elapsed={ElapsedMs}ms)")]
    public static partial void ItemsQueryCompleted(ILogger logger, string collectionId, int count, long? totalCount, double elapsedMs);

    [LoggerMessage(
        EventId = 5402,
        Level = LogLevel.Debug,
        Message = "OGC item query started for collection {CollectionId}, feature {FeatureId}")]
    public static partial void ItemQueryStarted(ILogger logger, string collectionId, string featureId);

    [LoggerMessage(
        EventId = 5403,
        Level = LogLevel.Information,
        Message = "OGC item query completed for collection {CollectionId}, feature {FeatureId} (elapsed={ElapsedMs}ms)")]
    public static partial void ItemQueryCompleted(ILogger logger, string collectionId, string featureId, double elapsedMs);

    [LoggerMessage(
        EventId = 5410,
        Level = LogLevel.Debug,
        Message = "OGC item creation started for collection {CollectionId} (geometryType={GeometryType})")]
    public static partial void CreateItemStarted(ILogger logger, string collectionId, string? geometryType);

    [LoggerMessage(
        EventId = 5411,
        Level = LogLevel.Information,
        Message = "OGC item creation completed for collection {CollectionId}, feature {FeatureId} (elapsed={ElapsedMs}ms)")]
    public static partial void CreateItemCompleted(ILogger logger, string collectionId, string featureId, double elapsedMs);

    [LoggerMessage(
        EventId = 5420,
        Level = LogLevel.Debug,
        Message = "OGC item update started for collection {CollectionId}, feature {FeatureId}")]
    public static partial void UpdateItemStarted(ILogger logger, string collectionId, string featureId);

    [LoggerMessage(
        EventId = 5421,
        Level = LogLevel.Information,
        Message = "OGC item update completed for collection {CollectionId}, feature {FeatureId} (elapsed={ElapsedMs}ms)")]
    public static partial void UpdateItemCompleted(ILogger logger, string collectionId, string featureId, double elapsedMs);

    [LoggerMessage(
        EventId = 5430,
        Level = LogLevel.Debug,
        Message = "OGC item deletion started for collection {CollectionId}, feature {FeatureId}")]
    public static partial void DeleteItemStarted(ILogger logger, string collectionId, string featureId);

    [LoggerMessage(
        EventId = 5431,
        Level = LogLevel.Information,
        Message = "OGC item deletion completed for collection {CollectionId}, feature {FeatureId} (elapsed={ElapsedMs}ms)")]
    public static partial void DeleteItemCompleted(ILogger logger, string collectionId, string featureId, double elapsedMs);

    // 5450-5499: Parameter Validation
    [LoggerMessage(
        EventId = 5450,
        Level = LogLevel.Debug,
        Message = "OGC filter parameter validation for collection {CollectionId} (language={FilterLang}, valid={FilterValid})")]
    public static partial void FilterParameterValidation(ILogger logger, string collectionId, string? filterLang, bool filterValid);

    [LoggerMessage(
        EventId = 5451,
        Level = LogLevel.Debug,
        Message = "OGC CRS parameter validation for collection {CollectionId} (crs={Crs}, valid={CrsValid})")]
    public static partial void CrsParameterValidation(ILogger logger, string collectionId, string? crs, bool crsValid);

    [LoggerMessage(
        EventId = 5452,
        Level = LogLevel.Debug,
        Message = "OGC bbox parameter validation for collection {CollectionId} (provided={BboxProvided})")]
    public static partial void BboxParameterValidation(ILogger logger, string collectionId, bool bboxProvided);

    [LoggerMessage(
        EventId = 5453,
        Level = LogLevel.Debug,
        Message = "OGC datetime parameter validation for collection {CollectionId} (provided={DateProvided})")]
    public static partial void DatetimeParameterValidation(ILogger logger, string collectionId, bool dateProvided);

    [LoggerMessage(
        EventId = 5454,
        Level = LogLevel.Debug,
        Message = "OGC pagination parameter validation (limit={Limit}, offset={Offset}, limitExceeded={LimitExceeded})")]
    public static partial void PaginationParameterValidation(ILogger logger, int? limit, int? offset, bool limitExceeded);

    [LoggerMessage(
        EventId = 5455,
        Level = LogLevel.Debug,
        Message = "OGC query parameters resolved for collection {CollectionId} (effectiveLimit={EffectiveLimit}, effectiveOffset={EffectiveOffset})")]
    public static partial void QueryParametersResolved(ILogger logger, string collectionId, int effectiveLimit, int effectiveOffset);

    // Error logging (preserving existing patterns)
    [LoggerMessage(
        EventId = 5210,
        Level = LogLevel.Error,
        Message = "OGC items query failed for collection {CollectionId}")]
    public static partial void ItemsQueryFailed(ILogger logger, string collectionId, Exception exception);

    [LoggerMessage(
        EventId = 5211,
        Level = LogLevel.Error,
        Message = "OGC item query failed for collection {CollectionId}")]
    public static partial void ItemQueryFailed(ILogger logger, string collectionId, Exception exception);

    [LoggerMessage(
        EventId = 5212,
        Level = LogLevel.Error,
        Message = "OGC feature creation failed for collection {CollectionId}: {Reason}")]
    public static partial void CreateFeatureFailed(ILogger logger, string collectionId, string? reason, Exception exception);

    [LoggerMessage(
        EventId = 5213,
        Level = LogLevel.Error,
        Message = "OGC feature update failed for collection {CollectionId}, feature {FeatureId}: {Reason}")]
    public static partial void UpdateFeatureFailed(ILogger logger, string collectionId, string? featureId, string? reason, Exception exception);

    [LoggerMessage(
        EventId = 5214,
        Level = LogLevel.Error,
        Message = "OGC feature deletion failed for collection {CollectionId}, feature {FeatureId}: {Reason}")]
    public static partial void DeleteFeatureFailed(ILogger logger, string collectionId, string? featureId, string? reason, Exception exception);
}
