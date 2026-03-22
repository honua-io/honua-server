// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Features.Export;

/// <summary>
/// Source-generated log messages for the data export feature.
/// Event IDs: 7750-7759.
/// </summary>
internal static partial class ExportLog
{
    [LoggerMessage(7750, LogLevel.Information,
        "Export requested: service={ServiceName} layer={LayerId} format={Format} features={FeatureCount}")]
    public static partial void ExportRequested(
        ILogger logger, string serviceName, int layerId, string format, long featureCount);

    [LoggerMessage(7751, LogLevel.Information,
        "Export completed: service={ServiceName} layer={LayerId} format={Format} features={FeatureCount} duration={DurationMs:F2}ms")]
    public static partial void ExportCompleted(
        ILogger logger, string serviceName, int layerId, string format, long featureCount, double durationMs);

    [LoggerMessage(7752, LogLevel.Error,
        "Export failed: service={ServiceName} layer={LayerId} format={Format}")]
    public static partial void ExportFailed(
        ILogger logger, string serviceName, int layerId, string format, Exception exception);

    [LoggerMessage(7753, LogLevel.Information,
        "Async export queued: jobId={JobId} service={ServiceName} layer={LayerId} format={Format} features={FeatureCount}")]
    public static partial void AsyncExportQueued(
        ILogger logger, string jobId, string serviceName, int layerId, string format, long featureCount);

    [LoggerMessage(7754, LogLevel.Warning,
        "Export format validation failed: format={Format}")]
    public static partial void FormatValidationFailed(ILogger logger, string format);

    [LoggerMessage(7755, LogLevel.Information,
        "Async export completed: jobId={JobId} features={FeatureCount} size={SizeBytes} bytes")]
    public static partial void AsyncExportCompleted(
        ILogger logger, string jobId, long featureCount, long sizeBytes);

    [LoggerMessage(7756, LogLevel.Error,
        "Async export failed: jobId={JobId}")]
    public static partial void AsyncExportFailed(ILogger logger, string jobId, Exception exception);
}

/// <summary>
/// Marker class for DI logger resolution in export endpoints.
/// </summary>
internal sealed class ExportEndpointsLog;
