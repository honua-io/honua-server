// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Microsoft.Extensions.Logging;

namespace Honua.Server.Features.Admin;

internal static partial class ServiceSettingsLog
{
    [LoggerMessage(EventId = 4660, Level = LogLevel.Warning, Message = "Failed to list services")]
    public static partial void ListServicesFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 4661, Level = LogLevel.Warning, Message = "Failed to get service settings for {ServiceName}")]
    public static partial void GetServiceSettingsFailed(ILogger logger, string serviceName, Exception exception);

    [LoggerMessage(EventId = 4662, Level = LogLevel.Warning, Message = "Failed to update protocols for {ServiceName}")]
    public static partial void UpdateProtocolsFailed(ILogger logger, string serviceName, Exception exception);

    [LoggerMessage(EventId = 4663, Level = LogLevel.Warning, Message = "Failed to update MapServer settings for {ServiceName}")]
    public static partial void UpdateMapServerSettingsFailed(ILogger logger, string serviceName, Exception exception);

    [LoggerMessage(EventId = 4664, Level = LogLevel.Warning, Message = "Failed to update access policy for {ServiceName}")]
    public static partial void UpdateAccessPolicyFailed(ILogger logger, string serviceName, Exception exception);

    [LoggerMessage(EventId = 4665, Level = LogLevel.Warning, Message = "Failed to update time info for {ServiceName}")]
    public static partial void UpdateTimeInfoFailed(ILogger logger, string serviceName, Exception exception);

    [LoggerMessage(EventId = 4666, Level = LogLevel.Warning, Message = "Failed to update layer metadata for {ServiceName}/{LayerId}")]
    public static partial void UpdateLayerMetadataFailed(ILogger logger, string serviceName, int layerId, Exception exception);

    [LoggerMessage(EventId = 4667, Level = LogLevel.Warning, Message = "Failed to invalidate service catalog cache for {ServiceName}")]
    public static partial void InvalidateServiceCatalogCacheFailed(ILogger logger, string serviceName, Exception exception);

    [LoggerMessage(EventId = 4668, Level = LogLevel.Warning, Message = "Failed to get service settings caps for {ServiceName}")]
    public static partial void GetServiceSettingsCapsFailed(ILogger logger, string serviceName, Exception exception);

    [LoggerMessage(EventId = 4669, Level = LogLevel.Warning, Message = "Failed to update service settings caps for {ServiceName}")]
    public static partial void UpdateServiceSettingsCapsFailed(ILogger logger, string serviceName, Exception exception);

    [LoggerMessage(EventId = 4670, Level = LogLevel.Warning, Message = "Failed to get service discovery metadata for {ServiceName}")]
    public static partial void GetServiceDiscoveryFailed(ILogger logger, string serviceName, Exception exception);

    [LoggerMessage(EventId = 4671, Level = LogLevel.Warning, Message = "Failed to update service discovery metadata for {ServiceName}")]
    public static partial void UpdateServiceDiscoveryFailed(ILogger logger, string serviceName, Exception exception);
}
