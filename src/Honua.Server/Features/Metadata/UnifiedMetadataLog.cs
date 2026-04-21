// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Microsoft.Extensions.Logging;

namespace Honua.Server.Features.Metadata;

/// <summary>
/// High-performance logging for unified metadata operations.
/// Provides structured logging for metadata generation and formatting activities.
/// </summary>
internal static partial class UnifiedMetadataLog
{
    [LoggerMessage(
        EventId = 1001,
        Level = LogLevel.Information,
        Message = "Generating unified service metadata for {ServiceName}")]
    public static partial void ServiceMetadataGenerationStarted(ILogger logger, string serviceName);

    [LoggerMessage(
        EventId = 1002,
        Level = LogLevel.Information,
        Message = "Generated unified service metadata for {ServiceName} with {LayerCount} layers in {ElapsedMs}ms")]
    public static partial void ServiceMetadataGenerationCompleted(ILogger logger, string serviceName, int layerCount, long elapsedMs);

    [LoggerMessage(
        EventId = 1003,
        Level = LogLevel.Warning,
        Message = "Failed to generate service metadata for {ServiceName}: {ErrorMessage}")]
    public static partial void ServiceMetadataGenerationFailed(ILogger logger, string serviceName, string errorMessage, Exception exception);

    [LoggerMessage(
        EventId = 1011,
        Level = LogLevel.Information,
        Message = "Generating unified layer metadata for {ServiceName}/{LayerName}")]
    public static partial void LayerMetadataGenerationStarted(ILogger logger, string serviceName, string layerName);

    [LoggerMessage(
        EventId = 1012,
        Level = LogLevel.Information,
        Message = "Generated unified layer metadata for {ServiceName}/{LayerName} with {FieldCount} fields in {ElapsedMs}ms")]
    public static partial void LayerMetadataGenerationCompleted(ILogger logger, string serviceName, string layerName, int fieldCount, long elapsedMs);

    [LoggerMessage(
        EventId = 1013,
        Level = LogLevel.Warning,
        Message = "Failed to generate layer metadata for {ServiceName}/{LayerName}: {ErrorMessage}")]
    public static partial void LayerMetadataGenerationFailed(ILogger logger, string serviceName, string layerName, string errorMessage, Exception exception);

    [LoggerMessage(
        EventId = 1021,
        Level = LogLevel.Information,
        Message = "Generating global capabilities")]
    public static partial void GlobalCapabilitiesGenerationStarted(ILogger logger);

    [LoggerMessage(
        EventId = 1022,
        Level = LogLevel.Information,
        Message = "Generated global capabilities with {ServiceCount} services in {ElapsedMs}ms")]
    public static partial void GlobalCapabilitiesGenerationCompleted(ILogger logger, int serviceCount, long elapsedMs);

    [LoggerMessage(
        EventId = 1023,
        Level = LogLevel.Warning,
        Message = "Failed to generate global capabilities: {ErrorMessage}")]
    public static partial void GlobalCapabilitiesGenerationFailed(ILogger logger, string errorMessage, Exception exception);

    [LoggerMessage(
        EventId = 1031,
        Level = LogLevel.Information,
        Message = "Formatting metadata for protocol {Protocol} and service {ServiceName}")]
    public static partial void MetadataFormattingStarted(ILogger logger, string protocol, string serviceName);

    [LoggerMessage(
        EventId = 1032,
        Level = LogLevel.Information,
        Message = "Formatted metadata for protocol {Protocol} and service {ServiceName} in {ElapsedMs}ms")]
    public static partial void MetadataFormattingCompleted(ILogger logger, string protocol, string serviceName, long elapsedMs);

    [LoggerMessage(
        EventId = 1033,
        Level = LogLevel.Warning,
        Message = "Failed to format metadata for protocol {Protocol} and service {ServiceName}: {ErrorMessage}")]
    public static partial void MetadataFormattingFailed(ILogger logger, string protocol, string serviceName, string errorMessage, Exception exception);

    [LoggerMessage(
        EventId = 1041,
        Level = LogLevel.Debug,
        Message = "Metadata cache hit for key {CacheKey}")]
    public static partial void MetadataCacheHit(ILogger logger, string cacheKey);

    [LoggerMessage(
        EventId = 1042,
        Level = LogLevel.Debug,
        Message = "Metadata cache miss for key {CacheKey}")]
    public static partial void MetadataCacheMiss(ILogger logger, string cacheKey);

    [LoggerMessage(
        EventId = 1043,
        Level = LogLevel.Debug,
        Message = "Storing metadata in cache with key {CacheKey} for {DurationSeconds} seconds")]
    public static partial void MetadataCacheStore(ILogger logger, string cacheKey, double durationSeconds);

    [LoggerMessage(
        EventId = 1051,
        Level = LogLevel.Warning,
        Message = "Expensive metadata computation timed out for {ResourceType} {ResourceName} after {TimeoutMs}ms")]
    public static partial void ExpensiveMetadataTimeout(ILogger logger, string resourceType, string resourceName, int timeoutMs);

    [LoggerMessage(
        EventId = 1052,
        Level = LogLevel.Information,
        Message = "Skipping expensive metadata computation for {ResourceType} {ResourceName} (disabled in options)")]
    public static partial void ExpensiveMetadataSkipped(ILogger logger, string resourceType, string resourceName);

    [LoggerMessage(
        EventId = 1061,
        Level = LogLevel.Information,
        Message = "Protocol {Protocol} formatter requested for {RequestType}")]
    public static partial void ProtocolFormatterRequested(ILogger logger, string protocol, string requestType);

    [LoggerMessage(
        EventId = 1062,
        Level = LogLevel.Warning,
        Message = "Protocol {Protocol} formatter not found for {RequestType}")]
    public static partial void ProtocolFormatterNotFound(ILogger logger, string protocol, string requestType);
}

/// <summary>
/// High-performance logging for WFS 2.0 capabilities formatting.
/// </summary>
internal static partial class Wfs20Log
{
    [LoggerMessage(
        EventId = 2001,
        Level = LogLevel.Information,
        Message = "Formatting WFS 2.0 capabilities for service {ServiceName}")]
    public static partial void FormatCapabilitiesRequested(ILogger logger, string serviceName);

    [LoggerMessage(
        EventId = 2002,
        Level = LogLevel.Information,
        Message = "Formatted WFS 2.0 capabilities for service {ServiceName}")]
    public static partial void FormatCapabilitiesCompleted(ILogger logger, string serviceName);

    [LoggerMessage(
        EventId = 2003,
        Level = LogLevel.Warning,
        Message = "Failed to format WFS 2.0 capabilities for service {ServiceName}: {ErrorMessage}")]
    public static partial void FormatCapabilitiesFailed(ILogger logger, string serviceName, string errorMessage, Exception exception);

    [LoggerMessage(
        EventId = 2011,
        Level = LogLevel.Information,
        Message = "Formatting global WFS 2.0 capabilities")]
    public static partial void FormatGlobalCapabilitiesRequested(ILogger logger);

    [LoggerMessage(
        EventId = 2012,
        Level = LogLevel.Information,
        Message = "Formatted global WFS 2.0 capabilities with {FeatureTypeCount} feature types")]
    public static partial void FormatGlobalCapabilitiesCompleted(ILogger logger, int featureTypeCount);

    [LoggerMessage(
        EventId = 2013,
        Level = LogLevel.Warning,
        Message = "Failed to format global WFS 2.0 capabilities: {ErrorMessage}")]
    public static partial void FormatGlobalCapabilitiesFailed(ILogger logger, string errorMessage, Exception exception);
}

/// <summary>
/// High-performance logging for FeatureServer capabilities formatting.
/// </summary>
internal static partial class FeatureServerLog
{
    [LoggerMessage(
        EventId = 3001,
        Level = LogLevel.Information,
        Message = "Formatting FeatureServer capabilities for service {ServiceName}")]
    public static partial void FormatCapabilitiesRequested(ILogger logger, string serviceName);

    [LoggerMessage(
        EventId = 3002,
        Level = LogLevel.Information,
        Message = "Formatted FeatureServer capabilities for service {ServiceName} with {LayerCount} layers")]
    public static partial void FormatCapabilitiesCompleted(ILogger logger, string serviceName, int layerCount);

    [LoggerMessage(
        EventId = 3003,
        Level = LogLevel.Warning,
        Message = "Failed to format FeatureServer capabilities for service {ServiceName}: {ErrorMessage}")]
    public static partial void FormatCapabilitiesFailed(ILogger logger, string serviceName, string errorMessage, Exception exception);

    [LoggerMessage(
        EventId = 3011,
        Level = LogLevel.Information,
        Message = "Formatting FeatureServer layer capabilities for service {ServiceName}, layer {LayerId}")]
    public static partial void FormatLayerCapabilitiesRequested(ILogger logger, string serviceName, int layerId);

    [LoggerMessage(
        EventId = 3012,
        Level = LogLevel.Information,
        Message = "Formatted FeatureServer layer capabilities for service {ServiceName}, layer {LayerId}")]
    public static partial void FormatLayerCapabilitiesCompleted(ILogger logger, string serviceName, int layerId);

    [LoggerMessage(
        EventId = 3013,
        Level = LogLevel.Warning,
        Message = "Failed to format FeatureServer layer capabilities for service {ServiceName}, layer {LayerId}: {ErrorMessage}")]
    public static partial void FormatLayerCapabilitiesFailed(ILogger logger, string serviceName, int layerId, string errorMessage, Exception exception);

    [LoggerMessage(
        EventId = 3021,
        Level = LogLevel.Information,
        Message = "Service metadata requested for {ServiceName}")]
    public static partial void ServiceMetadataRequested(ILogger logger, string serviceName);

    [LoggerMessage(
        EventId = 3022,
        Level = LogLevel.Information,
        Message = "Service metadata returned for {ServiceName} with {LayerCount} layers")]
    public static partial void ServiceMetadataReturned(ILogger logger, string serviceName, int layerCount);

    [LoggerMessage(
        EventId = 3023,
        Level = LogLevel.Warning,
        Message = "Service metadata failed for {ServiceName}: {ErrorMessage}")]
    public static partial void ServiceMetadataFailed(ILogger logger, string serviceName, string errorMessage, Exception exception);

    [LoggerMessage(
        EventId = 3031,
        Level = LogLevel.Information,
        Message = "Layer metadata requested for service {ServiceName}, layer {LayerId}")]
    public static partial void LayerMetadataRequested(ILogger logger, string serviceName, int layerId);

    [LoggerMessage(
        EventId = 3032,
        Level = LogLevel.Information,
        Message = "Layer metadata returned for service {ServiceName}, layer {LayerId} ({LayerName})")]
    public static partial void LayerMetadataReturned(ILogger logger, string serviceName, int layerId, string layerName);

    [LoggerMessage(
        EventId = 3033,
        Level = LogLevel.Warning,
        Message = "Layer metadata failed for service {ServiceName}, layer {LayerId}: {ErrorMessage}")]
    public static partial void LayerMetadataFailed(ILogger logger, string serviceName, int layerId, string errorMessage, Exception exception);
}

/// <summary>
/// High-performance logging for OGC API Features capabilities formatting.
/// </summary>
internal static partial class OgcFeaturesLog
{
    [LoggerMessage(
        EventId = 4001,
        Level = LogLevel.Information,
        Message = "Formatting OGC API Features capabilities for service {ServiceName}")]
    public static partial void FormatCapabilitiesRequested(ILogger logger, string serviceName);

    [LoggerMessage(
        EventId = 4002,
        Level = LogLevel.Information,
        Message = "Formatted OGC API Features capabilities for service {ServiceName}")]
    public static partial void FormatCapabilitiesCompleted(ILogger logger, string serviceName);

    [LoggerMessage(
        EventId = 4003,
        Level = LogLevel.Warning,
        Message = "Failed to format OGC API Features capabilities for service {ServiceName}: {ErrorMessage}")]
    public static partial void FormatCapabilitiesFailed(ILogger logger, string serviceName, string errorMessage, Exception exception);

    [LoggerMessage(
        EventId = 4011,
        Level = LogLevel.Information,
        Message = "Formatting global OGC API Features capabilities")]
    public static partial void FormatGlobalCapabilitiesRequested(ILogger logger);

    [LoggerMessage(
        EventId = 4012,
        Level = LogLevel.Information,
        Message = "Formatted global OGC API Features capabilities with {LinkCount} links")]
    public static partial void FormatGlobalCapabilitiesCompleted(ILogger logger, int linkCount);

    [LoggerMessage(
        EventId = 4013,
        Level = LogLevel.Warning,
        Message = "Failed to format global OGC API Features capabilities: {ErrorMessage}")]
    public static partial void FormatGlobalCapabilitiesFailed(ILogger logger, string errorMessage, Exception exception);

    [LoggerMessage(
        EventId = 4021,
        Level = LogLevel.Information,
        Message = "Landing page requested")]
    public static partial void LandingPageRequested(ILogger logger);

    [LoggerMessage(
        EventId = 4022,
        Level = LogLevel.Information,
        Message = "Landing page returned")]
    public static partial void LandingPageReturned(ILogger logger);

    [LoggerMessage(
        EventId = 4031,
        Level = LogLevel.Information,
        Message = "Conformance declaration requested")]
    public static partial void FormatConformanceRequested(ILogger logger);

    [LoggerMessage(
        EventId = 4032,
        Level = LogLevel.Information,
        Message = "Conformance declaration returned with {ConformanceCount} classes")]
    public static partial void FormatConformanceCompleted(ILogger logger, int conformanceCount);

    [LoggerMessage(
        EventId = 4033,
        Level = LogLevel.Warning,
        Message = "Failed to format conformance declaration: {ErrorMessage}")]
    public static partial void FormatConformanceFailed(ILogger logger, string errorMessage, Exception exception);

    [LoggerMessage(
        EventId = 4041,
        Level = LogLevel.Information,
        Message = "Collections requested for service {ServiceName}")]
    public static partial void FormatCollectionsRequested(ILogger logger, string serviceName);

    [LoggerMessage(
        EventId = 4042,
        Level = LogLevel.Information,
        Message = "Collections returned for service {ServiceName} with {CollectionCount} collections")]
    public static partial void FormatCollectionsCompleted(ILogger logger, string serviceName, int collectionCount);

    [LoggerMessage(
        EventId = 4043,
        Level = LogLevel.Warning,
        Message = "Failed to format collections for service {ServiceName}: {ErrorMessage}")]
    public static partial void FormatCollectionsFailed(ILogger logger, string serviceName, string errorMessage, Exception exception);

    [LoggerMessage(
        EventId = 4051,
        Level = LogLevel.Information,
        Message = "OpenAPI specification requested")]
    public static partial void FormatOpenApiRequested(ILogger logger);

    [LoggerMessage(
        EventId = 4052,
        Level = LogLevel.Information,
        Message = "OpenAPI specification returned")]
    public static partial void FormatOpenApiCompleted(ILogger logger);

    [LoggerMessage(
        EventId = 4053,
        Level = LogLevel.Warning,
        Message = "Failed to format OpenAPI specification: {ErrorMessage}")]
    public static partial void FormatOpenApiFailed(ILogger logger, string errorMessage, Exception exception);
}

/// <summary>
/// High-performance logging for OData capabilities formatting.
/// </summary>
internal static partial class ODataLog
{
    [LoggerMessage(
        EventId = 5001,
        Level = LogLevel.Information,
        Message = "Formatting OData capabilities for service {ServiceName}")]
    public static partial void FormatCapabilitiesRequested(ILogger logger, string serviceName);

    [LoggerMessage(
        EventId = 5002,
        Level = LogLevel.Information,
        Message = "Formatted OData capabilities for service {ServiceName} with {EntitySetCount} entity sets")]
    public static partial void FormatCapabilitiesCompleted(ILogger logger, string serviceName, int entitySetCount);

    [LoggerMessage(
        EventId = 5003,
        Level = LogLevel.Warning,
        Message = "Failed to format OData capabilities for service {ServiceName}: {ErrorMessage}")]
    public static partial void FormatCapabilitiesFailed(ILogger logger, string serviceName, string errorMessage, Exception exception);

    [LoggerMessage(
        EventId = 5011,
        Level = LogLevel.Information,
        Message = "Formatting global OData capabilities")]
    public static partial void FormatGlobalCapabilitiesRequested(ILogger logger);

    [LoggerMessage(
        EventId = 5012,
        Level = LogLevel.Information,
        Message = "Formatted global OData capabilities with {EntitySetCount} entity sets")]
    public static partial void FormatGlobalCapabilitiesCompleted(ILogger logger, int entitySetCount);

    [LoggerMessage(
        EventId = 5013,
        Level = LogLevel.Warning,
        Message = "Failed to format global OData capabilities: {ErrorMessage}")]
    public static partial void FormatGlobalCapabilitiesFailed(ILogger logger, string errorMessage, Exception exception);

    [LoggerMessage(
        EventId = 5021,
        Level = LogLevel.Information,
        Message = "OData metadata document requested for service {ServiceName}")]
    public static partial void FormatMetadataRequested(ILogger logger, string serviceName);

    [LoggerMessage(
        EventId = 5022,
        Level = LogLevel.Information,
        Message = "OData metadata document returned for service {ServiceName}")]
    public static partial void FormatMetadataCompleted(ILogger logger, string serviceName);

    [LoggerMessage(
        EventId = 5023,
        Level = LogLevel.Warning,
        Message = "Failed to format OData metadata document for service {ServiceName}: {ErrorMessage}")]
    public static partial void FormatMetadataFailed(ILogger logger, string serviceName, string errorMessage, Exception exception);

    [LoggerMessage(
        EventId = 5031,
        Level = LogLevel.Information,
        Message = "Global OData metadata document requested")]
    public static partial void FormatGlobalMetadataRequested(ILogger logger);

    [LoggerMessage(
        EventId = 5032,
        Level = LogLevel.Information,
        Message = "Global OData metadata document returned")]
    public static partial void FormatGlobalMetadataCompleted(ILogger logger);

    [LoggerMessage(
        EventId = 5033,
        Level = LogLevel.Warning,
        Message = "Failed to format global OData metadata document: {ErrorMessage}")]
    public static partial void FormatGlobalMetadataFailed(ILogger logger, string errorMessage, Exception exception);
}