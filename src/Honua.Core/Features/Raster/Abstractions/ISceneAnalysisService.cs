// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Raster.Domain;

namespace Honua.Core.Features.Raster.Abstractions;

/// <summary>
/// Computes scene analyses (sun/shadow and slice/volumetric cross-sections)
/// against a registered elevation/terrain surface.
/// </summary>
/// <remarks>
/// Implementations build on the elevation profile sampler
/// (<see cref="IElevationService.QueryProfileAsync"/>) rather than reading the
/// DEM directly, so they inherit the geodesic profile geometry, mosaic merge
/// strategy, and no-data handling already established for the elevation API.
/// The solar position math is independent of the elevation surface and is pure.
/// </remarks>
public interface ISceneAnalysisService
{
    /// <summary>
    /// Computes the solar position for <paramref name="observer"/> at the
    /// requested UTC instant, then casts the resulting shadow against the
    /// elevation surface in the anti-solar direction.
    /// </summary>
    /// <param name="layerId">Layer that owns the elevation source.</param>
    /// <param name="observer">Observer position and object height.</param>
    /// <param name="options">Solar instant and shadow-ray tracing options.</param>
    /// <param name="mergeStrategy">Mosaic merge strategy applied across overlapping rasters.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<SunShadowResult> ComputeSunShadowAsync(
        int layerId,
        ShadowObserver observer,
        SunShadowOptions options,
        RasterMergeStrategy mergeStrategy,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Computes the intersection of a vertical slice plane with the elevation
    /// surface, returning the sampled profile plus min/max/length metadata.
    /// </summary>
    /// <param name="layerId">Layer that owns the elevation source.</param>
    /// <param name="plane">Start/end definition of the vertical slice plane.</param>
    /// <param name="sampleCount">
    /// Requested number of samples along the slice. <c>null</c> applies the
    /// configured default. Clamped to the slice sampling limit.
    /// </param>
    /// <param name="mergeStrategy">Mosaic merge strategy applied across overlapping rasters.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<SliceResult> ComputeSliceAsync(
        int layerId,
        SlicePlane plane,
        int? sampleCount,
        RasterMergeStrategy mergeStrategy,
        CancellationToken cancellationToken = default);
}
