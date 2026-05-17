// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Microsoft.Extensions.Logging;

namespace Honua.Server.Features.Admin;

internal static partial class LayerPublishingLog
{
    [LoggerMessage(EventId = 9621, Level = LogLevel.Warning, Message = "Layer list failed: {Message}")]
    public static partial void LayerListFailed(ILogger logger, string message, Exception exception);

    [LoggerMessage(EventId = 9622, Level = LogLevel.Warning, Message = "Layer list invalid request")]
    public static partial void LayerListInvalidRequest(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 9623, Level = LogLevel.Warning, Message = "Layer list connection not found")]
    public static partial void LayerListConnectionNotFound(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 9624, Level = LogLevel.Error, Message = "Layer list failed due to invalid operation")]
    public static partial void LayerListInvalidOperation(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 9625, Level = LogLevel.Warning, Message = "Layer list forbidden")]
    public static partial void LayerListForbidden(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 9626, Level = LogLevel.Error, Message = "Layer publish migration failed: {Message}")]
    public static partial void LayerPublishMigrationFailed(ILogger logger, string? message, Exception? exception);

    [LoggerMessage(EventId = 9627, Level = LogLevel.Warning, Message = "Layer publish conflict")]
    public static partial void LayerPublishConflict(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 9628, Level = LogLevel.Warning, Message = "Layer publish not found")]
    public static partial void LayerPublishNotFound(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 9629, Level = LogLevel.Warning, Message = "Layer publish validation failed")]
    public static partial void LayerPublishValidationFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 9630, Level = LogLevel.Warning, Message = "Layer publish invalid request")]
    public static partial void LayerPublishInvalidRequest(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 9631, Level = LogLevel.Warning, Message = "Layer publish connection not found")]
    public static partial void LayerPublishConnectionNotFound(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 9632, Level = LogLevel.Error, Message = "Layer publish failed due to invalid operation")]
    public static partial void LayerPublishInvalidOperation(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 9633, Level = LogLevel.Error, Message = "Layer enable migration failed: {Message}")]
    public static partial void LayerEnableMigrationFailed(ILogger logger, string? message, Exception? exception);

    [LoggerMessage(EventId = 9634, Level = LogLevel.Warning, Message = "Layer toggle failed")]
    public static partial void LayerToggleFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 9635, Level = LogLevel.Warning, Message = "Layer toggle invalid request")]
    public static partial void LayerToggleInvalidRequest(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 9636, Level = LogLevel.Warning, Message = "Layer toggle connection not found")]
    public static partial void LayerToggleConnectionNotFound(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 9637, Level = LogLevel.Error, Message = "Layer toggle failed due to invalid operation")]
    public static partial void LayerToggleInvalidOperation(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 9638, Level = LogLevel.Error, Message = "Layer bulk enable migration failed: {Message}")]
    public static partial void LayerBulkEnableMigrationFailed(ILogger logger, string? message, Exception? exception);

    [LoggerMessage(EventId = 9639, Level = LogLevel.Warning, Message = "Layer bulk toggle failed")]
    public static partial void LayerBulkToggleFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 9640, Level = LogLevel.Warning, Message = "Layer bulk toggle invalid request")]
    public static partial void LayerBulkToggleInvalidRequest(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 9641, Level = LogLevel.Warning, Message = "Layer bulk toggle connection not found")]
    public static partial void LayerBulkToggleConnectionNotFound(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 9642, Level = LogLevel.Error, Message = "Layer bulk toggle failed due to invalid operation")]
    public static partial void LayerBulkToggleInvalidOperation(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 9643, Level = LogLevel.Warning, Message = "Failed to invalidate service catalog cache for {ServiceName}")]
    public static partial void InvalidateServiceCatalogCacheFailed(ILogger logger, string? serviceName, Exception exception);

    [LoggerMessage(EventId = 9644, Level = LogLevel.Error, Message = "Layer extent refresh migration failed: {Message}")]
    public static partial void LayerExtentRefreshMigrationFailed(ILogger logger, string? message, Exception? exception);

    [LoggerMessage(EventId = 9645, Level = LogLevel.Warning, Message = "Layer extent refresh not found")]
    public static partial void LayerExtentRefreshNotFound(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 9646, Level = LogLevel.Warning, Message = "Layer extent refresh failed")]
    public static partial void LayerExtentRefreshFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 9647, Level = LogLevel.Warning, Message = "Layer extent refresh invalid request")]
    public static partial void LayerExtentRefreshInvalidRequest(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 9648, Level = LogLevel.Warning, Message = "Layer extent refresh connection not found")]
    public static partial void LayerExtentRefreshConnectionNotFound(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 9649, Level = LogLevel.Error, Message = "Layer extent refresh failed due to invalid operation")]
    public static partial void LayerExtentRefreshInvalidOperation(ILogger logger, Exception exception);
}
