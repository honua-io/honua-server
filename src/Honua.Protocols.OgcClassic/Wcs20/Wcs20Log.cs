// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Features.Protocols.Ogc.Classic.Wcs20;

internal static partial class Wcs20Log
{
    [LoggerMessage(EventId = 20001, Level = LogLevel.Information, Message = "WCS {Operation} request received for {RouteScope}")]
    internal static partial void RequestReceived(ILogger logger, string operation, string routeScope);

    [LoggerMessage(EventId = 20002, Level = LogLevel.Information, Message = "WCS {Operation} request completed for {RouteScope}")]
    internal static partial void RequestCompleted(ILogger logger, string operation, string routeScope);

    [LoggerMessage(EventId = 20003, Level = LogLevel.Warning, Message = "WCS validation failed for {Operation}: {Detail}")]
    internal static partial void ValidationFailed(ILogger logger, string operation, string detail);

    [LoggerMessage(EventId = 20004, Level = LogLevel.Warning, Message = "WCS coverage {CoverageId} was not found or is not accessible")]
    internal static partial void CoverageNotFound(ILogger logger, string coverageId);

    [LoggerMessage(EventId = 20005, Level = LogLevel.Error, Message = "WCS {Operation} request failed for {RouteScope}")]
    internal static partial void RequestFailed(ILogger logger, Exception exception, string operation, string routeScope);

    [LoggerMessage(EventId = 20006, Level = LogLevel.Information, Message = "WCS GetCoverage returned {ByteCount} bytes for coverage {CoverageId} as {ContentType}")]
    internal static partial void CoverageReturned(ILogger logger, string coverageId, int byteCount, string contentType);
}
