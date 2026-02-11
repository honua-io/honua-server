// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Microsoft.Extensions.Logging;

namespace Honua.Server.Features.MapServer;

/// <summary>
/// Source-generated log messages for MapServer operations.
/// EventId range: 5400-5499 (MapServer)
/// </summary>
internal static partial class MapServerLog
{
    /// <summary>
    /// Logs when an export request is received.
    /// </summary>
    [LoggerMessage(
        EventId = 5401,
        Level = LogLevel.Information,
        Message = "MapServer export requested: {ServiceId} with size {Width}x{Height}")]
    public static partial void ExportRequested(ILogger logger, string serviceId, int width, int height);

    /// <summary>
    /// Logs when an export completes successfully.
    /// </summary>
    [LoggerMessage(
        EventId = 5402,
        Level = LogLevel.Information,
        Message = "MapServer export completed: {ServiceId} rendered {FeatureCount} features in {ElapsedMs}ms")]
    public static partial void ExportCompleted(ILogger logger, string serviceId, int featureCount, double elapsedMs);

    /// <summary>
    /// Logs when an export fails.
    /// </summary>
    [LoggerMessage(
        EventId = 5403,
        Level = LogLevel.Error,
        Message = "MapServer export failed: {ServiceId}: {ErrorMessage}")]
    public static partial void ExportFailed(ILogger logger, string serviceId, string errorMessage, Exception? exception = null);

    /// <summary>
    /// Logs when an identify request is received.
    /// </summary>
    [LoggerMessage(
        EventId = 5411,
        Level = LogLevel.Information,
        Message = "MapServer identify requested: {ServiceId} at ({X},{Y})")]
    public static partial void IdentifyRequested(ILogger logger, string serviceId, string x, string y);

    /// <summary>
    /// Logs when an identify completes.
    /// </summary>
    [LoggerMessage(
        EventId = 5412,
        Level = LogLevel.Information,
        Message = "MapServer identify completed: {ServiceId} found {ResultCount} results")]
    public static partial void IdentifyCompleted(ILogger logger, string serviceId, int resultCount);

    /// <summary>
    /// Logs when an identify fails.
    /// </summary>
    [LoggerMessage(
        EventId = 5413,
        Level = LogLevel.Error,
        Message = "MapServer identify failed: {ServiceId}: {ErrorMessage}")]
    public static partial void IdentifyFailed(ILogger logger, string serviceId, string errorMessage, Exception? exception = null);

    /// <summary>
    /// Logs when a legend request is received.
    /// </summary>
    [LoggerMessage(
        EventId = 5421,
        Level = LogLevel.Information,
        Message = "MapServer legend requested: {ServiceId}")]
    public static partial void LegendRequested(ILogger logger, string serviceId);

    /// <summary>
    /// Logs when a legend request completes.
    /// </summary>
    [LoggerMessage(
        EventId = 5422,
        Level = LogLevel.Information,
        Message = "MapServer legend completed: {ServiceId} with {LayerCount} layers")]
    public static partial void LegendCompleted(ILogger logger, string serviceId, int layerCount);

    /// <summary>
    /// Logs when a legend request fails.
    /// </summary>
    [LoggerMessage(
        EventId = 5423,
        Level = LogLevel.Error,
        Message = "MapServer legend failed: {ServiceId}: {ErrorMessage}")]
    public static partial void LegendFailed(ILogger logger, string serviceId, string errorMessage, Exception? exception = null);

    /// <summary>
    /// Logs when a find request is received.
    /// </summary>
    [LoggerMessage(
        EventId = 5430,
        Level = LogLevel.Information,
        Message = "MapServer find requested: {ServiceId} searchText={SearchText}")]
    public static partial void FindRequested(ILogger logger, string serviceId, string searchText);

    /// <summary>
    /// Logs when a find request completes.
    /// </summary>
    [LoggerMessage(
        EventId = 5431,
        Level = LogLevel.Information,
        Message = "MapServer find completed: {ServiceId} found {ResultCount} results")]
    public static partial void FindCompleted(ILogger logger, string serviceId, int resultCount);

    /// <summary>
    /// Logs when a find request fails.
    /// </summary>
    [LoggerMessage(
        EventId = 5432,
        Level = LogLevel.Error,
        Message = "MapServer find failed: {ServiceId}: {ErrorMessage}")]
    public static partial void FindFailed(ILogger logger, string serviceId, string errorMessage, Exception? exception = null);
}
