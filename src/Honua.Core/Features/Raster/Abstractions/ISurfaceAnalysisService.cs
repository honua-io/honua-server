// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Raster.Domain;

namespace Honua.Core.Features.Raster.Abstractions;

/// <summary>
/// Canonical surface-analysis operations for the geoprocessing runtime. Each
/// method reads a DEM from the supplied raster store and writes a derived
/// raster (slope, aspect, hillshade, or terrain ruggedness) into the caller's
/// output layer.
/// </summary>
/// <remarks>
/// Implementations issue PostGIS raster expressions (ST_Slope, ST_Aspect,
/// ST_HillShade, ST_MapAlgebra) against the source raster and persist the
/// output through the same <c>raster_data</c> path the raster import service
/// uses, so downstream adapters can read it via
/// <see cref="IRasterStore"/> without additional bookkeeping.
/// </remarks>
public interface ISurfaceAnalysisService
{
    /// <summary>
    /// Computes a slope raster from a DEM.
    /// </summary>
    /// <param name="request">Identifies the source raster and target layer.</param>
    /// <param name="units">Output units (degrees, percent, or radians).</param>
    /// <param name="zFactor">
    /// Vertical-to-horizontal scale factor. Must be strictly positive. Use
    /// <c>1.0</c> when the DEM's vertical units match its horizontal units.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<SurfaceAnalysisResult> ComputeSlopeAsync(
        SurfaceAnalysisRequest request,
        SlopeUnits units,
        double zFactor,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Computes an aspect raster (compass bearing of steepest descent, in
    /// degrees, 0–360 with 0 at north).
    /// </summary>
    Task<SurfaceAnalysisResult> ComputeAspectAsync(
        SurfaceAnalysisRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Computes a hillshade raster.
    /// </summary>
    /// <param name="request">Identifies the source raster and target layer.</param>
    /// <param name="azimuthDegrees">
    /// Azimuth of the illumination source in degrees, measured clockwise from
    /// north. Must lie in [0, 360].
    /// </param>
    /// <param name="altitudeDegrees">
    /// Altitude of the illumination source above the horizon, in degrees. Must
    /// lie in [0, 90].
    /// </param>
    /// <param name="zFactor">
    /// Vertical-to-horizontal scale factor. Must be strictly positive.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<SurfaceAnalysisResult> ComputeHillshadeAsync(
        SurfaceAnalysisRequest request,
        double azimuthDegrees,
        double altitudeDegrees,
        double zFactor,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Computes a rugosity raster using the specified method (TRI, TPI, or
    /// Roughness) over a square window of radius <paramref name="windowRadius"/>.
    /// </summary>
    /// <param name="request">Identifies the source raster and target layer.</param>
    /// <param name="method">
    /// Ruggedness family: Terrain Ruggedness Index, Topographic Position
    /// Index, or Roughness.
    /// </param>
    /// <param name="windowRadius">
    /// Window radius in pixels. Must currently be <c>1</c>; the PostGIS
    /// built-ins used by the canonical runtime only support a 3x3 focal
    /// neighborhood today.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<SurfaceAnalysisResult> ComputeRugosityAsync(
        SurfaceAnalysisRequest request,
        RugosityMethod method,
        int windowRadius,
        CancellationToken cancellationToken = default);
}
