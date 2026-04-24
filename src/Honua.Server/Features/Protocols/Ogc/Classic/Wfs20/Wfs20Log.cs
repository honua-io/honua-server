// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Features.Protocols.Ogc.Classic.Wfs20;

/// <summary>
/// Structured logging for WFS 2.0 operations
/// </summary>
internal static partial class Wfs20Log
{
    /// <summary>
    /// Logs when a GetCapabilities request is received
    /// </summary>
    [LoggerMessage(Level = LogLevel.Information, Message = "WFS 2.0 GetCapabilities request received")]
    public static partial void GetCapabilitiesRequested(ILogger logger);

    /// <summary>
    /// Logs when GetCapabilities response is returned
    /// </summary>
    [LoggerMessage(Level = LogLevel.Information, Message = "WFS 2.0 GetCapabilities response returned")]
    public static partial void GetCapabilitiesReturned(ILogger logger);

    /// <summary>
    /// Logs when a DescribeFeatureType request is received
    /// </summary>
    [LoggerMessage(Level = LogLevel.Information, Message = "WFS 2.0 DescribeFeatureType request received for types: {TypeNames}")]
    public static partial void DescribeFeatureTypeRequested(ILogger logger, string typeNames);

    /// <summary>
    /// Logs when DescribeFeatureType response is returned
    /// </summary>
    [LoggerMessage(Level = LogLevel.Information, Message = "WFS 2.0 DescribeFeatureType response returned with {SchemaCount} schemas")]
    public static partial void DescribeFeatureTypeReturned(ILogger logger, int schemaCount);

    /// <summary>
    /// Logs when a GetFeature request is received
    /// </summary>
    [LoggerMessage(Level = LogLevel.Information, Message = "WFS 2.0 GetFeature request received for types: {TypeNames}, format: {OutputFormat}")]
    public static partial void GetFeatureRequested(ILogger logger, string typeNames, string outputFormat);

    /// <summary>
    /// Logs when GetFeature response is returned
    /// </summary>
    [LoggerMessage(Level = LogLevel.Information, Message = "WFS 2.0 GetFeature response returned with {FeatureCount} features, matched: {NumberMatched}")]
    public static partial void GetFeatureReturned(ILogger logger, int featureCount, string numberMatched);

    /// <summary>
    /// Logs when a GetPropertyValue request is received
    /// </summary>
    [LoggerMessage(Level = LogLevel.Information, Message = "WFS 2.0 GetPropertyValue request received for property: {PropertyName}, type: {TypeName}")]
    public static partial void GetPropertyValueRequested(ILogger logger, string propertyName, string typeName);

    /// <summary>
    /// Logs when GetPropertyValue response is returned
    /// </summary>
    [LoggerMessage(Level = LogLevel.Information, Message = "WFS 2.0 GetPropertyValue response returned with {ValueCount} values")]
    public static partial void GetPropertyValueReturned(ILogger logger, int valueCount);

    /// <summary>
    /// Logs when a Transaction request is received
    /// </summary>
    [LoggerMessage(Level = LogLevel.Information, Message = "WFS 2.0 Transaction request received with {InsertCount} inserts, {UpdateCount} updates, {DeleteCount} deletes")]
    public static partial void TransactionRequested(ILogger logger, int insertCount, int updateCount, int deleteCount);

    /// <summary>
    /// Logs when Transaction response is returned
    /// </summary>
    [LoggerMessage(Level = LogLevel.Information, Message = "WFS 2.0 Transaction response returned - inserted: {Inserted}, updated: {Updated}, deleted: {Deleted}")]
    public static partial void TransactionReturned(ILogger logger, int inserted, int updated, int deleted);

    /// <summary>
    /// Logs parameter validation errors
    /// </summary>
    [LoggerMessage(Level = LogLevel.Warning, Message = "WFS 2.0 parameter validation failed: {Error}")]
    public static partial void ParameterValidationFailed(ILogger logger, string error);

    /// <summary>
    /// Logs filter parsing errors
    /// </summary>
    [LoggerMessage(Level = LogLevel.Warning, Message = "WFS 2.0 filter parsing failed for operation {Operation}: {Error}")]
    public static partial void FilterParsingFailed(ILogger logger, string operation, string error);

    /// <summary>
    /// Logs feature type not found errors
    /// </summary>
    [LoggerMessage(Level = LogLevel.Warning, Message = "WFS 2.0 feature type not found: {TypeName}")]
    public static partial void FeatureTypeNotFound(ILogger logger, string typeName);

    /// <summary>
    /// Logs unsupported operation attempts
    /// </summary>
    [LoggerMessage(Level = LogLevel.Warning, Message = "WFS 2.0 unsupported operation requested: {Operation}")]
    public static partial void UnsupportedOperationRequested(ILogger logger, string operation);

    /// <summary>
    /// Logs unsupported output format attempts
    /// </summary>
    [LoggerMessage(Level = LogLevel.Warning, Message = "WFS 2.0 unsupported output format requested: {Format}")]
    public static partial void UnsupportedOutputFormatRequested(ILogger logger, string format);

    /// <summary>
    /// Logs CRS transformation errors
    /// </summary>
    [LoggerMessage(Level = LogLevel.Error, Message = "WFS 2.0 CRS transformation failed from {SourceCrs} to {TargetCrs}: {Error}")]
    public static partial void CrsTransformationFailed(ILogger logger, string sourceCrs, string targetCrs, string error);

    /// <summary>
    /// Logs feature serialization errors
    /// </summary>
    [LoggerMessage(Level = LogLevel.Error, Message = "WFS 2.0 feature serialization failed for format {Format}: {Error}")]
    public static partial void FeatureSerializationFailed(ILogger logger, string format, string error);

    /// <summary>
    /// Logs database query errors
    /// </summary>
    [LoggerMessage(Level = LogLevel.Error, Message = "WFS 2.0 database query failed for operation {Operation}: {Error}")]
    public static partial void DatabaseQueryFailed(ILogger logger, string operation, string error);

    /// <summary>
    /// Logs transaction rollback events
    /// </summary>
    [LoggerMessage(Level = LogLevel.Warning, Message = "WFS 2.0 transaction rolled back due to error: {Error}")]
    public static partial void TransactionRolledBack(ILogger logger, string error);

    /// <summary>
    /// Logs spatial query performance metrics
    /// </summary>
    [LoggerMessage(Level = LogLevel.Debug, Message = "WFS 2.0 spatial query completed in {ElapsedMs}ms for {FeatureCount} features")]
    public static partial void SpatialQueryCompleted(ILogger logger, long elapsedMs, int featureCount);

    /// <summary>
    /// Logs feature type schema validation
    /// </summary>
    [LoggerMessage(Level = LogLevel.Debug, Message = "WFS 2.0 validating feature type schema for: {TypeName}")]
    public static partial void ValidatingFeatureTypeSchema(ILogger logger, string typeName);

    /// <summary>
    /// Logs feature type schema validation success
    /// </summary>
    [LoggerMessage(Level = LogLevel.Debug, Message = "WFS 2.0 feature type schema validation succeeded for: {TypeName}")]
    public static partial void FeatureTypeSchemaValidated(ILogger logger, string typeName);

    /// <summary>
    /// Logs feature type schema validation failure
    /// </summary>
    [LoggerMessage(Level = LogLevel.Warning, Message = "WFS 2.0 feature type schema validation failed for {TypeName}: {Error}")]
    public static partial void FeatureTypeSchemaValidationFailed(ILogger logger, string typeName, string error);
}
