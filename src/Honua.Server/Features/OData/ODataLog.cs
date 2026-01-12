// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Features.OData;

/// <summary>
/// Structured logging for OData endpoint requests.
/// </summary>
internal static partial class ODataLog
{
    [LoggerMessage(
        EventId = 3300,
        Level = LogLevel.Information,
        Message = "OData batch requested with {RequestCount} request(s)")]
    public static partial void BatchRequested(ILogger logger, int requestCount);

    [LoggerMessage(
        EventId = 3301,
        Level = LogLevel.Information,
        Message = "OData apply requested for layer {LayerId}")]
    public static partial void ApplyRequested(ILogger logger, int layerId);

    [LoggerMessage(
        EventId = 3302,
        Level = LogLevel.Information,
        Message = "OData search requested for layer {LayerId}")]
    public static partial void SearchRequested(ILogger logger, int layerId);
}
