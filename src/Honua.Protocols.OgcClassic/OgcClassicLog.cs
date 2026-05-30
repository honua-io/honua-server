// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Microsoft.Extensions.Logging;

namespace Honua.Protocols.Ogc.Classic;

/// <summary>
/// Source-generated log messages for classic OGC web service operations.
/// </summary>
internal static partial class OgcClassicLog
{
    [LoggerMessage(
        EventId = 5640,
        Level = LogLevel.Information,
        Message = "OGC WMS {RequestType} requested: {ServiceId}")]
    public static partial void WmsRequested(ILogger logger, string serviceId, string requestType);

    [LoggerMessage(
        EventId = 5641,
        Level = LogLevel.Error,
        Message = "OGC WMS failed: {ServiceId}: {ErrorMessage}")]
    public static partial void WmsFailed(ILogger logger, string serviceId, string errorMessage, Exception? exception = null);

    [LoggerMessage(
        EventId = 5642,
        Level = LogLevel.Information,
        Message = "OGC WMTS {RequestType} requested: {ServiceId}")]
    public static partial void WmtsRequested(ILogger logger, string serviceId, string requestType);

    [LoggerMessage(
        EventId = 5643,
        Level = LogLevel.Error,
        Message = "OGC WMTS failed: {ServiceId}: {ErrorMessage}")]
    public static partial void WmtsFailed(ILogger logger, string serviceId, string errorMessage, Exception? exception = null);
}
