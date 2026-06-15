// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Protocols.Ogc.Api.Coverages.Services;

/// <summary>
/// Source-generated log messages for Zarr-backed OGC API Coverages serving (#1590).
/// </summary>
internal static partial class ZarrCoverageLog
{
    [LoggerMessage(EventId = 575230, Level = LogLevel.Information, Message = "Zarr coverage served: collection={CollectionId}, variable={Variable}, bytes={ByteCount}")]
    public static partial void CoverageServed(ILogger logger, string collectionId, string variable, int byteCount);

    [LoggerMessage(EventId = 575231, Level = LogLevel.Warning, Message = "Zarr coverage subset rejected: collection={CollectionId}, detail={Detail}")]
    public static partial void SubsetRejected(ILogger logger, string collectionId, string detail);

    [LoggerMessage(EventId = 575232, Level = LogLevel.Error, Message = "Zarr coverage subset read failed: collection={CollectionId}")]
    public static partial void SubsetReadFailed(ILogger logger, Exception exception, string collectionId);

    [LoggerMessage(EventId = 575233, Level = LogLevel.Error, Message = "Zarr coverage range reader missing: collection={CollectionId}, provider={Provider}")]
    public static partial void RangeReaderMissing(ILogger logger, string collectionId, string provider);
}
