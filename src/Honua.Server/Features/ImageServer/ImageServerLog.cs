// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Microsoft.Extensions.Logging;

namespace Honua.Server.Features.ImageServer;

/// <summary>
/// Structured logging for Image Server operations.
/// Event ID range: 5800-5899 (avoiding collisions with other features).
/// </summary>
internal static partial class ImageServerLog
{
    [LoggerMessage(
        EventId = 5800,
        Level = LogLevel.Debug,
        Message = "Layer {LayerId} not found")]
    public static partial void LayerNotFound(ILogger logger, int layerId);

    [LoggerMessage(
        EventId = 5801,
        Level = LogLevel.Information,
        Message = "No rasters found for layer {LayerId}")]
    public static partial void NoRastersFound(ILogger logger, int layerId);

    [LoggerMessage(
        EventId = 5802,
        Level = LogLevel.Warning,
        Message = "Extent not available for layer {LayerId}")]
    public static partial void ExtentNotAvailable(ILogger logger, int layerId);

    [LoggerMessage(
        EventId = 5803,
        Level = LogLevel.Information,
        Message = "Generated service info for layer {LayerId}: {BandCount} bands, {StatisticsCount} statistics")]
    public static partial void ServiceInfoGenerated(ILogger logger, int layerId, int bandCount, int statisticsCount);

    [LoggerMessage(
        EventId = 5804,
        Level = LogLevel.Error,
        Message = "Failed to get service info for layer {LayerId}")]
    public static partial void ServiceInfoFailed(ILogger logger, Exception ex, int layerId);

    [LoggerMessage(
        EventId = 5805,
        Level = LogLevel.Information,
        Message = "Exporting image for layer {LayerId}: {Width}x{Height}, format={Format}")]
    public static partial void ExportImageStarted(ILogger logger, int layerId, int width, int height, string format);

    [LoggerMessage(
        EventId = 5806,
        Level = LogLevel.Information,
        Message = "Successfully exported image for layer {LayerId}: {DataSize} bytes")]
    public static partial void ExportImageCompleted(ILogger logger, int layerId, int dataSize);

    [LoggerMessage(
        EventId = 5807,
        Level = LogLevel.Error,
        Message = "Failed to export image for layer {LayerId}")]
    public static partial void ExportImageFailed(ILogger logger, Exception ex, int layerId);

    [LoggerMessage(
        EventId = 5808,
        Level = LogLevel.Information,
        Message = "Identifying pixel values for layer {LayerId} at ({X}, {Y})")]
    public static partial void IdentifyStarted(ILogger logger, int layerId, double x, double y);

    [LoggerMessage(
        EventId = 5809,
        Level = LogLevel.Information,
        Message = "Pixel identification for layer {LayerId}: HasData={HasData}, BandCount={BandCount}")]
    public static partial void IdentifyCompleted(ILogger logger, int layerId, bool hasData, int bandCount);

    [LoggerMessage(
        EventId = 5810,
        Level = LogLevel.Error,
        Message = "Failed to identify pixel values for layer {LayerId}")]
    public static partial void IdentifyFailed(ILogger logger, Exception ex, int layerId);

    [LoggerMessage(
        EventId = 5811,
        Level = LogLevel.Information,
        Message = "Getting image tile for layer {LayerId}: level={Level}, row={Row}, col={Col}")]
    public static partial void ImageTileRequested(ILogger logger, int layerId, int level, int row, int col);

    [LoggerMessage(
        EventId = 5812,
        Level = LogLevel.Information,
        Message = "Generated image tile for layer {LayerId}: {DataSize} bytes")]
    public static partial void ImageTileGenerated(ILogger logger, int layerId, int dataSize);

    [LoggerMessage(
        EventId = 5813,
        Level = LogLevel.Debug,
        Message = "Image tile not found for layer {LayerId}: level={Level}, row={Row}, col={Col}")]
    public static partial void ImageTileNotFound(ILogger logger, int layerId, int level, int row, int col);

    [LoggerMessage(
        EventId = 5814,
        Level = LogLevel.Error,
        Message = "Failed to get image tile for layer {LayerId}")]
    public static partial void ImageTileFailed(ILogger logger, Exception ex, int layerId);

    [LoggerMessage(
        EventId = 5815,
        Level = LogLevel.Warning,
        Message = "Invalid export parameters for layer {LayerId}: {ValidationErrors}")]
    public static partial void InvalidExportParameters(ILogger logger, int layerId, string validationErrors);

    [LoggerMessage(
        EventId = 5816,
        Level = LogLevel.Warning,
        Message = "Invalid identify parameters for layer {LayerId}: {ValidationErrors}")]
    public static partial void InvalidIdentifyParameters(ILogger logger, int layerId, string validationErrors);

    [LoggerMessage(
        EventId = 5817,
        Level = LogLevel.Warning,
        Message = "Temporary export storage limit reached for layer {LayerId}: {ErrorMessage}")]
    public static partial void ExportStorageLimitReached(ILogger logger, int layerId, string errorMessage);
}
