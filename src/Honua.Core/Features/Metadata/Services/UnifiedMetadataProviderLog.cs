// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Microsoft.Extensions.Logging;

namespace Honua.Core.Features.Metadata.Services;

internal static partial class UnifiedMetadataProviderLog
{
    [LoggerMessage(EventId = 7660, Level = LogLevel.Debug, Message = "Generating service metadata for {ServiceName}")]
    public static partial void GeneratingServiceMetadata(ILogger logger, string serviceName);

    [LoggerMessage(EventId = 7661, Level = LogLevel.Debug, Message = "Generated service metadata for {ServiceName} with {LayerCount} layers")]
    public static partial void GeneratedServiceMetadata(ILogger logger, string serviceName, int layerCount);

    [LoggerMessage(EventId = 7662, Level = LogLevel.Error, Message = "Failed to generate service metadata for {ServiceName}")]
    public static partial void GenerateServiceMetadataFailed(ILogger logger, string serviceName, Exception exception);

    [LoggerMessage(EventId = 7663, Level = LogLevel.Debug, Message = "Generating layer metadata for {ServiceName}/{LayerName}")]
    public static partial void GeneratingLayerMetadata(ILogger logger, string serviceName, string layerName);

    [LoggerMessage(EventId = 7664, Level = LogLevel.Debug, Message = "Generated layer metadata for {ServiceName}/{LayerName}")]
    public static partial void GeneratedLayerMetadata(ILogger logger, string serviceName, string layerName);

    [LoggerMessage(EventId = 7665, Level = LogLevel.Error, Message = "Failed to generate layer metadata for {ServiceName}/{LayerName}")]
    public static partial void GenerateLayerMetadataFailed(ILogger logger, string serviceName, string layerName, Exception exception);

    [LoggerMessage(EventId = 7666, Level = LogLevel.Debug, Message = "Generating global capabilities")]
    public static partial void GeneratingGlobalCapabilities(ILogger logger);

    [LoggerMessage(EventId = 7667, Level = LogLevel.Debug, Message = "Generated global capabilities")]
    public static partial void GeneratedGlobalCapabilities(ILogger logger);

    [LoggerMessage(EventId = 7668, Level = LogLevel.Error, Message = "Failed to generate global capabilities")]
    public static partial void GenerateGlobalCapabilitiesFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 7669, Level = LogLevel.Warning, Message = "Field statistics computation timed out for {LayerName}.{FieldName}")]
    public static partial void FieldStatisticsTimedOut(ILogger logger, string layerName, string fieldName);

    [LoggerMessage(EventId = 7670, Level = LogLevel.Warning, Message = "Failed to compute field statistics for {LayerName}.{FieldName}")]
    public static partial void FieldStatisticsFailed(ILogger logger, string layerName, string fieldName, Exception exception);
}
