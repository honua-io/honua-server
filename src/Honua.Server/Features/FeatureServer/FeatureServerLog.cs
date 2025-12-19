// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Features.FeatureServer;

/// <summary>
/// Structured logging for FeatureServer endpoints with source generation (AOT compatible)
/// </summary>
public static partial class FeatureServerLog
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
}
