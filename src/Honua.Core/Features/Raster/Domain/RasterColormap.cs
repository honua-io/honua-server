// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Raster.Domain;

/// <summary>
/// A single colormap stop mapping a pixel value to an RGBA colour.
/// </summary>
/// <param name="Value">The (post-stretch) pixel value this colour anchors.</param>
/// <param name="Red">Red channel, 0–255.</param>
/// <param name="Green">Green channel, 0–255.</param>
/// <param name="Blue">Blue channel, 0–255.</param>
/// <param name="Alpha">Alpha channel, 0–255 (255 = opaque).</param>
public readonly record struct RasterColormapEntry(double Value, byte Red, byte Green, byte Blue, byte Alpha);

/// <summary>
/// A pseudocolour colormap applied to a single-band raster to produce an RGBA image,
/// mirroring the Esri ImageServer <c>Colormap</c> raster function. Applied after any
/// <see cref="RasterStretch"/> in the same rendering rule.
/// </summary>
public sealed class RasterColormap
{
    /// <summary>
    /// Colour stops, each mapping a pixel value to an RGBA colour. Values between
    /// stops are interpolated by the raster store.
    /// </summary>
    public required IReadOnlyList<RasterColormapEntry> Entries { get; init; }
}
