// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Protocols.Stac;

/// <summary>
/// Source-generated log messages for STAC API endpoints.
/// Event IDs: 5950–5999.
/// </summary>
internal static partial class StacLog
{
    // 5950–5955: Catalog & Collections

    [LoggerMessage(EventId = 5950, Level = LogLevel.Information,
        Message = "STAC catalog requested")]
    public static partial void CatalogRequested(ILogger logger);

    [LoggerMessage(EventId = 5951, Level = LogLevel.Debug,
        Message = "STAC catalog returned with {CollectionCount} child collections")]
    public static partial void CatalogReturned(ILogger logger, int collectionCount);

    [LoggerMessage(EventId = 5952, Level = LogLevel.Information,
        Message = "STAC collections list requested")]
    public static partial void CollectionsRequested(ILogger logger);

    [LoggerMessage(EventId = 5953, Level = LogLevel.Debug,
        Message = "STAC collections returned: {Count}")]
    public static partial void CollectionsReturned(ILogger logger, int count);

    [LoggerMessage(EventId = 5954, Level = LogLevel.Information,
        Message = "STAC collection {CollectionId} requested")]
    public static partial void CollectionRequested(ILogger logger, string collectionId);

    [LoggerMessage(EventId = 5955, Level = LogLevel.Warning,
        Message = "STAC collection {CollectionId} not found")]
    public static partial void CollectionNotFound(ILogger logger, string collectionId);

    // 5960–5963: Items

    [LoggerMessage(EventId = 5960, Level = LogLevel.Information,
        Message = "STAC items requested for collection {CollectionId} (limit={Limit})")]
    public static partial void ItemsRequested(ILogger logger, string collectionId, int? limit);

    [LoggerMessage(EventId = 5961, Level = LogLevel.Debug,
        Message = "STAC items returned: {Count} for collection {CollectionId}")]
    public static partial void ItemsReturned(ILogger logger, int count, string collectionId);

    [LoggerMessage(EventId = 5962, Level = LogLevel.Information,
        Message = "STAC item {ItemId} requested from collection {CollectionId}")]
    public static partial void ItemRequested(ILogger logger, string collectionId, string itemId);

    [LoggerMessage(EventId = 5963, Level = LogLevel.Warning,
        Message = "STAC item {ItemId} not found in collection {CollectionId}")]
    public static partial void ItemNotFound(ILogger logger, string collectionId, string itemId);

    // 5970–5971: Search

    [LoggerMessage(EventId = 5970, Level = LogLevel.Information,
        Message = "STAC search requested (collections={CollectionCount}, limit={Limit})")]
    public static partial void SearchRequested(ILogger logger, int collectionCount, int? limit);

    [LoggerMessage(EventId = 5971, Level = LogLevel.Debug,
        Message = "STAC search returned {Count} items")]
    public static partial void SearchReturned(ILogger logger, int count);

    [LoggerMessage(EventId = 5972, Level = LogLevel.Warning,
        Message = "STAC search skipped publication for layer {LayerId} after a storage query error (e.g. the request bbox could not be transformed into the publication's CRS)")]
    public static partial void SearchPublicationSkipped(ILogger logger, int layerId, Exception exception);

    // 5980: Errors

    [LoggerMessage(EventId = 5980, Level = LogLevel.Error,
        Message = "STAC operation failed")]
    public static partial void OperationFailed(ILogger logger, Exception exception);
}
