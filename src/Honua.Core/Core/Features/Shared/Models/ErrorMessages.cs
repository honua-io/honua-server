// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using System.Text;

namespace Honua.Core.Features.Shared.Models;

public static class ErrorMessages
{
    public static class NotFound
    {
        public const string Service = "Service '{0}' not found.";
        public const string ServiceGeneric = "Service not found.";
        public const string Layer = "Layer {0} not found.";
        public const string LayerGeneric = "Layer not found.";
        public const string LayerInService = "Layer {0} not found in service '{1}'.";
        public const string Collection = "Collection '{0}' not found.";
        public const string CollectionGeneric = "Collection not found.";
        public const string Field = "Field '{0}' not found in layer '{1}'.";
        public const string Resource = "The requested resource was not found.";
        public const string ResourceGeneric = "{0} not found";
        private static readonly CompositeFormat _serviceFormat = CompositeFormat.Parse(Service);
        private static readonly CompositeFormat _layerFormat = CompositeFormat.Parse(Layer);
        private static readonly CompositeFormat _layerInServiceFormat = CompositeFormat.Parse(LayerInService);
        private static readonly CompositeFormat _collectionFormat = CompositeFormat.Parse(Collection);
        private static readonly CompositeFormat _fieldFormat = CompositeFormat.Parse(Field);
        private static readonly CompositeFormat _resourceGenericFormat = CompositeFormat.Parse(ResourceGeneric);

        public static string FormatService(string serviceId) =>
            string.Format(CultureInfo.InvariantCulture, _serviceFormat, serviceId);

        public static string FormatLayer(int layerId) =>
            string.Format(CultureInfo.InvariantCulture, _layerFormat, layerId);

        public static string FormatLayerInService(int layerId, string serviceId) =>
            string.Format(CultureInfo.InvariantCulture, _layerInServiceFormat, layerId, serviceId);

        public static string FormatCollection(string collectionId) =>
            string.Format(CultureInfo.InvariantCulture, _collectionFormat, collectionId);

        public static string FormatField(string fieldName, string layerName) =>
            string.Format(CultureInfo.InvariantCulture, _fieldFormat, fieldName, layerName);

        public static string FormatResource(string resource) =>
            string.Format(CultureInfo.InvariantCulture, _resourceGenericFormat, resource);
    }

    public static class Validation
    {
        public const string InvalidParameter = "Invalid query parameters";
        public const string InvalidQueryParameter = "Invalid query parameter.";
        public const string InvalidRequestParameters = "Invalid request parameters.";
        public const string InvalidPaginationParameters = "Invalid pagination parameters.";
        public const string InvalidPagingParameters = "Invalid paging parameters.";
        public const string InvalidGeometryParameter = "Invalid geometry parameter";
        public const string InvalidJsonFormat = "Invalid JSON format in geometry parameter";
        public const string InvalidBboxFormat = "Invalid bbox parameter format.";
        public const string InvalidDatetimeFormat = "Invalid datetime parameter format.";
        public const string InvalidDatetimeParameter = "Invalid datetime parameter.";
        public const string InvalidCollectionsParameter = "Invalid collections parameter.";
        public const string InvalidQueryParameterValue = "Invalid query parameter value.";
        public const string RequiredParameterMissing = "A required parameter was not provided.";
        public const string CollectionIdRequired = "Collection ID is required.";
        public const string ServiceIdRequired = "Service ID is required.";
        public const string CollectionIdInvalid = "Collection '{0}' is invalid.";
        private static readonly CompositeFormat _collectionIdInvalidFormat = CompositeFormat.Parse(CollectionIdInvalid);

        public static string FormatRange(string field, object min, object max) =>
            $"{field} must be between {min} and {max}";

        public static string FormatRangeWithUnits(string field, object min, object max, string units) =>
            $"{field} must be between {min} and {max} {units}";

        public static string FormatMinimum(string field, object min) =>
            $"{field} must be at least {min}";

        public static string FormatCollectionIdInvalid(string collectionId) =>
            string.Format(CultureInfo.InvariantCulture, _collectionIdInvalidFormat, collectionId);
    }

    public static class RangeValidation
    {
        public const string MaxRecordCount = "MaxRecordCount must be between 100 and 10,000";
        public const string DefaultRecordCount = "DefaultRecordCount must be at least 100";
        public const string MaxOffset = "MaxOffset must be between 1,000 and 1,000,000";
        public const string QueryTimeout = "Query.QueryTimeout must be between 5 seconds and 2 minutes";
        public const string MaxH3CellsPerQuery = "MaxH3CellsPerQuery must be between 100 and 1,000,000";
        public const string MaxVerticesPerGeometry = "MaxVerticesPerGeometry must be between 1,000 and 1,000,000";
        public const string MaxGeometrySize = "MaxGeometrySize must be between 1MB and 100MB";
        public const string MaxCoordinatePrecision = "MaxCoordinatePrecision must be between 1 and 15";
        public const string SimplifyTolerance = "SimplifyTolerance must be between 0 and 1000 meters";
        public const string MaxFeaturesPerEdit = "MaxFeaturesPerEdit must be between 1 and 10,000";
        public const string MaxEditsPerTransaction = "MaxEditsPerTransaction must be between 100 and 50,000";
        public const string MaxPayloadSize = "MaxPayloadSize must be between 1MB and 500MB";
        public const string MaxAttachmentSize = "MaxAttachmentSize must be between 1MB and 100MB";
        public const string MaxAttachmentsPerFeature = "MaxAttachmentsPerFeature must be between 1 and 100";
        public const string MaxTotalAttachmentSize = "MaxTotalAttachmentSize must be between 10MB and 1GB";
        public const string MaxTileZoom = "MaxTileZoom must be between 1 and 24";
        public const string MinTileZoom = "MinTileZoom must be between 0 and 10";
        public const string MaxFeaturesPerTile = "MaxFeaturesPerTile must be between 1,000 and 1,000,000";
        public const string TileTimeout = "Tiles.TileTimeout must be between 1 second and 1 minute";
        public const string MaxTileSize = "MaxTileSize must be a positive number of encoded vector tile bytes";
        public const string MaxConcurrentQueries = "MaxConcurrentQueries must be between 10 and 1,000";
        public const string MaxConnectionPoolSize = "MaxConnectionPoolSize must be between 1 and 1000";
        public const string RequestTimeout = "Connections.RequestTimeout must be between 10 seconds and 10 minutes";
        public const string MaxPreviewSize = "MaxPreviewSize must be between 1MB and 50MB";
        public const string MaxSyncImportSize = "MaxSyncImportSize must be between 10MB and 500MB";
        public const string MaxImportSize = "MaxImportSize must be between 50MB and 5GB";
        public const string MaxPreviewFeatures = "MaxPreviewFeatures must be between 10 and 1,000";
        public const string MaxPreviewCountScan = "MaxPreviewCountScan must be between 10 and 1,000,000";
        public const string BatchSize = "BatchSize must be between 100 and 10,000";
        public const string MaxVertices = "MaxVertices must be between 1,000 and 100,000";
        public const string MaxRings = "MaxRings must be between 10 and 1,000";
        public const string CoordinatePrecision = "CoordinatePrecision must be between 1 and 15";
        public const string MaxWkbSize = "MaxWkbSize must be between 100KB and 10MB";
        public const string MaxAttributeLength = "MaxAttributeLength must be between 1,000 and 1,000,000";

        // Cache options validation messages
        public const string DefaultTtlSeconds = "DefaultTtlSeconds must be between 1 and 86400 (24 hours)";
        public const string ServiceTtlSeconds = "ServiceTtlSeconds must be between 1 and 86400 (24 hours)";
        public const string LayerTtlSeconds = "LayerTtlSeconds must be between 1 and 86400 (24 hours)";
        public const string QueryTtlSeconds = "QueryTtlSeconds must be between 1 and 3600 (1 hour)";
        public const string NegativeTtlSeconds = "NegativeTtlSeconds must be between 1 and 3600 (1 hour)";
        public const string JitterPercentage = "JitterPercentage must be between 0 and 0.5 (50%)";
        public const string MaxEntries = "MaxEntries must be between 10 and 500000";
        public const string BackgroundRefreshThreshold = "BackgroundRefreshThreshold must be between 0.05 and 0.75";
        public const string MaxConcurrentRefreshes = "MaxConcurrentRefreshes must be between 1 and 100";
        public const string RefreshTimeoutSeconds = "RefreshTimeoutSeconds must be between 5 and 120";

        // StandardTtl options validation messages
        public const string VeryShortTtlSeconds = "VeryShortTtlSeconds must be between 1 and 300 (5 minutes)";
        public const string ShortTtlSeconds = "ShortTtlSeconds must be between 1 and 600 (10 minutes)";
        public const string MediumTtlSeconds = "MediumTtlSeconds must be between 1 and 3600 (1 hour)";
        public const string LongTtlSeconds = "LongTtlSeconds must be between 1 and 86400 (1 day)";
        public const string VeryLongTtlSeconds = "VeryLongTtlSeconds must be between 1 and 86400 (1 day)";

        // Adaptive sampling options validation messages
        public const string BaseSamplingRate = "BaseSamplingRate must be between 0.001 and 1.0";
        public const string MinSamplingRate = "MinSamplingRate must be between 0.001 and 0.5";
        public const string MaxSamplingRate = "MaxSamplingRate must be between 0.1 and 1.0";
        public const string CpuThreshold = "CpuThreshold must be between 30 and 95";
        public const string MemoryThreshold = "MemoryThreshold must be between 30 and 95";
        public const string ActiveRequestThreshold = "ActiveRequestThreshold must be between 10 and 1000";
        public const string ResponseTimeThresholdMs = "ResponseTimeThresholdMs must be between 100 and 10000";
        public const string ErrorRateThreshold = "ErrorRateThreshold must be between 0.1 and 50";
        public const string ErrorMultiplier = "ErrorMultiplier must be between 1.5 and 10";
        public const string ErrorWindowMinutes = "ErrorWindowMinutes must be between 1 and 30";
        public const string CriticalRate = "CriticalRate must be between 0.1 and 1.0";
        public const string ImportantRate = "ImportantRate must be between 0.05 and 1.0";
        public const string NormalRate = "NormalRate must be between 0.01 and 1.0";
        public const string BackgroundRate = "BackgroundRate must be between 0.001 and 0.1";

        // File storage validation messages
        public const string AwsS3BucketName = "AwsS3.BucketName must be between 3 and 63 characters";
        public const string AzureBlobContainerName = "AzureBlob.ContainerName must be between 3 and 63 characters";

        // Geometry and spatial validation messages
        public const string SridRange = "SRID must be between 0 and 999,999.";
        public const string LongitudeRange = "Longitude must be between -180 and 180 degrees.";
        public const string LatitudeRange = "Latitude must be between -90 and 90 degrees.";

        // Configuration validation messages
        public const string AdaptiveSamplingLoadCpuThreshold = "AdaptiveSampling:Load:CpuThreshold must be between 30 and 95";
        public const string AdaptiveSamplingLoadMemoryThreshold = "AdaptiveSampling:Load:MemoryThreshold must be between 30 and 95";
        public const string AdaptiveSamplingBaseSamplingRate = "AdaptiveSampling:BaseSamplingRate must be between 0.0 and 1.0";
    }

    public static class Security
    {
        public const string Unauthorized = "Authentication is required to access this resource.";
        public const string Forbidden = "Forbidden to perform {0}";
        public const string UnauthorizedOperation = "Unauthorized to perform {0}";
        public const string InvalidToken = "Invalid or expired authentication token.";
        public const string TokenRequired = "Authentication token is required.";
        private static readonly CompositeFormat _forbiddenFormat = CompositeFormat.Parse(Forbidden);
        private static readonly CompositeFormat _unauthorizedOperationFormat = CompositeFormat.Parse(UnauthorizedOperation);

        public static string FormatForbidden(string operation) =>
            string.Format(CultureInfo.InvariantCulture, _forbiddenFormat, operation);

        public static string FormatUnauthorized(string operation) =>
            string.Format(CultureInfo.InvariantCulture, _unauthorizedOperationFormat, operation);
    }

    public static class System
    {
        public const string ServiceUnavailable = "The service is temporarily unavailable. Please try again later.";

        public const string RequestTimeout = "The request timed out.";

        public const string RequestCancelled = "The request was cancelled or timed out.";

        public const string ConflictState = "The request could not be completed due to a conflict with the current state.";

        public const string InvalidOperation = "The requested operation is not valid in the current state.";

        public const string NotSupported = "The requested operation is not supported.";

        public const string UnexpectedError = "An unexpected error occurred.";

        public const string PayloadTooLarge = "Request payload is too large.";

        public const string TooManyRequests = "Too many requests. Please try again later.";

        // Protocol-specific operation error messages

        public const string InvalidObjectIdValue = "Invalid objectId value. objectIds parameter must contain only numeric values.";

        public const string InvalidSpatialParameters = "Invalid spatial parameters: {0}";

        public const string InvalidTemporalParameters = "Invalid temporal parameters: {0}";

        public const string InvalidTimeParameter = "Invalid time parameter: {0}";

        public const string InvalidTimeParameterFormat = "Invalid time parameter format: {0}";

        public const string RequiredParameterMissing = Validation.RequiredParameterMissing;
        private static readonly CompositeFormat _invalidSpatialParametersFormat = CompositeFormat.Parse(InvalidSpatialParameters);
        private static readonly CompositeFormat _invalidTemporalParametersFormat = CompositeFormat.Parse(InvalidTemporalParameters);
        private static readonly CompositeFormat _invalidTimeParameterFormatValue = CompositeFormat.Parse(InvalidTimeParameter);
        private static readonly CompositeFormat _invalidTimeParameterFormatFormat = CompositeFormat.Parse(InvalidTimeParameterFormat);

        public static string FormatInvalidSpatialParameters(string details) =>
            string.Format(CultureInfo.InvariantCulture, _invalidSpatialParametersFormat, details);

        public static string FormatInvalidTemporalParameters(string details) =>
            string.Format(CultureInfo.InvariantCulture, _invalidTemporalParametersFormat, details);

        public static string FormatInvalidTimeParameter(string details) =>
            string.Format(CultureInfo.InvariantCulture, _invalidTimeParameterFormatValue, details);

        public static string FormatInvalidTimeParameterFormat(string timeValue) =>
            string.Format(CultureInfo.InvariantCulture, _invalidTimeParameterFormatFormat, timeValue);
    }

    public static class OData
    {
        public const string ApplyParameterRequired = "$apply parameter is required.";
        public const string SearchParameterRequired = "$search parameter is required.";
    }

    public static class Import
    {
        public const string FileNotFound = "File not found.";
        public const string InvalidFileFormat = "Invalid file format.";
        public const string UnsupportedFileType = "Unsupported file type.";
        public const string FileTooLarge = "File is too large.";
        public const string ImportFailed = "Import operation failed.";
        public const string ProcessingFailed = "File processing failed.";
    }
}
