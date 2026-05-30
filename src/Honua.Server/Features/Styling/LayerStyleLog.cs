// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Microsoft.Extensions.Logging;

namespace Honua.Server.Features.Styling;

/// <summary>
/// Source-generated logging for style operations.
/// </summary>
internal static partial class LayerStyleLog
{
    [LoggerMessage(
        EventId = 6400,
        Level = LogLevel.Warning,
        Message = "Unsupported GeoServices renderer type '{RendererType}' for layer {LayerId}. Falling back to default MapLibre style.")]
    public static partial void UnsupportedRendererType(ILogger logger, string rendererType, int layerId);

    [LoggerMessage(
        EventId = 6401,
        Level = LogLevel.Warning,
        Message = "Failed to resolve optional drawingInfo for layer {LayerId}. Returning metadata without drawingInfo.")]
    public static partial void OptionalDrawingInfoUnavailable(ILogger logger, int layerId, Exception exception);

    [LoggerMessage(
        EventId = 6402,
        Level = LogLevel.Debug,
        Message = "Applied {Theme} theme transform for layer {LayerId}.")]
    public static partial void ThemeApplied(ILogger logger, string theme, int layerId);

    [LoggerMessage(
        EventId = 6403,
        Level = LogLevel.Debug,
        Message = "Theme transform for layer {LayerId} skipped property '{Property}' due to malformed color '{Color}'.")]
    public static partial void ThemeColorParseFailure(ILogger logger, int layerId, string property, string color);
}
