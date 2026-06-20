// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Raster.Domain;

/// <summary>
/// Optional rendering rule applied to an <c>identify</c> operation so the returned pixel
/// value reflects the rendered output (post-band-math / post-stretch / post-colormap) rather
/// than the raw source value. Mirrors the Esri ImageServer <c>identify</c> behaviour when a
/// <c>renderingRule</c> is supplied: the same <see cref="RasterBandArithmetic"/>,
/// <see cref="RasterTerrainFunction"/>, <see cref="RasterStretch"/>, and
/// <see cref="RasterColormap"/> the export path executes are applied at the sampled pixel.
/// </summary>
/// <param name="Stretch">
/// Optional display stretch applied to the sampled band value(s). When <c>null</c> no
/// stretch is applied.
/// </param>
/// <param name="Colormap">
/// Optional pseudocolour colormap applied (after any <see cref="Stretch"/>) to a single-band
/// value, yielding RGBA channels. When <c>null</c> no colormap is applied.
/// </param>
/// <param name="Clip">
/// Optional clip region. A point that falls in the masked-out portion of the clip
/// (outside an inclusive clip, or inside an inverted "keep outside" clip) resolves to NoData.
/// When <c>null</c> no clip masking is applied.
/// </param>
/// <param name="BandArithmetic">
/// Optional two-band arithmetic (e.g. NDVI) applied before the stretch so the sampled value
/// is the analytic-band value, matching exportImage. When <c>null</c> no band math is applied.
/// </param>
/// <param name="Terrain">
/// Optional inline terrain (hillshade/slope/aspect) function applied to the elevation band so
/// the sampled value matches the rendered terrain output. When <c>null</c> none is applied.
/// </param>
public readonly record struct RasterIdentifyRendering(
    RasterStretch? Stretch,
    RasterColormap? Colormap,
    RasterClipRegion? Clip,
    RasterBandArithmetic? BandArithmetic = null,
    RasterTerrainFunction? Terrain = null)
{
    /// <summary>
    /// <c>true</c> when at least one rendering transformation is present and the
    /// rendering rule will change the returned value relative to the raw pixel.
    /// </summary>
    public bool HasRendering =>
        Stretch is not null
        || Colormap is not null
        || Clip is not null
        || BandArithmetic is not null
        || Terrain is not null;
}
