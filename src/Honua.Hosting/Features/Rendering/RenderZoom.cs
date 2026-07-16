// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Shared.Models;

namespace Honua.Infrastructure.Rendering;

/// <summary>
/// The MapLibre zoom level a render is evaluated at, used to apply style layer
/// <c>minzoom</c>/<c>maxzoom</c> gates.
/// </summary>
/// <remarks>
/// <para>A render either carries a derived zoom (<see cref="At"/>) or records why one could not be
/// derived (<see cref="NotDerivable"/>). This is a reference type with no public constructor and no
/// meaningful default so that "no zoom" cannot be reached by omitting an argument: every render path
/// has to state which case it is in, and an ungated render carries a reason callers can record.</para>
/// <para>Zoom follows MapLibre GL JS, where the mercator world spans <c>512 * 2^zoom</c> pixels
/// (<c>Transform.tileSize</c> is a constant 512). The zoom for an image is therefore the camera zoom
/// at which that image would be displayed 1:1. Deriving it from the rendered envelope and pixel size
/// — rather than from a tile matrix level or a client's zoom convention — is what keeps protocols
/// consistent: the same envelope at the same pixel size gates identically whether it arrives as a WMS
/// <c>GetMap</c>, a raster tile, a static map, or a map export.</para>
/// </remarks>
internal sealed class RenderZoom
{
    private const double MapLibreWorldTileSize = 512.0;
    private const double MercatorWorldSpanMeters = SpatialConstants.WebMercatorExtent * 2.0;
    private const int WebMercatorSrid = 3857;

    private RenderZoom(double? level, string? notDerivableReason)
    {
        Level = level;
        NotDerivableReason = notDerivableReason;
    }

    /// <summary>
    /// The MapLibre zoom level, or <see langword="null"/> when no zoom could be derived and
    /// <c>minzoom</c>/<c>maxzoom</c> gates consequently do not apply.
    /// </summary>
    public double? Level { get; }

    /// <summary>
    /// Why no zoom is available, or <see langword="null"/> when <see cref="Level"/> has a value.
    /// </summary>
    public string? NotDerivableReason { get; }

    /// <summary>
    /// A render at a known MapLibre zoom level.
    /// </summary>
    public static RenderZoom At(double level) => new(level, null);

    /// <summary>
    /// A render with no derivable zoom. The <paramref name="reason"/> is retained so that an
    /// ungated render is traceable rather than silent.
    /// </summary>
    public static RenderZoom NotDerivable(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        return new RenderZoom(null, reason);
    }

    /// <summary>
    /// Derives the MapLibre zoom for an extent already expressed in Web Mercator (EPSG:3857) metres
    /// and rendered at the supplied pixel size.
    /// </summary>
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
        if (!(spanX > 0) || !(spanY > 0))
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

    /// <summary>
    /// Derives the MapLibre zoom for an extent in <paramref name="srid"/> using in-process geodesy
    /// only. Returns a <see cref="NotDerivable"/> zoom when the CRS has no in-process Web Mercator
    /// mapping; callers that can reach the shared coordinate transform services should prefer
    /// <see cref="RasterMapRenderingPipeline.DeriveRenderZoomAsync"/>, which resolves those CRSs too.
    /// </summary>
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

    /// <summary>
    /// Derives the MapLibre zoom for a shared render extent in <paramref name="srid"/>.
    /// </summary>
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
