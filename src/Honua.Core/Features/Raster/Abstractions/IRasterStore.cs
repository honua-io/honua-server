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
    /// Queries rasters in a layer using optional spatial and temporal filters.
    /// Returned rasters are ordered from newest acquisition to oldest.
    /// </summary>
    /// <remarks>
    /// When <see cref="RasterSelectionQuery.Timestamp"/> is set, the implementation uses
    /// "newest batch" semantics: it returns only rasters whose effective acquisition equals
    /// the single most-recent acquisition at or before the requested instant. Rasters from
    /// earlier acquisitions are excluded even when they cover areas the newer batch does not.
    /// Layers with mixed-date scenes can therefore produce spatial coverage gaps when a
    /// timestamp filter is applied; per-pixel temporal mosaicking is deferred follow-up scope.
    /// </remarks>
    /// <param name="layerId">Layer identifier to query.</param>
    /// <param name="query">Selection filters to apply before mosaic building.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Matching raster metadata rows.</returns>
    Task<RasterInfo[]> QueryRastersAsync(
        int layerId,
        RasterSelectionQuery query,
        CancellationToken cancellationToken = default);

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
    /// Exports a composited layer mosaic built from the requested raster identifiers.
    /// </summary>
    Task<RasterResult> ExportMosaicAsync(
        int layerId,
        long[] rasterIds,
        RasterMergeStrategy mergeStrategy,
        RasterQuery query,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Identifies pixel values at a specific geographic point.
    /// Equivalent to Esri Image Server identify operation.
    /// </summary>
    /// <param name="layerId">Layer identifier containing the raster</param>
    /// <param name="rasterId">Raster identifier to query</param>
    /// <param name="x">X coordinate of the query point</param>
    /// <param name="y">Y coordinate of the query point</param>
    /// <param name="srid">Spatial reference system of the coordinates (defaults to raster's SRID)</param>
    /// <param name="rendering">
    /// Optional rendering rule (stretch/colormap/clip). When supplied, the returned value
    /// reflects the rendered pixel (post-stretch / post-colormap) instead of the raw source
    /// value, matching Esri ImageServer identify with a <c>renderingRule</c>. When <c>null</c>
    /// the raw source pixel values are returned (the default behaviour).
    /// </param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Pixel values at the specified point</returns>
    Task<PixelValueResult> IdentifyAsync(
        int layerId,
        long rasterId,
        double x,
        double y,
        int? srid = null,
        RasterIdentifyRendering? rendering = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Identifies pixel values against a composited layer mosaic. When <paramref name="rendering"/>
    /// is supplied, the returned value reflects the rendered pixel instead of the raw source value.
    /// </summary>
    Task<PixelValueResult> IdentifyMosaicAsync(
        int layerId,
        long[] rasterIds,
        RasterMergeStrategy mergeStrategy,
        double x,
        double y,
        int? srid = null,
        RasterIdentifyRendering? rendering = null,
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
    /// Generates a tile from a composited layer mosaic.
    /// </summary>
    Task<RasterResult?> GetMosaicImageTileAsync(
        int layerId,
        long[] rasterIds,
        RasterMergeStrategy mergeStrategy,
        int level,
        int row,
        int col,
        RasterFormat format = RasterFormat.PNG,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets statistics for raster bands. Implementations must serve persisted values
    /// (written at import time, or computed once and persisted on first read) rather than
    /// recomputing per request: full-pixel scans take tens of seconds on real datasets (#1639).
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
    /// Gets statistics for a composited layer mosaic. Implementations must persist computed
    /// values keyed by the layer's raster-id set and serve subsequent reads from the persisted
    /// rows; the snapshot is invalidated when the layer's raster membership changes.
    /// </summary>
    Task<RasterStatistics[]> GetMosaicStatisticsAsync(
        int layerId,
        long[] rasterIds,
        RasterMergeStrategy mergeStrategy,
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
    /// Computes histograms for a composited layer mosaic.
    /// </summary>
    Task<RasterHistogram[]> GetMosaicHistogramsAsync(
        int layerId,
        long[] rasterIds,
        RasterMergeStrategy mergeStrategy,
        int[]? bands = null,
        int binCount = 256,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Computes per-band statistics over the portion of a raster clipped to an
    /// area-of-interest geometry (WKB). Always computed fresh (the cached
    /// whole-raster statistics are not used). Used by ImageServer
    /// <c>computeStatisticsHistograms</c> when an AOI <c>geometry</c> is supplied.
    /// </summary>
    /// <param name="layerId">Layer identifier containing the raster.</param>
    /// <param name="rasterId">Raster identifier to analyse.</param>
    /// <param name="clipGeometry">Clip geometry in Well-Known Binary form.</param>
    /// <param name="clipSrid">SRID of <paramref name="clipGeometry"/>; <c>null</c> assumes the raster SRID.</param>
    /// <param name="bands">Optional 1-based band selection; <c>null</c> requests every band.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<RasterStatistics[]> GetClippedStatisticsAsync(
        int layerId,
        long rasterId,
        byte[] clipGeometry,
        int? clipSrid,
        int[]? bands = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Computes per-band histograms over the portion of a raster clipped to an
    /// area-of-interest geometry (WKB). Always computed fresh.
    /// </summary>
    Task<RasterHistogram[]> GetClippedHistogramsAsync(
        int layerId,
        long rasterId,
        byte[] clipGeometry,
        int? clipSrid,
        int[]? bands = null,
        int binCount = 256,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Computes per-band statistics over a composited layer mosaic clipped to an
    /// area-of-interest geometry (WKB).
    /// </summary>
    Task<RasterStatistics[]> GetClippedMosaicStatisticsAsync(
        int layerId,
        long[] rasterIds,
        RasterMergeStrategy mergeStrategy,
        byte[] clipGeometry,
        int? clipSrid,
        int[]? bands = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Computes per-band histograms over a composited layer mosaic clipped to an
    /// area-of-interest geometry (WKB).
    /// </summary>
    Task<RasterHistogram[]> GetClippedMosaicHistogramsAsync(
        int layerId,
        long[] rasterIds,
        RasterMergeStrategy mergeStrategy,
        byte[] clipGeometry,
        int? clipSrid,
        int[]? bands = null,
        int binCount = 256,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Computes zonal aggregates by intersecting a raster with the geometries
    /// of a zones feature layer, producing one row per eligible zone.
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
    /// One row per zone feature whose geometry is non-null and has a known SRID.
    /// Zones with missing geometry or unknown SRID are skipped. Empty intersections
    /// still emit a row with <c>PixelCount = 0</c> and <c>null</c> aggregate values.
    /// Throws <see cref="InvalidOperationException"/> when the source raster is
    /// missing or has an unknown SRID.
    /// </returns>
    Task<RasterZonalStatisticsRow[]> ComputeZonalStatisticsAsync(
        int layerId,
        long rasterId,
        int zonesLayerId,
        int band,
        IReadOnlyList<string> statistics,
        CancellationToken cancellationToken = default);
}
