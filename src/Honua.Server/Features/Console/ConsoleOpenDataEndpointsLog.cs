// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Features.Console;

/// <summary>
/// Structured logging hooks for the Console open-data DCAT/STAC publication
/// endpoints. Anonymous-denial events deliberately omit item identifiers so the
/// public open-data surface cannot be turned into a log-driven enumeration
/// oracle.
/// </summary>
internal static partial class ConsoleOpenDataEndpointsLog
{
    [LoggerMessage(EventId = 4840, Level = LogLevel.Information,
        Message = "Console open-data page saved for {ItemId} by {PrincipalId}")]
    public static partial void PageSaved(ILogger logger, string itemId, string? principalId);

    [LoggerMessage(EventId = 4841, Level = LogLevel.Information,
        Message = "Console open-data published to STAC for {ItemId}: collection {CollectionId} (revision {Revision}) by {PrincipalId}")]
    public static partial void StacPublished(ILogger logger, string itemId, string? collectionId, long revision, string? principalId);

    [LoggerMessage(EventId = 4842, Level = LogLevel.Information,
        Message = "Console open-data unpublished from STAC for {ItemId} by {PrincipalId}")]
    public static partial void StacUnpublished(ILogger logger, string itemId, string? principalId);

    [LoggerMessage(EventId = 4843, Level = LogLevel.Information,
        Message = "Console open-data STAC publish denied for {ItemId}: {ReasonCode}")]
    public static partial void StacPublishDenied(ILogger logger, string itemId, string reasonCode);

    [LoggerMessage(EventId = 4844, Level = LogLevel.Information,
        Message = "Console open-data anonymous read granted for {ItemId}")]
    public static partial void PublicReadGranted(ILogger logger, string itemId);

    [LoggerMessage(EventId = 4845, Level = LogLevel.Information,
        Message = "Console open-data anonymous read denied")]
    public static partial void PublicReadDenied(ILogger logger);
}
