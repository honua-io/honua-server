// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Raster.Domain;

namespace Honua.Core.Features.Raster.Abstractions;

/// <summary>
/// Canonical surface-analysis operations for the geoprocessing runtime. Each
/// method reads a DEM from the supplied raster store and writes a derived
/// raster into the caller's output layer.
/// </summary>
/// <remarks>
/// Implementations issue PostGIS raster expressions against the source raster
/// and persist the output through the same <c>raster_data</c> path the raster
/// import service uses, so downstream adapters can read it via
/// <see cref="IRasterStore"/> without additional bookkeeping.
/// </remarks>
public interface ISurfaceAnalysisService
{
    /// <summary>
    /// Computes a slope raster from a DEM.
    /// </summary>
    Task<SurfaceAnalysisResult> ComputeSlopeAsync(
        SurfaceAnalysisRequest request,
        SlopeUnits units,
        double zFactor,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Computes an aspect raster.
    /// </summary>
    Task<SurfaceAnalysisResult> ComputeAspectAsync(
        SurfaceAnalysisRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Computes a hillshade raster.
    /// </summary>
    Task<SurfaceAnalysisResult> ComputeHillshadeAsync(
        SurfaceAnalysisRequest request,
        double azimuthDegrees,
        double altitudeDegrees,
        double zFactor,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Computes a ruggedness raster using a 3x3 focal neighborhood.
    /// </summary>
    Task<SurfaceAnalysisResult> ComputeRugosityAsync(
        SurfaceAnalysisRequest request,
        RugosityMethod method,
        int windowRadius,
        CancellationToken cancellationToken = default);
}
