// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Microsoft.Extensions.Logging;

namespace Honua.Infrastructure.Rendering;

internal static partial class RasterMapRenderingPipelineLog
{
    [LoggerMessage(
        EventId = 3470,
        Level = LogLevel.Warning,
        Message = "PostGIS extent transform failed from SRID {FromSrid} to {ToSrid}")]
    public static partial void PostGisExtentTransformFailed(
        ILogger logger,
        int fromSrid,
        int toSrid,
        Exception exception);

    [LoggerMessage(
        EventId = 3471,
        Level = LogLevel.Warning,
        Message = "Failed to resolve style '{StyleId}' for styled vector render; falling back to the layer's bound style")]
    public static partial void StyleResolutionFailed(
        ILogger logger,
        string styleId,
        Exception exception);

    [LoggerMessage(
        EventId = 3472,
        Level = LogLevel.Error,
        Message = "Native map-rendering runtime (SkiaSharp) failed to load while rendering {LayerCount} layer(s) at {Width}x{Height}; the deployed runtime image cannot rasterize maps. Surfacing a capability error.")]
    public static partial void RenderingRuntimeUnavailable(
        ILogger logger,
        int layerCount,
        int width,
        int height,
        Exception exception);
}
