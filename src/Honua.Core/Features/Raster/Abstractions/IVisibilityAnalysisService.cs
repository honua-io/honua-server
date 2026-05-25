// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Raster.Domain;

namespace Honua.Core.Features.Raster.Abstractions;

/// <summary>
/// Computes 3D visibility analyses (line-of-sight and viewshed) against a
/// registered elevation/terrain surface.
/// </summary>
/// <remarks>
/// Implementations build on the elevation profile sampler
/// (<see cref="IElevationService.QueryProfileAsync"/>) rather than reading the
/// DEM directly, so they inherit the geodesic profile geometry, mosaic merge
/// strategy, and no-data handling already established for the elevation API.
/// </remarks>
public interface IVisibilityAnalysisService
{
    /// <summary>
    /// Determines whether <paramref name="target"/> is visible from
    /// <paramref name="observer"/> over the elevation surface, returning the
    /// first terrain obstruction when the target is blocked.
    /// </summary>
    /// <param name="layerId">Layer that owns the elevation source.</param>
    /// <param name="observer">Observer position and height offset.</param>
    /// <param name="target">Target position and height offset.</param>
    /// <param name="sampleCount">
    /// Requested number of profile samples along the sight line. <c>null</c>
    /// applies the configured default. Clamped to the elevation sampling limit.
    /// </param>
    /// <param name="mergeStrategy">Mosaic merge strategy applied across overlapping rasters.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<LineOfSightResult> ComputeLineOfSightAsync(
        int layerId,
        VisibilityPoint observer,
        VisibilityPoint target,
        int? sampleCount,
        RasterMergeStrategy mergeStrategy,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Computes which radially-sampled points around <paramref name="observer"/>
    /// are visible over the elevation surface within the requested radius.
    /// </summary>
    /// <param name="layerId">Layer that owns the elevation source.</param>
    /// <param name="observer">Observer longitude/latitude (height comes from <paramref name="options"/>).</param>
    /// <param name="options">Radius, ray count, sample resolution, and height offsets.</param>
    /// <param name="mergeStrategy">Mosaic merge strategy applied across overlapping rasters.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<ViewshedResult> ComputeViewshedAsync(
        int layerId,
        VisibilityPoint observer,
        ViewshedOptions options,
        RasterMergeStrategy mergeStrategy,
        CancellationToken cancellationToken = default);
}
