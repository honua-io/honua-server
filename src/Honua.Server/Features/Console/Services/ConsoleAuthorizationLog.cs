// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Features.Console.Services;

/// <summary>
/// Structured logging hooks for Console authorization decisions.
/// </summary>
internal static partial class ConsoleAuthorizationLog
{
    [LoggerMessage(EventId = 4800, Level = LogLevel.Debug,
        Message = "Console authorization profile resolved for {UserId}: {CapabilityCount} capabilities (isAdmin={IsAdmin})")]
    public static partial void ProfileResolved(ILogger logger, string userId, int capabilityCount, bool isAdmin);

    [LoggerMessage(EventId = 4801, Level = LogLevel.Debug,
        Message = "Evaluated {RouteCount} Console route entitlements for {UserId}")]
    public static partial void RouteEntitlementsEvaluated(ILogger logger, string userId, int routeCount);

    [LoggerMessage(EventId = 4802, Level = LogLevel.Debug,
        Message = "Evaluated item actions for {UserId}: item={ItemId} allowed={AllowedCount}")]
    public static partial void ItemActionsEvaluated(ILogger logger, string userId, string itemId, int allowedCount);
}
