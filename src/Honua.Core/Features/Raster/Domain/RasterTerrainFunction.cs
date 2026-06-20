// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Raster.Domain;

/// <summary>
/// Inline terrain (focal/neighbourhood) raster function selected by a rendering rule.
/// Distinct from the per-pixel <see cref="RasterBandArithmetic"/> band-math case: terrain
/// functions read a 3x3 neighbourhood, so they compose as a non-persisting
/// <c>ST_HillShade</c>/<c>ST_Slope</c>/<c>ST_Aspect</c> step over the source band rather
/// than an <c>ST_MapAlgebra</c> per-pixel expression. The method is a closed enum (not a
/// free-form expression), so the terrain boundary never accepts caller-supplied SQL text.
/// </summary>
public enum RasterTerrainMethod
{
    /// <summary>
    /// Shaded relief from a single elevation band via <c>ST_HillShade</c>, using the
    /// configured sun azimuth/altitude and z-factor.
    /// </summary>
    Hillshade = 0,

    /// <summary>
    /// Per-cell slope from a single elevation band via <c>ST_Slope</c> in degrees.
    /// </summary>
    Slope = 1,

    /// <summary>
    /// Per-cell aspect (downslope compass direction) from a single elevation band via
    /// <c>ST_Aspect</c> in degrees.
    /// </summary>
    Aspect = 2,
}

/// <summary>
/// Specification for an inline terrain raster function carried on a <see cref="RasterQuery"/>
/// (or <see cref="RasterIdentifyRendering"/>). When present, the raster store derives a single
/// analytic band from one elevation source band using a vetted, hardcoded PostGIS surface
/// function selected by <see cref="Method"/>. This type carries no free-form expression:
/// <see cref="Method"/> is the injection boundary; the numeric parameters are bound as SQL
/// command parameters by the store.
/// </summary>
public readonly record struct RasterTerrainFunction
{
    /// <summary>
    /// Default sun azimuth (degrees clockwise from north) for hillshade, matching the
    /// ArcGIS default of 315 (north-west illumination).
    /// </summary>
    public const double DefaultAzimuthDegrees = 315.0;

    /// <summary>
    /// Default sun altitude (degrees above the horizon) for hillshade, matching the
    /// ArcGIS default of 45.
    /// </summary>
    public const double DefaultAltitudeDegrees = 45.0;

    /// <summary>
    /// Default vertical exaggeration factor.
    /// </summary>
    public const double DefaultZFactor = 1.0;

    /// <summary>
    /// Vetted surface function applied to the elevation band.
    /// </summary>
    public required RasterTerrainMethod Method { get; init; }

    /// <summary>
    /// 1-based elevation source band the terrain function reads. Defaults to band 1.
    /// </summary>
    public int Band { get; init; } = 1;

    /// <summary>
    /// Sun azimuth in degrees clockwise from north (hillshade only). Range 0..360.
    /// </summary>
    public double AzimuthDegrees { get; init; } = DefaultAzimuthDegrees;

    /// <summary>
    /// Sun altitude in degrees above the horizon (hillshade only). Range 0..90.
    /// </summary>
    public double AltitudeDegrees { get; init; } = DefaultAltitudeDegrees;

    /// <summary>
    /// Vertical exaggeration factor (hillshade/slope). Must be finite and positive.
    /// </summary>
    public double ZFactor { get; init; } = DefaultZFactor;

    /// <summary>
    /// Initializes a new instance of the <see cref="RasterTerrainFunction"/> struct.
    /// </summary>
    public RasterTerrainFunction()
    {
    }
}
