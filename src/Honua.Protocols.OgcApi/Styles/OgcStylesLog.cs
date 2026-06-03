// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Microsoft.Extensions.Logging;

namespace Honua.Protocols.Ogc.Api.Styles;

/// <summary>
/// Structured logging for OGC API - Styles operations.
/// Event ID range: 5950-5999 (avoiding collisions with other features).
/// </summary>
internal static partial class OgcStylesLog
{
    [LoggerMessage(
        EventId = 5950,
        Level = LogLevel.Information,
        Message = "OGC Styles conformance requested")]
    public static partial void ConformanceRequested(ILogger logger);

    [LoggerMessage(
        EventId = 5951,
        Level = LogLevel.Debug,
        Message = "OGC Styles list returned {Count} styles")]
    public static partial void StylesListed(ILogger logger, int count);

    [LoggerMessage(
        EventId = 5952,
        Level = LogLevel.Debug,
        Message = "OGC Style '{StyleId}' not found")]
    public static partial void StyleNotFound(ILogger logger, string styleId);

    [LoggerMessage(
        EventId = 5953,
        Level = LogLevel.Information,
        Message = "OGC Style '{StyleId}' updated")]
    public static partial void StyleUpdated(ILogger logger, string styleId);

    [LoggerMessage(
        EventId = 5954,
        Level = LogLevel.Warning,
        Message = "OGC Style '{StyleId}' update rejected: {Reason}")]
    public static partial void StyleUpdateRejected(ILogger logger, string styleId, string reason);

    [LoggerMessage(
        EventId = 5955,
        Level = LogLevel.Information,
        Message = "OGC Style '{StyleId}' created")]
    public static partial void StyleCreated(ILogger logger, string styleId);

    [LoggerMessage(
        EventId = 5956,
        Level = LogLevel.Warning,
        Message = "OGC Style create rejected: {Reason}")]
    public static partial void StyleCreateRejected(ILogger logger, string reason);

    [LoggerMessage(
        EventId = 5957,
        Level = LogLevel.Information,
        Message = "OGC Style '{StyleId}' deleted")]
    public static partial void StyleDeleted(ILogger logger, string styleId);
}
