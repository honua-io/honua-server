// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Raster.Domain;

namespace Honua.Core.Features.Raster.Abstractions;

/// <summary>
/// Abstraction for raster data storage and retrieval operations.
/// Provides access to raster datasets with PostGIS raster backend.
/// </summary>
public interface IRasterStore
{
    /// <summary>
    /// Retrieves metadata for a raster by its unique identifier.
    /// </summary>
    /// <param name="layerId">Layer identifier containing the raster</param>
    /// <param name="rasterId">Unique raster identifier</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Raster metadata if found, null otherwise</returns>
    Task<RasterInfo?> GetRasterInfoAsync(int layerId, long rasterId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Exports raster data with optional clipping, reprojection, and resampling.
    /// Equivalent to Esri Image Server exportImage operation.
    /// </summary>
    /// <param name="layerId">Layer identifier containing the raster</param>
    /// <param name="rasterId">Raster identifier to export</param>
    /// <param name="query">Query specification for filtering, clipping, and formatting</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Processed raster data in the requested format</returns>
    Task<RasterResult> ExportImageAsync(int layerId, long rasterId, RasterQuery query, CancellationToken cancellationToken = default);

    /// <summary>
    /// Identifies pixel values at a specific geographic point.
    /// Equivalent to Esri Image Server identify operation.
    /// </summary>
    /// <param name="layerId">Layer identifier containing the raster</param>
    /// <param name="rasterId">Raster identifier to query</param>
    /// <param name="x">X coordinate of the query point</param>
    /// <param name="y">Y coordinate of the query point</param>
    /// <param name="srid">Spatial reference system of the coordinates (defaults to raster's SRID)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Pixel values at the specified point</returns>
    Task<PixelValueResult> IdentifyAsync(
        int layerId,
        long rasterId,
        double x,
        double y,
        int? srid = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Generates a pre-tiled raster tile for efficient web mapping.
    /// Equivalent to Esri Image Server imageTile operation.
    /// </summary>
    /// <param name="layerId">Layer identifier containing the raster</param>
    /// <param name="rasterId">Raster identifier to tile</param>
    /// <param name="level">Zoom level</param>
    /// <param name="row">Tile row</param>
    /// <param name="col">Tile column</param>
    /// <param name="format">Output format for the tile</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Tile data in the requested format, null if tile is empty</returns>
    Task<RasterResult?> GetImageTileAsync(
        int layerId,
        long rasterId,
        int level,
        int row,
        int col,
        RasterFormat format = RasterFormat.PNG,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Calculates statistics for raster bands.
    /// </summary>
    /// <param name="layerId">Layer identifier containing the raster</param>
    /// <param name="rasterId">Raster identifier to analyze</param>
    /// <param name="bands">Specific bands to analyze (1-based indexing). If null, all bands are analyzed.</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Statistics for each requested band</returns>
    Task<RasterStatistics[]> GetStatisticsAsync(
        int layerId,
        long rasterId,
        int[]? bands = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the spatial extent of a raster.
    /// </summary>
    /// <param name="layerId">Layer identifier containing the raster</param>
    /// <param name="rasterId">Raster identifier to analyze</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Spatial extent of the raster, null if raster not found</returns>
    Task<RasterExtent?> GetExtentAsync(int layerId, long rasterId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves the primary raster metadata for a layer.
    /// </summary>
    /// <param name="layerId">Layer identifier to query</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Most recent raster metadata for the layer, or null if none exists</returns>
    Task<RasterInfo?> GetPrimaryRasterInfoAsync(int layerId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists all rasters in a layer.
    /// </summary>
    /// <param name="layerId">Layer identifier to query</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Array of raster metadata</returns>
    Task<RasterInfo[]> ListRastersAsync(int layerId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Computes histograms for the requested bands of a raster.
    /// </summary>
    /// <param name="layerId">Layer identifier containing the raster.</param>
    /// <param name="rasterId">Raster identifier to analyze.</param>
    /// <param name="bands">Optional 1-based band selection. <c>null</c> requests every band.</param>
    /// <param name="binCount">Number of bins per band; clamped at the implementation level.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>One histogram per requested band.</returns>
    Task<RasterHistogram[]> GetHistogramsAsync(
        int layerId,
        long rasterId,
        int[]? bands = null,
        int binCount = 256,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Computes zonal aggregates by intersecting a raster with the geometries
    /// of a zones feature layer, producing one row per zone.
    /// </summary>
    /// <param name="layerId">Layer identifier containing the raster.</param>
    /// <param name="rasterId">Raster identifier to aggregate.</param>
    /// <param name="zonesLayerId">Feature layer whose geometries define the aggregation zones.</param>
    /// <param name="band">1-based band to aggregate.</param>
    /// <param name="statistics">
    /// Stat names to compute. Allowed values: <c>count</c>, <c>sum</c>,
    /// <c>mean</c>, <c>min</c>, <c>max</c>, <c>stddev</c>, <c>variance</c>.
    /// Comparison is case-insensitive; unknown names are rejected.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// One row per zone feature, containing the requested stats. Empty
    /// intersections still emit a row with <c>PixelCount = 0</c>.
    /// </returns>
    Task<RasterZonalStatisticsRow[]> ComputeZonalStatisticsAsync(
        int layerId,
        long rasterId,
        int zonesLayerId,
        int band,
        IReadOnlyList<string> statistics,
        CancellationToken cancellationToken = default);
}
