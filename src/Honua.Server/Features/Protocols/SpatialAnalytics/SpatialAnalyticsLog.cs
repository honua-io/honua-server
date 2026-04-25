// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Microsoft.Extensions.Logging;

namespace Honua.Server.Features.Protocols.SpatialAnalytics;

/// <summary>
/// Source-generated log messages for the spatial analytics endpoints.
/// EventId range: 8200-8249 (SpatialAnalytics).
/// </summary>
internal static partial class SpatialAnalyticsLog
{
    [LoggerMessage(
        EventId = 8200,
        Level = LogLevel.Debug,
        Message = "Spatial analytics request: op={Operation} layerId={LayerId}")]
    public static partial void RequestReceived(ILogger logger, string operation, int layerId);

    [LoggerMessage(
        EventId = 8201,
        Level = LogLevel.Information,
        Message = "Spatial analytics completed: op={Operation} layerId={LayerId} rows={RowCount} duration={ElapsedMs}ms")]
    public static partial void RequestCompleted(ILogger logger, string operation, int layerId, int rowCount, double elapsedMs);

    [LoggerMessage(
        EventId = 8202,
        Level = LogLevel.Warning,
        Message = "Spatial analytics blocked by edition gate: op={Operation} currentEdition={Edition}")]
    public static partial void EditionGateBlocked(ILogger logger, string operation, string edition);

    [LoggerMessage(
        EventId = 8203,
        Level = LogLevel.Warning,
        Message = "Spatial analytics input truncated: op={Operation} layerId={LayerId} maxInputFeatures={MaxInputFeatures}")]
    public static partial void InputTruncated(ILogger logger, string operation, int layerId, int maxInputFeatures);

    [LoggerMessage(
        EventId = 8204,
        Level = LogLevel.Warning,
        Message = "Spatial analytics result truncated: op={Operation} layerId={LayerId} maxOutputRows={MaxOutputRows}")]
    public static partial void ResultTruncated(ILogger logger, string operation, int layerId, int maxOutputRows);

    [LoggerMessage(
        EventId = 8205,
        Level = LogLevel.Warning,
        Message = "Spatial analytics invalid parameter: op={Operation} parameter={Parameter} reason={Reason}")]
    public static partial void InvalidParameter(ILogger logger, string operation, string parameter, string reason);

    [LoggerMessage(
        EventId = 8206,
        Level = LogLevel.Error,
        Message = "Spatial analytics failed: op={Operation} layerId={LayerId}: {ErrorMessage}")]
    public static partial void RequestFailed(ILogger logger, string operation, int layerId, string errorMessage, Exception? exception = null);

    [LoggerMessage(
        EventId = 8207,
        Level = LogLevel.Warning,
        Message = "Spatial analytics reader not registered: op={Operation}; the active feature-store provider does not ship a spatial analytics backend")]
    public static partial void ReaderUnavailable(ILogger logger, string operation);
}
