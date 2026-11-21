// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Tiles;

// Shared by OGC API Tiles, WMTS and TileJSON discovery so every surface advertises the same bounds.
public static class TileExtent
{
    public static (double MinLon, double MinLat, double MaxLon, double MaxLat) ExpandDegenerateAxes(
        (double MinLon, double MinLat, double MaxLon, double MaxLat) extent, int maxZoom)
    {
        // An epsilon can still round to zero pixels; use a finest-zoom tile span.
        var tileSpan = Math.ScaleB(360.0, -Math.Clamp(maxZoom, 0, TileMath.MaxSupportedZoomLevel));
        var longitude = extent.MinLon > extent.MaxLon
            ? (Min: extent.MinLon, Max: extent.MaxLon)
            : ExpandAxis((extent.MinLon, extent.MaxLon), tileSpan, 180);
        var latitude = ExpandAxis((Math.Min(extent.MinLat, extent.MaxLat), Math.Max(extent.MinLat, extent.MaxLat)), tileSpan, 90);
        return (longitude.Min, latitude.Min, longitude.Max, latitude.Max);
    }

    // Any axis narrower than one finest-zoom tile (a single point, a reprojected point padded
    // by the extent margin, or a line whose stored float bbox is a few millionths wide) becomes
    // one tile centred on it, so a low Tiles:MaxTileZoom widens small layers to its tile size.
    // A dateline-crossing longitude (min > max) keeps its edges, as the OGC extent reprojection
    // does; surfaces that cannot express one advertise the full longitude range instead.
    private static (double Min, double Max) ExpandAxis((double Min, double Max) axis, double span, double limit)
    {
        var width = Math.Min(span, 2 * limit);
        if (axis.Max - axis.Min >= width)
        {
            return axis;
        }
        var lower = Math.Clamp((axis.Min + axis.Max - width) / 2, -limit, limit - width);
        return (lower, lower + width);
    }
}
