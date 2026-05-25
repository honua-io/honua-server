// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Console.Domain;

namespace Honua.Server.Features.Console;

/// <summary>
/// Structured logging hooks for the Console content/RBAC endpoints.
/// </summary>
internal static partial class ConsoleEndpointsLog
{
    [LoggerMessage(EventId = 4803, Level = LogLevel.Information,
        Message = "Console session loaded for {UserId}: items={ItemCount} routes={RouteCount}")]
    public static partial void SessionLoaded(ILogger logger, string userId, int itemCount, int routeCount);

    [LoggerMessage(EventId = 4804, Level = LogLevel.Information,
        Message = "Listed {Count} Console content items (total {Total})")]
    public static partial void ContentItemsListed(ILogger logger, int count, int total);

    [LoggerMessage(EventId = 4805, Level = LogLevel.Information,
        Message = "Created Console content item {ItemId} ({ItemType})")]
    public static partial void ContentItemCreated(ILogger logger, string itemId, ConsoleContentItemType itemType);

    [LoggerMessage(EventId = 4806, Level = LogLevel.Information,
        Message = "Updated Console content item {ItemId} (generation {Generation})")]
    public static partial void ContentItemUpdated(ILogger logger, string itemId, long? generation);

    [LoggerMessage(EventId = 4807, Level = LogLevel.Information,
        Message = "Deleted Console content item {ItemId}")]
    public static partial void ContentItemDeleted(ILogger logger, string itemId);

    [LoggerMessage(EventId = 4808, Level = LogLevel.Information,
        Message = "Evaluated Console actions: {TargetCount} targets ({ItemTargets} items, {RouteTargets} routes)")]
    public static partial void ActionCheckEvaluated(ILogger logger, int targetCount, int itemTargets, int routeTargets);

    [LoggerMessage(EventId = 4809, Level = LogLevel.Error,
        Message = "Console endpoint failure: {Operation}")]
    public static partial void EndpointFailed(ILogger logger, string operation, Exception exception);
}
