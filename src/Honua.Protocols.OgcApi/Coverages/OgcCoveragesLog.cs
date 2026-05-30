// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Protocols.Ogc.Api.Coverages;

internal static partial class OgcCoveragesLog
{
    [LoggerMessage(EventId = 575210, Level = LogLevel.Debug, Message = "OGC Coverages landing page requested")]
    public static partial void LandingRequested(ILogger logger);

    [LoggerMessage(EventId = 575211, Level = LogLevel.Debug, Message = "OGC Coverages conformance requested")]
    public static partial void ConformanceRequested(ILogger logger);

    [LoggerMessage(EventId = 575212, Level = LogLevel.Debug, Message = "OGC Coverages collections requested")]
    public static partial void CollectionsRequested(ILogger logger);

    [LoggerMessage(EventId = 575219, Level = LogLevel.Debug, Message = "OGC Coverages OpenAPI requested")]
    public static partial void OpenApiRequested(ILogger logger);

    [LoggerMessage(EventId = 575213, Level = LogLevel.Debug, Message = "OGC Coverages collection requested: {CollectionId}")]
    public static partial void CollectionRequested(ILogger logger, string collectionId);

    [LoggerMessage(EventId = 575214, Level = LogLevel.Debug, Message = "OGC Coverages schema requested: {CollectionId}")]
    public static partial void SchemaRequested(ILogger logger, string collectionId);

    [LoggerMessage(EventId = 575215, Level = LogLevel.Information, Message = "OGC Coverages coverage requested: {CollectionId}")]
    public static partial void CoverageRequested(ILogger logger, string collectionId);

    [LoggerMessage(EventId = 575216, Level = LogLevel.Information, Message = "OGC Coverages coverage returned: {CollectionId}, bytes={ByteCount}, contentType={ContentType}")]
    public static partial void CoverageReturned(ILogger logger, string collectionId, int byteCount, string contentType);

    [LoggerMessage(EventId = 575217, Level = LogLevel.Warning, Message = "OGC Coverages validation failed: {CollectionId}, detail={Detail}")]
    public static partial void ValidationFailed(ILogger logger, string collectionId, string detail);

    [LoggerMessage(EventId = 575218, Level = LogLevel.Error, Message = "OGC Coverages request failed: operation={Operation}, collection={CollectionId}")]
    public static partial void RequestFailed(ILogger logger, Exception exception, string operation, string? collectionId);
}
