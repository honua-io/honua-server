// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Protocols.Ogc.Classic.Wfs20;

internal static partial class Wfs20Log
{
    [LoggerMessage(Level = LogLevel.Information, Message = "WFS 2.0 GetCapabilities request received")]
    public static partial void GetCapabilitiesRequested(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "WFS 2.0 GetCapabilities response returned")]
    public static partial void GetCapabilitiesReturned(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "WFS 2.0 DescribeFeatureType request received for types: {TypeNames}")]
    public static partial void DescribeFeatureTypeRequested(ILogger logger, string typeNames);

    [LoggerMessage(Level = LogLevel.Information, Message = "WFS 2.0 DescribeFeatureType response returned with {SchemaCount} schemas")]
    public static partial void DescribeFeatureTypeReturned(ILogger logger, int schemaCount);

    [LoggerMessage(Level = LogLevel.Information, Message = "WFS 2.0 GetFeature request received for types: {TypeNames}, format: {OutputFormat}")]
    public static partial void GetFeatureRequested(ILogger logger, string typeNames, string outputFormat);

    [LoggerMessage(Level = LogLevel.Information, Message = "WFS 2.0 GetFeature response returned with {FeatureCount} features, matched: {NumberMatched}")]
    public static partial void GetFeatureReturned(ILogger logger, int featureCount, string numberMatched);

    [LoggerMessage(Level = LogLevel.Information, Message = "WFS 2.0 GetPropertyValue request received for property: {PropertyName}, type: {TypeName}")]
    public static partial void GetPropertyValueRequested(ILogger logger, string propertyName, string typeName);

    [LoggerMessage(Level = LogLevel.Information, Message = "WFS 2.0 GetPropertyValue response returned with {ValueCount} values")]
    public static partial void GetPropertyValueReturned(ILogger logger, int valueCount);

    [LoggerMessage(Level = LogLevel.Information, Message = "WFS 2.0 Transaction request received with {InsertCount} inserts, {UpdateCount} updates, {DeleteCount} deletes")]
    public static partial void TransactionRequested(ILogger logger, int insertCount, int updateCount, int deleteCount);

    [LoggerMessage(Level = LogLevel.Information, Message = "WFS 2.0 Transaction response returned - inserted: {Inserted}, updated: {Updated}, deleted: {Deleted}")]
    public static partial void TransactionReturned(ILogger logger, int inserted, int updated, int deleted);

    [LoggerMessage(Level = LogLevel.Warning, Message = "WFS 2.0 parameter validation failed: {Error}")]
    public static partial void ParameterValidationFailed(ILogger logger, string error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "WFS 2.0 filter parsing failed for operation {Operation}: {Error}")]
    public static partial void FilterParsingFailed(ILogger logger, string operation, string error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "WFS 2.0 feature type not found: {TypeName}")]
    public static partial void FeatureTypeNotFound(ILogger logger, string typeName);

    [LoggerMessage(Level = LogLevel.Warning, Message = "WFS 2.0 unsupported operation requested: {Operation}")]
    public static partial void UnsupportedOperationRequested(ILogger logger, string operation);

    [LoggerMessage(Level = LogLevel.Warning, Message = "WFS 2.0 unsupported output format requested: {Format}")]
    public static partial void UnsupportedOutputFormatRequested(ILogger logger, string format);

    [LoggerMessage(Level = LogLevel.Error, Message = "WFS 2.0 CRS transformation failed from {SourceCrs} to {TargetCrs}: {Error}")]
    public static partial void CrsTransformationFailed(ILogger logger, string sourceCrs, string targetCrs, string error);

    [LoggerMessage(Level = LogLevel.Error, Message = "WFS 2.0 feature serialization failed for format {Format}: {Error}")]
    public static partial void FeatureSerializationFailed(ILogger logger, string format, string error);

    [LoggerMessage(Level = LogLevel.Error, Message = "WFS 2.0 database query failed for operation {Operation}: {Error}")]
    public static partial void DatabaseQueryFailed(ILogger logger, Exception exception, string operation, string error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "WFS 2.0 transaction rolled back due to error: {Error}")]
    public static partial void TransactionRolledBack(ILogger logger, string error);

    [LoggerMessage(Level = LogLevel.Debug, Message = "WFS 2.0 spatial query completed in {ElapsedMs}ms for {FeatureCount} features")]
    public static partial void SpatialQueryCompleted(ILogger logger, long elapsedMs, int featureCount);

    [LoggerMessage(Level = LogLevel.Debug, Message = "WFS 2.0 validating feature type schema for: {TypeName}")]
    public static partial void ValidatingFeatureTypeSchema(ILogger logger, string typeName);

    [LoggerMessage(Level = LogLevel.Debug, Message = "WFS 2.0 feature type schema validation succeeded for: {TypeName}")]
    public static partial void FeatureTypeSchemaValidated(ILogger logger, string typeName);

    [LoggerMessage(Level = LogLevel.Warning, Message = "WFS 2.0 feature type schema validation failed for {TypeName}: {Error}")]
    public static partial void FeatureTypeSchemaValidationFailed(ILogger logger, string typeName, string error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "WFS Transaction post-commit side-effects timed out for request {TraceId}; transaction was committed successfully.")]
    public static partial void PostCommitSideEffectsTimedOut(ILogger logger, string traceId);

    [LoggerMessage(Level = LogLevel.Error, Message = "WFS Transaction layer {LayerId} execution failed; treating as per-operation errors to preserve cache invalidation for earlier committed layers: {Error}")]
    public static partial void MultiLayerTransactionLayerFailed(ILogger logger, Exception exception, int layerId, string error);
}
