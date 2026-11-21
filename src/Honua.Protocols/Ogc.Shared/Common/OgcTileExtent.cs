// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Tiles;

namespace Honua.Protocols.Ogc.Common;

internal static class OgcTileExtent
{
    public static (double MinLon, double MinLat, double MaxLon, double MaxLat) ExpandDegenerateAxes(
        (double MinLon, double MinLat, double MaxLon, double MaxLat) extent, int maxZoom)
    {
        // An epsilon can still round to zero pixels; use a finest-zoom tile span.
        var tileSpan = Math.ScaleB(360.0, -Math.Clamp(maxZoom, 0, TileMath.MaxSupportedZoomLevel));
        var longitude = ExpandAxis((extent.MinLon, extent.MaxLon), tileSpan, 180);
        var latitude = ExpandAxis((extent.MinLat, extent.MaxLat), tileSpan, 90);
        return (longitude.Min, latitude.Min, longitude.Max, latitude.Max);
    }

    // Any axis narrower than one finest-zoom tile (a single point, a reprojected point padded
    // by the extent margin, or a line whose stored float bbox is a few millionths wide) becomes
    // one tile centred on it. A dateline-crossing longitude (min > max) keeps its edges, as
    // OgcExtentTransformer.ExpandReprojectedCrs84 does.
    private static (double Min, double Max) ExpandAxis((double Min, double Max) axis, double span, double limit)
    {
        var width = Math.Min(span, 2 * limit);
        if (axis.Max < axis.Min || axis.Max - axis.Min >= width)
        {
            return axis;
        }
        var lower = Math.Clamp((axis.Min + axis.Max - width) / 2, -limit, limit - width);
        return (lower, lower + width);
    }
}
