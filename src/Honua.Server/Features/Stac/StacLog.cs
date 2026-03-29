// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Features.Stac;

/// <summary>
/// Source-generated log messages for STAC API endpoints.
/// Event IDs: 5800–5849.
/// </summary>
internal static partial class StacLog
{
    // 5800–5809: Catalog

    [LoggerMessage(EventId = 5800, Level = LogLevel.Information,
        Message = "STAC catalog requested")]
    public static partial void CatalogRequested(ILogger logger);

    [LoggerMessage(EventId = 5801, Level = LogLevel.Debug,
        Message = "STAC catalog returned with {CollectionCount} child collections")]
    public static partial void CatalogReturned(ILogger logger, int collectionCount);

    // 5810–5819: Collections

    [LoggerMessage(EventId = 5810, Level = LogLevel.Information,
        Message = "STAC collections list requested")]
    public static partial void CollectionsRequested(ILogger logger);

    [LoggerMessage(EventId = 5811, Level = LogLevel.Debug,
        Message = "STAC collections returned: {Count}")]
    public static partial void CollectionsReturned(ILogger logger, int count);

    [LoggerMessage(EventId = 5812, Level = LogLevel.Information,
        Message = "STAC collection {CollectionId} requested")]
    public static partial void CollectionRequested(ILogger logger, string collectionId);

    [LoggerMessage(EventId = 5813, Level = LogLevel.Warning,
        Message = "STAC collection {CollectionId} not found")]
    public static partial void CollectionNotFound(ILogger logger, string collectionId);

    // 5820–5829: Items

    [LoggerMessage(EventId = 5820, Level = LogLevel.Information,
        Message = "STAC items requested for collection {CollectionId} (limit={Limit})")]
    public static partial void ItemsRequested(ILogger logger, string collectionId, int? limit);

    [LoggerMessage(EventId = 5821, Level = LogLevel.Debug,
        Message = "STAC items returned: {Count} for collection {CollectionId}")]
    public static partial void ItemsReturned(ILogger logger, int count, string collectionId);

    [LoggerMessage(EventId = 5822, Level = LogLevel.Information,
        Message = "STAC item {ItemId} requested from collection {CollectionId}")]
    public static partial void ItemRequested(ILogger logger, string collectionId, string itemId);

    [LoggerMessage(EventId = 5823, Level = LogLevel.Warning,
        Message = "STAC item {ItemId} not found in collection {CollectionId}")]
    public static partial void ItemNotFound(ILogger logger, string collectionId, string itemId);

    // 5830–5839: Search

    [LoggerMessage(EventId = 5830, Level = LogLevel.Information,
        Message = "STAC search requested (collections={CollectionCount}, limit={Limit})")]
    public static partial void SearchRequested(ILogger logger, int collectionCount, int? limit);

    [LoggerMessage(EventId = 5831, Level = LogLevel.Debug,
        Message = "STAC search returned {Count} items")]
    public static partial void SearchReturned(ILogger logger, int count);

    // 5840–5849: Errors

    [LoggerMessage(EventId = 5840, Level = LogLevel.Error,
        Message = "STAC operation failed")]
    public static partial void OperationFailed(ILogger logger, Exception exception);
}
