// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Shared.Models;

namespace Honua.Infrastructure.Rendering;

// Zoom comes from the envelope and pixel size, not from a tile-matrix level, so the same picture gates the same way on every protocol.
internal sealed class RenderZoom
{
    private const double MapLibreWorldTileSize = 512.0;
    private const double MercatorWorldSpanMeters = SpatialConstants.WebMercatorExtent * 2.0;
    private const int WebMercatorSrid = 3857;

    private const int ReferencePixelSpan = 256;

    private RenderZoom(double? level, string? notDerivableReason)
    {
        Level = level;
        NotDerivableReason = notDerivableReason;
    }

    // With no derived zoom, minzoom and maxzoom gates do not apply.
    public double? Level { get; }

    public string? NotDerivableReason { get; }

    public static RenderZoom At(double level) => new(level, null);

    public static RenderZoom NotDerivable(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        return new RenderZoom(null, reason);
    }

    // A scale denominator goes through the same derivation as a map; a separate 256-pixel constant would offset every gate by one zoom level.
    public static RenderZoom FromScaleDenominator(double scaleDenominator)
    {
        if (scaleDenominator <= 0 || !double.IsFinite(scaleDenominator))
        {
            return NotDerivable("the scale denominator is not a positive, finite number");
        }

        var extentSpanMeters = ReferencePixelSpan * scaleDenominator * SpatialConstants.PixelSizeMeters;

        // Anchored at the origin so MinX < MaxX and the extent is never read as a
        // dateline-wrapped envelope; only the span feeds the derivation.
        return FromWebMercatorExtent(
            new SkiaMapRenderer.RenderExtent(0, 0, extentSpanMeters, extentSpanMeters),
            ReferencePixelSpan,
            ReferencePixelSpan);
    }

    public static RenderZoom FromWebMercatorExtent(
        SkiaMapRenderer.RenderExtent extent,
        int imageWidth,
        int imageHeight)
    {
        if (imageWidth <= 0 || imageHeight <= 0)
        {
            return NotDerivable("the render has no positive pixel dimensions");
        }

        var spanX = CoordinateTransformer.GetEffectiveWidth(extent) / MercatorWorldSpanMeters;
        var spanY = extent.Height / MercatorWorldSpanMeters;
        if (spanX <= 0 || double.IsNaN(spanX) ||
            spanY <= 0 || double.IsNaN(spanY))
        {
            return NotDerivable("the render extent is empty or degenerate");
        }

        // Mirrors MapLibre's cameraForBoxAndBearing, which fits the more constrained axis
        // (camera_helper.ts: `scaleZoom(tr.scale * Math.min(scaleX, scaleY))`). For a well-formed
        // request whose envelope aspect matches the image aspect both axes agree.
        var level = Math.Min(
            Math.Log2(imageWidth / (MapLibreWorldTileSize * spanX)),
            Math.Log2(imageHeight / (MapLibreWorldTileSize * spanY)));

        return double.IsFinite(level)
            ? At(level)
            : NotDerivable("the render extent is empty or degenerate");
    }

    public static RenderZoom FromExtent(
        SkiaMapRenderer.RenderExtent extent,
        int imageWidth,
        int imageHeight,
        int srid)
    {
        if (SpatialReferenceExtensions.IsWebMercatorSrid(srid))
        {
            return FromWebMercatorExtent(extent, imageWidth, imageHeight);
        }

        try
        {
            return FromWebMercatorExtent(
                CoordinateTransformer.TransformExtent(extent, srid, WebMercatorSrid),
                imageWidth,
                imageHeight);
        }
        catch (NotSupportedException)
        {
            return NotDerivable(
                $"EPSG:{srid.ToString(System.Globalization.CultureInfo.InvariantCulture)} has no in-process Web Mercator transform");
        }
    }

    public static RenderZoom FromExtent(
        RenderExtent extent,
        int imageWidth,
        int imageHeight,
        int srid)
        => FromExtent(
            new SkiaMapRenderer.RenderExtent(extent.MinX, extent.MinY, extent.MaxX, extent.MaxY),
            imageWidth,
            imageHeight,
            srid);
}
