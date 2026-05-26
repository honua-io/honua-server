// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.OpenData.Domain;

namespace Honua.Server.Features.Infrastructure.OpenData;

/// <summary>
/// Source-generated log messages for open-data publication APIs.
/// Event IDs: 9830-9849.
/// </summary>
internal static partial class OpenDataLog
{
    [LoggerMessage(EventId = 9830, Level = LogLevel.Information, Message = "Open-data page {ItemId} updated (published={IsPublished})")]
    public static partial void PageUpdated(ILogger logger, string itemId, bool isPublished);

    [LoggerMessage(EventId = 9831, Level = LogLevel.Debug, Message = "Open-data DCAT catalog generated with {ItemCount} items")]
    public static partial void DcatCatalogGenerated(ILogger logger, int itemCount);

    [LoggerMessage(EventId = 9832, Level = LogLevel.Information, Message = "STAC publication {Operation} for {CollectionId} changed to {Status}")]
    public static partial void StacPublicationChanged(
        ILogger logger,
        string operation,
        string collectionId,
        OpenDataStacPublicationStatus status);

    [LoggerMessage(EventId = 9833, Level = LogLevel.Warning, Message = "Open-data cache invalidation failed for {ItemId}")]
    public static partial void CacheInvalidationFailed(ILogger logger, string? itemId, Exception exception);

    [LoggerMessage(EventId = 9834, Level = LogLevel.Error, Message = "Open-data endpoint operation failed: {Operation}")]
    public static partial void EndpointFailed(ILogger logger, string operation, Exception exception);
}
