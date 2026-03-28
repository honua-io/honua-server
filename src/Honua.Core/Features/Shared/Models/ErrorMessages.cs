// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

#pragma warning disable CS1591 // Missing XML comment for publicly visible type or member
#pragma warning disable CS1587 // XML comment is not placed on a valid language element
using System.Globalization;
using System.Text;

namespace Honua.Core.Features.Shared.Models;

/// <summary>
/// Centralized error message constants to eliminate duplication and ensure consistent messaging
/// across all protocols (GeoServices REST, OGC API Features, OData, MVT).
/// </summary>
public static class ErrorMessages
{
    /// <summary>
    /// Resource not found error messages
    /// </summary>
    public static class NotFound
    {
        public const string         /// <inheritdoc/>
Service = "Service '{0}' not found.";
        public const string         /// <inheritdoc/>
ServiceGeneric = "Service not found.";
        public const string         /// <inheritdoc/>
Layer = "Layer {0} not found.";
        public const string         /// <inheritdoc/>
LayerGeneric = "Layer not found.";
        public const string         /// <inheritdoc/>
LayerInService = "Layer {0} not found in service '{1}'.";
        public const string         /// <inheritdoc/>
Collection = "Collection '{0}' not found.";
        public const string         /// <inheritdoc/>
CollectionGeneric = "Collection not found.";
        public const string         /// <inheritdoc/>
Field = "Field '{0}' not found in layer '{1}'.";
        public const string         /// <inheritdoc/>
Resource = "The requested resource was not found.";
        public const string         /// <inheritdoc/>
ResourceGeneric = "{0} not found";
        private static readonly CompositeFormat _serviceFormat = CompositeFormat.Parse(Service);
        private static readonly CompositeFormat _layerFormat = CompositeFormat.Parse(Layer);
        private static readonly CompositeFormat _layerInServiceFormat = CompositeFormat.Parse(LayerInService);
        private static readonly CompositeFormat _collectionFormat = CompositeFormat.Parse(Collection);
        private static readonly CompositeFormat _fieldFormat = CompositeFormat.Parse(Field);
        private static readonly CompositeFormat _resourceGenericFormat = CompositeFormat.Parse(ResourceGeneric);

        /// <summary>
        /// Formats service not found message
        /// </summary>
        public static string FormatService(string serviceId) =>
            string.Format(CultureInfo.InvariantCulture, _serviceFormat, serviceId);

        /// <summary>
        /// Formats layer not found message
        /// </summary>
        public static string FormatLayer(int layerId) =>
            string.Format(CultureInfo.InvariantCulture, _layerFormat, layerId);

        /// <summary>
        /// Formats layer not found in service message
        /// </summary>
        public static string FormatLayerInService(int layerId, string serviceId) =>
            string.Format(CultureInfo.InvariantCulture, _layerInServiceFormat, layerId, serviceId);

        /// <summary>
        /// Formats collection not found message
        /// </summary>
        public static string FormatCollection(string collectionId) =>
            string.Format(CultureInfo.InvariantCulture, _collectionFormat, collectionId);

        /// <summary>
        /// Formats field not found message
        /// </summary>
        public static string FormatField(string fieldName, string layerName) =>
            string.Format(CultureInfo.InvariantCulture, _fieldFormat, fieldName, layerName);

        /// <summary>
        /// Formats generic resource not found message
        /// </summary>
        public static string FormatResource(string resource) =>
            string.Format(CultureInfo.InvariantCulture, _resourceGenericFormat, resource);
    }

    /// <summary>
    /// Validation error messages
    /// </summary>
    public static class Validation
    {
        public const string         /// <inheritdoc/>
InvalidParameter = "Invalid query parameters";
        public const string         /// <inheritdoc/>
InvalidQueryParameter = "Invalid query parameter.";
        public const string         /// <inheritdoc/>
InvalidRequestParameters = "Invalid request parameters.";
        public const string         /// <inheritdoc/>
InvalidPaginationParameters = "Invalid pagination parameters.";
        public const string         /// <inheritdoc/>
InvalidPagingParameters = "Invalid paging parameters.";
        public const string         /// <inheritdoc/>
InvalidGeometryParameter = "Invalid geometry parameter";
        public const string         /// <inheritdoc/>
InvalidJsonFormat = "Invalid JSON format in geometry parameter";
        public const string         /// <inheritdoc/>
InvalidBboxFormat = "Invalid bbox parameter format.";
        public const string         /// <inheritdoc/>
InvalidDatetimeFormat = "Invalid datetime parameter format.";
        public const string         /// <inheritdoc/>
InvalidDatetimeParameter = "Invalid datetime parameter.";
        public const string         /// <inheritdoc/>
InvalidCollectionsParameter = "Invalid collections parameter.";
        public const string         /// <inheritdoc/>
InvalidQueryParameterValue = "Invalid query parameter value.";
        public const string         /// <inheritdoc/>
RequiredParameterMissing = "A required parameter was not provided.";
        public const string         /// <inheritdoc/>
CollectionIdRequired = "Collection ID is required.";
        public const string         /// <inheritdoc/>
ServiceIdRequired = "Service ID is required.";
        public const string         /// <inheritdoc/>
CollectionIdInvalid = "Collection '{0}' is invalid.";
        private static readonly CompositeFormat _collectionIdInvalidFormat = CompositeFormat.Parse(CollectionIdInvalid);

        /// <summary>
        /// Formats range validation message
        /// </summary>
        public static string FormatRange(string field, object min, object max) =>
            $"{field} must be between {min} and {max}";

        /// <summary>
        /// Formats range validation message with units
        /// </summary>
        public static string FormatRangeWithUnits(string field, object min, object max, string units) =>
            $"{field} must be between {min} and {max} {units}";

        /// <summary>
        /// Formats minimum value validation message
        /// </summary>
        public static string FormatMinimum(string field, object min) =>
            $"{field} must be at least {min}";

        /// <summary>
        /// Formats collection ID invalid message
        /// </summary>
        public static string FormatCollectionIdInvalid(string collectionId) =>
            string.Format(CultureInfo.InvariantCulture, _collectionIdInvalidFormat, collectionId);
    }

    /// <summary>
    /// Range validation error messages for common ranges
    /// </summary>
    public static class RangeValidation
    {
        public const string         /// <inheritdoc/>
MaxRecordCount = "MaxRecordCount must be between 100 and 10,000";
        public const string         /// <inheritdoc/>
DefaultRecordCount = "DefaultRecordCount must be at least 100";
        public const string         /// <inheritdoc/>
MaxOffset = "MaxOffset must be between 1,000 and 1,000,000";
        public const string         /// <inheritdoc/>
QueryTimeout = "Query.QueryTimeout must be between 5 seconds and 2 minutes";
        public const string         /// <inheritdoc/>
MaxVerticesPerGeometry = "MaxVerticesPerGeometry must be between 1,000 and 1,000,000";
        public const string         /// <inheritdoc/>
MaxGeometrySize = "MaxGeometrySize must be between 1MB and 100MB";
        public const string         /// <inheritdoc/>
MaxCoordinatePrecision = "MaxCoordinatePrecision must be between 1 and 15";
        public const string         /// <inheritdoc/>
SimplifyTolerance = "SimplifyTolerance must be between 0 and 1000 meters";
        public const string         /// <inheritdoc/>
MaxFeaturesPerEdit = "MaxFeaturesPerEdit must be between 1 and 10,000";
        public const string         /// <inheritdoc/>
MaxEditsPerTransaction = "MaxEditsPerTransaction must be between 100 and 50,000";
        public const string         /// <inheritdoc/>
MaxPayloadSize = "MaxPayloadSize must be between 1MB and 500MB";
        public const string         /// <inheritdoc/>
MaxAttachmentSize = "MaxAttachmentSize must be between 1MB and 100MB";
        public const string         /// <inheritdoc/>
MaxAttachmentsPerFeature = "MaxAttachmentsPerFeature must be between 1 and 100";
        public const string         /// <inheritdoc/>
MaxTotalAttachmentSize = "MaxTotalAttachmentSize must be between 10MB and 1GB";
        public const string         /// <inheritdoc/>
MaxTileZoom = "MaxTileZoom must be between 1 and 24";
        public const string         /// <inheritdoc/>
MinTileZoom = "MinTileZoom must be between 0 and 10";
        public const string         /// <inheritdoc/>
MaxFeaturesPerTile = "MaxFeaturesPerTile must be between 1,000 and 1,000,000";
        public const string         /// <inheritdoc/>
TileTimeout = "Tiles.TileTimeout must be between 1 second and 1 minute";
        public const string         /// <inheritdoc/>
MaxTileSize = "MaxTileSize must be between 100KB and 5MB";
        public const string         /// <inheritdoc/>
MaxConcurrentQueries = "MaxConcurrentQueries must be between 10 and 1,000";
        public const string         /// <inheritdoc/>
MaxConnectionPoolSize = "MaxConnectionPoolSize must be between 10 and 500";
        public const string         /// <inheritdoc/>
RequestTimeout = "Connections.RequestTimeout must be between 10 seconds and 10 minutes";
        public const string         /// <inheritdoc/>
MaxPreviewSize = "MaxPreviewSize must be between 1MB and 50MB";
        public const string         /// <inheritdoc/>
MaxSyncImportSize = "MaxSyncImportSize must be between 10MB and 500MB";
        public const string         /// <inheritdoc/>
MaxImportSize = "MaxImportSize must be between 50MB and 5GB";
        public const string         /// <inheritdoc/>
MaxPreviewFeatures = "MaxPreviewFeatures must be between 10 and 1,000";
        public const string         /// <inheritdoc/>
MaxPreviewCountScan = "MaxPreviewCountScan must be between 10 and 1,000,000";
        public const string         /// <inheritdoc/>
BatchSize = "BatchSize must be between 100 and 10,000";
        public const string         /// <inheritdoc/>
MaxVertices = "MaxVertices must be between 1,000 and 100,000";
        public const string         /// <inheritdoc/>
MaxRings = "MaxRings must be between 10 and 1,000";
        public const string         /// <inheritdoc/>
CoordinatePrecision = "CoordinatePrecision must be between 1 and 15";
        public const string         /// <inheritdoc/>
MaxWkbSize = "MaxWkbSize must be between 100KB and 10MB";
        public const string         /// <inheritdoc/>
MaxAttributeLength = "MaxAttributeLength must be between 1,000 and 1,000,000";

        // Cache options validation messages
        public const string         /// <inheritdoc/>
DefaultTtlSeconds = "DefaultTtlSeconds must be between 1 and 86400 (24 hours)";
        public const string         /// <inheritdoc/>
ServiceTtlSeconds = "ServiceTtlSeconds must be between 1 and 86400 (24 hours)";
        public const string         /// <inheritdoc/>
LayerTtlSeconds = "LayerTtlSeconds must be between 1 and 86400 (24 hours)";
        public const string         /// <inheritdoc/>
QueryTtlSeconds = "QueryTtlSeconds must be between 1 and 3600 (1 hour)";
        public const string         /// <inheritdoc/>
NegativeTtlSeconds = "NegativeTtlSeconds must be between 1 and 3600 (1 hour)";
        public const string         /// <inheritdoc/>
JitterPercentage = "JitterPercentage must be between 0 and 0.5 (50%)";
        public const string         /// <inheritdoc/>
FallbackMaxEntries = "FallbackMaxEntries must be between 10 and 100000";
        public const string         /// <inheritdoc/>
RetryIntervalSeconds = "RetryIntervalSeconds must be between 5 and 300";
        public const string         /// <inheritdoc/>
BackgroundRefreshThreshold = "BackgroundRefreshThreshold must be between 0.05 and 0.75";
        public const string         /// <inheritdoc/>
MaxConcurrentRefreshes = "MaxConcurrentRefreshes must be between 1 and 100";
        public const string         /// <inheritdoc/>
RefreshTimeoutSeconds = "RefreshTimeoutSeconds must be between 5 and 120";

        // Adaptive sampling options validation messages
        public const string         /// <inheritdoc/>
BaseSamplingRate = "BaseSamplingRate must be between 0.001 and 1.0";
        public const string         /// <inheritdoc/>
MinSamplingRate = "MinSamplingRate must be between 0.001 and 0.5";
        public const string         /// <inheritdoc/>
MaxSamplingRate = "MaxSamplingRate must be between 0.1 and 1.0";
        public const string         /// <inheritdoc/>
CpuThreshold = "CpuThreshold must be between 30 and 95";
        public const string         /// <inheritdoc/>
MemoryThreshold = "MemoryThreshold must be between 30 and 95";
        public const string         /// <inheritdoc/>
ActiveRequestThreshold = "ActiveRequestThreshold must be between 10 and 1000";
        public const string         /// <inheritdoc/>
ResponseTimeThresholdMs = "ResponseTimeThresholdMs must be between 100 and 10000";
        public const string         /// <inheritdoc/>
ErrorRateThreshold = "ErrorRateThreshold must be between 0.1 and 50";
        public const string         /// <inheritdoc/>
ErrorMultiplier = "ErrorMultiplier must be between 1.5 and 10";
        public const string         /// <inheritdoc/>
ErrorWindowMinutes = "ErrorWindowMinutes must be between 1 and 30";
        public const string         /// <inheritdoc/>
CriticalRate = "CriticalRate must be between 0.1 and 1.0";
        public const string         /// <inheritdoc/>
ImportantRate = "ImportantRate must be between 0.05 and 1.0";
        public const string         /// <inheritdoc/>
NormalRate = "NormalRate must be between 0.01 and 1.0";
        public const string         /// <inheritdoc/>
BackgroundRate = "BackgroundRate must be between 0.001 and 0.1";

        // File storage validation messages
        public const string         /// <inheritdoc/>
AwsS3BucketName = "AwsS3.BucketName must be between 3 and 63 characters";
        public const string         /// <inheritdoc/>
AzureBlobContainerName = "AzureBlob.ContainerName must be between 3 and 63 characters";

        // Geometry and spatial validation messages
        public const string         /// <inheritdoc/>
SridRange = "SRID must be between 0 and 999,999.";
        public const string         /// <inheritdoc/>
LongitudeRange = "Longitude must be between -180 and 180 degrees.";
        public const string         /// <inheritdoc/>
LatitudeRange = "Latitude must be between -90 and 90 degrees.";

        // Configuration validation messages
        public const string         /// <inheritdoc/>
AdaptiveSamplingLoadCpuThreshold = "AdaptiveSampling:Load:CpuThreshold must be between 30 and 95";
        public const string         /// <inheritdoc/>
AdaptiveSamplingLoadMemoryThreshold = "AdaptiveSampling:Load:MemoryThreshold must be between 30 and 95";
        public const string         /// <inheritdoc/>
AdaptiveSamplingBaseSamplingRate = "AdaptiveSampling:BaseSamplingRate must be between 0.0 and 1.0";
    }

    /// <summary>
    /// Authorization and security error messages
    /// </summary>
    public static class Security
    {
        public const string         /// <inheritdoc/>
Unauthorized = "Authentication is required to access this resource.";
        public const string         /// <inheritdoc/>
Forbidden = "Forbidden to perform {0}";
        public const string         /// <inheritdoc/>
UnauthorizedOperation = "Unauthorized to perform {0}";
        public const string         /// <inheritdoc/>
InvalidToken = "Invalid or expired authentication token.";
        public const string         /// <inheritdoc/>
TokenRequired = "Authentication token is required.";
        private static readonly CompositeFormat _forbiddenFormat = CompositeFormat.Parse(Forbidden);
        private static readonly CompositeFormat _unauthorizedOperationFormat = CompositeFormat.Parse(UnauthorizedOperation);

        /// <summary>
        /// Formats forbidden operation message
        /// </summary>
        public static string FormatForbidden(string operation) =>
            string.Format(CultureInfo.InvariantCulture, _forbiddenFormat, operation);

        /// <summary>
        /// Formats unauthorized operation message
        /// </summary>
        public static string FormatUnauthorized(string operation) =>
            string.Format(CultureInfo.InvariantCulture, _unauthorizedOperationFormat, operation);
    }

    /// <summary>
    /// Service and system error messages
    /// </summary>
    public static class System
    {
        /// <summary>
        /// Service temporarily unavailable error message
        /// </summary>
        public const string ServiceUnavailable = "The service is temporarily unavailable. Please try again later.";

        /// <summary>
        /// Request timeout error message
        /// </summary>
        public const string RequestTimeout = "The request timed out.";

        /// <summary>
        /// Request cancelled error message
        /// </summary>
        public const string RequestCancelled = "The request was cancelled or timed out.";

        /// <summary>
        /// Conflict state error message
        /// </summary>
        public const string ConflictState = "The request could not be completed due to a conflict with the current state.";

        /// <summary>
        /// Invalid operation error message
        /// </summary>
        public const string InvalidOperation = "The requested operation is not valid in the current state.";

        /// <summary>
        /// Not supported error message
        /// </summary>
        public const string NotSupported = "The requested operation is not supported.";

        /// <summary>
        /// Unexpected error message
        /// </summary>
        public const string UnexpectedError = "An unexpected error occurred.";

        /// <summary>
        /// Payload too large error message
        /// </summary>
        public const string PayloadTooLarge = "Request payload is too large.";

        /// <summary>
        /// Too many requests error message
        /// </summary>
        public const string TooManyRequests = "Too many requests. Please try again later.";

        // Protocol-specific operation error messages

        /// <summary>
        /// Invalid object ID value error message
        /// </summary>
        public const string InvalidObjectIdValue = "Invalid objectId value. objectIds parameter must contain only numeric values.";

        /// <summary>
        /// Invalid spatial parameters error message
        /// </summary>
        public const string InvalidSpatialParameters = "Invalid spatial parameters: {0}";

        /// <summary>
        /// Invalid temporal parameters error message
        /// </summary>
        public const string InvalidTemporalParameters = "Invalid temporal parameters: {0}";

        /// <summary>
        /// Invalid time parameter error message
        /// </summary>
        public const string InvalidTimeParameter = "Invalid time parameter: {0}";

        /// <summary>
        /// Invalid time parameter format error message
        /// </summary>
        public const string InvalidTimeParameterFormat = "Invalid time parameter format: {0}";

        /// <summary>
        /// Required parameter missing error message
        /// </summary>
        public const string RequiredParameterMissing = "A required parameter was not provided.";
        private static readonly CompositeFormat _invalidSpatialParametersFormat = CompositeFormat.Parse(InvalidSpatialParameters);
        private static readonly CompositeFormat _invalidTemporalParametersFormat = CompositeFormat.Parse(InvalidTemporalParameters);
        private static readonly CompositeFormat _invalidTimeParameterFormatValue = CompositeFormat.Parse(InvalidTimeParameter);
        private static readonly CompositeFormat _invalidTimeParameterFormatFormat = CompositeFormat.Parse(InvalidTimeParameterFormat);

        /// <summary>
        /// Formats invalid spatial parameters message
        /// </summary>
        public static string FormatInvalidSpatialParameters(string details) =>
            string.Format(CultureInfo.InvariantCulture, _invalidSpatialParametersFormat, details);

        /// <summary>
        /// Formats invalid temporal parameters message
        /// </summary>
        public static string FormatInvalidTemporalParameters(string details) =>
            string.Format(CultureInfo.InvariantCulture, _invalidTemporalParametersFormat, details);

        /// <summary>
        /// Formats invalid time parameter message
        /// </summary>
        public static string FormatInvalidTimeParameter(string details) =>
            string.Format(CultureInfo.InvariantCulture, _invalidTimeParameterFormatValue, details);

        /// <summary>
        /// Formats invalid time parameter format message
        /// </summary>
        public static string FormatInvalidTimeParameterFormat(string timeValue) =>
            string.Format(CultureInfo.InvariantCulture, _invalidTimeParameterFormatFormat, timeValue);
    }

    /// <summary>
    /// OData-specific error messages
    /// </summary>
    public static class OData
    {
        public const string         /// <inheritdoc/>
ApplyParameterRequired = "$apply parameter is required.";
        public const string         /// <inheritdoc/>
SearchParameterRequired = "$search parameter is required.";
    }

    /// <summary>
    /// Import and file processing error messages
    /// </summary>
    public static class Import
    {
        public const string         /// <inheritdoc/>
FileNotFound = "File not found.";
        public const string         /// <inheritdoc/>
InvalidFileFormat = "Invalid file format.";
        public const string         /// <inheritdoc/>
UnsupportedFileType = "Unsupported file type.";
        public const string         /// <inheritdoc/>
FileTooLarge = "File is too large.";
        public const string         /// <inheritdoc/>
ImportFailed = "Import operation failed.";
        public const string         /// <inheritdoc/>
ProcessingFailed = "File processing failed.";
    }
}
