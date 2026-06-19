// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Raster.Domain;

/// <summary>
/// Vetted two-band arithmetic formula selected by a band-math rendering rule.
/// The method (not a free-form expression) selects a hardcoded, injection-safe
/// PostGIS <c>ST_MapAlgebra</c> formula in the raster store, so the band-math
/// boundary never accepts caller-supplied SQL text.
/// </summary>
public enum RasterBandArithmeticMethod
{
    /// <summary>
    /// Normalized Difference Vegetation Index:
    /// <c>(NIR - VIS) / (NIR + VIS)</c>, clamped to the [-1, 1] range with a
    /// zero-denominator guard. Pairs naturally with a pseudocolour colormap.
    /// </summary>
    Ndvi = 0,
}

/// <summary>
/// Specification for a two-raster band-arithmetic operation carried on a
/// <see cref="RasterQuery"/>. When present, the raster store derives a single
/// analytic band (e.g. NDVI) from two source bands using a vetted, hardcoded
/// <c>ST_MapAlgebra</c> formula selected by <see cref="Method"/>. Band numbers
/// are 1-based. This type carries no free-form expression: <see cref="Method"/>
/// is the injection boundary.
/// </summary>
public readonly record struct RasterBandArithmetic
{
    /// <summary>
    /// 1-based band number of the visible (red) source band, used as the second
    /// operand of the band-arithmetic formula.
    /// </summary>
    public required int VisibleBand { get; init; }

    /// <summary>
    /// 1-based band number of the infrared (NIR) source band, used as the first
    /// operand of the band-arithmetic formula.
    /// </summary>
    public required int InfraredBand { get; init; }

    /// <summary>
    /// Vetted formula applied to the two source bands.
    /// </summary>
    public required RasterBandArithmeticMethod Method { get; init; }

    /// <summary>
    /// Initializes a new instance of the <see cref="RasterBandArithmetic"/> struct.
    /// </summary>
    public RasterBandArithmetic()
    {
    }
}
