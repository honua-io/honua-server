// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Microsoft.Extensions.Logging;

namespace Honua.Server.Features.StaticMap;

/// <summary>
/// Source-generated log messages for StaticMap operations.
/// EventId range: 5700-5749 (StaticMap)
/// </summary>
internal static partial class StaticMapLog
{
    /// <summary>
    /// Logs when a static map request is received.
    /// </summary>
    [LoggerMessage(
        EventId = 5700,
        Level = LogLevel.Information,
        Message = "StaticMap requested: {ServiceId} {Width}x{Height} format={Format} dpi={Dpi}")]
    public static partial void Requested(ILogger logger, string serviceId, int width, int height, string format, int dpi);

    /// <summary>
    /// Logs when a static map request completes successfully.
    /// </summary>
    [LoggerMessage(
        EventId = 5701,
        Level = LogLevel.Information,
        Message = "StaticMap completed: {ServiceId} rendered {FeatureCount} features in {ElapsedMs}ms")]
    public static partial void Completed(ILogger logger, string serviceId, int featureCount, double elapsedMs);

    /// <summary>
    /// Logs when a static map request fails.
    /// </summary>
    [LoggerMessage(
        EventId = 5702,
        Level = LogLevel.Error,
        Message = "StaticMap failed: {ServiceId}: {ErrorMessage}")]
    public static partial void Failed(ILogger logger, string serviceId, string errorMessage, Exception? exception = null);

}
