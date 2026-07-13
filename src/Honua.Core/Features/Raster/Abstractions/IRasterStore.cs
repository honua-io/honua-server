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
    /// Queries a layer's raster catalog with the neutral <see cref="RasterCatalogQuery"/> contract,
    /// pushing the validated envelope-intersects predicate, identity, temporal, and paging inputs
    /// into storage <b>before materialization</b> so large imagery catalogs are never fully read to
    /// satisfy a spatial browse.
    /// </summary>
    /// <remarks>
    /// Implementations that can push the predicate into indexed storage (PostGIS) MUST do so and set
    /// <see cref="RasterCatalogPage.PredicatePushedDown"/> to <see langword="true"/>. Providers that
    /// cannot push down MUST fall back to a bounded materialization — delegate to
    /// <see cref="Honua.Core.Features.Raster.Services.RasterCatalogQueryEvaluator.EvaluateAsync"/> over
    /// <see cref="ListRastersAsync"/> — which preserves identical result count, aggregate extent,
    /// paging, and envelope-intersects semantics while declaring the fallback via
    /// <see cref="RasterCatalogPage.PredicatePushedDown"/> = <see langword="false"/>. The envelope
    /// predicate is intentionally an inclusive bounding-box overlap, not exact geometry.
    /// </remarks>
    /// <param name="layerId">Layer identifier to query.</param>
    /// <param name="query">The validated neutral catalog query.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The matched page plus pre-paging count/extent and read-bounding telemetry counters.</returns>
    Task<RasterCatalogPage> QueryCatalogAsync(
        int layerId,
        RasterCatalogQuery query,
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
    /// <param name="layerId">Layer identifier containing the rasters.</param>
    /// <param name="rasterIds">Raster identifiers to composite.</param>
    /// <param name="mergeStrategy">Pixel-resolution operation applied to overlapping pixels.</param>
    /// <param name="query">Query specification for clipping, reprojection, sizing, and formatting.</param>
    /// <param name="ordering">
    /// Ordering applied when overlapping rasters are unioned. Controls which raster wins a
    /// contested pixel for the LAST/FIRST merge strategies and is orthogonal to
    /// <paramref name="mergeStrategy"/>. <see cref="RasterMosaicOrdering.LockOrder"/> assumes
    /// the caller has already restricted <paramref name="rasterIds"/> to the locked set; it
    /// composites them ordered by newest acquisition and bypasses the timestamp newest-batch
    /// snapshot filter that <see cref="QueryRastersAsync"/> would otherwise apply.
    /// </param>
    /// <param name="attributeSort">
    /// Optional non-date attribute ordering for an <c>esriMosaicByAttribute</c> mosaic. When
    /// supplied (and <paramref name="ordering"/> is <see cref="RasterMosaicOrdering.Attribute"/>),
    /// the contested pixel is resolved by the allowlisted raster-catalog column it names rather
    /// than by acquisition date. Ignored for the other ordering modes.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<RasterResult> ExportMosaicAsync(
        int layerId,
        long[] rasterIds,
        RasterMergeStrategy mergeStrategy,
        RasterQuery query,
        RasterMosaicOrdering ordering = RasterMosaicOrdering.AcquisitionNewest,
        RasterMosaicAttributeSort? attributeSort = null,
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
    /// Generates a pre-tiled raster tile aligned to an explicit tile matrix set (gridset) window
    /// rather than the implicit Web Mercator <c>z/x/y</c> grid. The tile bounds and output SRID are
    /// supplied in <paramref name="window"/> (computed by the caller from the one canonical gridset
    /// definition), and the source raster is reprojected into that window. This is the
    /// gridset-aware counterpart to
    /// <see cref="GetImageTileAsync(int, long, int, int, int, RasterFormat, CancellationToken)"/>
    /// used by the WorldCRS84Quad and operator-defined gridset tile paths so no gridset geodesy is
    /// duplicated in the protocol adapters.
    /// </summary>
    /// <param name="layerId">Layer identifier containing the raster.</param>
    /// <param name="rasterId">Raster identifier to tile.</param>
    /// <param name="window">The gridset tile window (bounds + SRID + output pixel dimensions).</param>
    /// <param name="format">Output format for the tile.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Tile data in the requested format, or <see langword="null"/> when the tile is empty.</returns>
    Task<RasterResult?> GetImageTileAsync(
        int layerId,
        long rasterId,
        RasterTileWindow window,
        RasterFormat format = RasterFormat.PNG,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Generates a mosaicked tile aligned to an explicit tile matrix set (gridset) window. The
    /// gridset-aware counterpart to
    /// <see cref="GetMosaicImageTileAsync(int, long[], RasterMergeStrategy, int, int, int, RasterFormat, CancellationToken)"/>.
    /// </summary>
    /// <param name="layerId">Layer identifier containing the rasters.</param>
    /// <param name="rasterIds">Raster identifiers participating in the mosaic.</param>
    /// <param name="mergeStrategy">The mosaic merge strategy.</param>
    /// <param name="window">The gridset tile window (bounds + SRID + output pixel dimensions).</param>
    /// <param name="format">Output format for the tile.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Tile data in the requested format, or <see langword="null"/> when the tile is empty.</returns>
    Task<RasterResult?> GetMosaicImageTileAsync(
        int layerId,
        long[] rasterIds,
        RasterMergeStrategy mergeStrategy,
        RasterTileWindow window,
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
    /// <param name="rendering">
    /// Optional rendering rule (clip/stretch/colormap). When supplied, statistics are computed
    /// over the rendered pixels (post clip/stretch/colormap) rather than the raw source raster,
    /// matching the Esri ImageServer <c>computeStatisticsHistograms</c> <c>renderingRule</c>
    /// behaviour. When supplied the values are always computed fresh (the persisted whole-raster
    /// statistics describe the raw source and cannot be reused). When <c>null</c> the persisted
    /// raw-source statistics are returned (the default behaviour).
    /// </param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Statistics for each requested band</returns>
    Task<RasterStatistics[]> GetStatisticsAsync(
        int layerId,
        long rasterId,
        int[]? bands = null,
        RasterIdentifyRendering? rendering = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets statistics for a composited layer mosaic. Implementations must persist computed
    /// values keyed by the layer's raster-id set and serve subsequent reads from the persisted
    /// rows; the snapshot is invalidated when the layer's raster membership changes. When
    /// <paramref name="rendering"/> is supplied the statistics are computed fresh over the
    /// rendered mosaic pixels instead.
    /// </summary>
    Task<RasterStatistics[]> GetMosaicStatisticsAsync(
        int layerId,
        long[] rasterIds,
        RasterMergeStrategy mergeStrategy,
        int[]? bands = null,
        RasterIdentifyRendering? rendering = null,
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
    /// Retrieves the sensor/camera/orientation/RPC metadata rows for the supplied rasters
    /// from the <c>raster_sensor_metadata</c> companion table, keyed by raster id. Rasters
    /// without a companion row are omitted from the result. Returns an empty dictionary when
    /// the companion table is not provisioned (degraded-deployment guard), so callers degrade
    /// gracefully to the no-sensor-metadata path.
    /// </summary>
    /// <param name="rasterIds">Raster identifiers to hydrate.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Sensor metadata keyed by raster id; entries are present only when modeled.</returns>
    Task<IReadOnlyDictionary<long, RasterSensorMetadata>> GetSensorMetadataAsync(
        IReadOnlyCollection<long> rasterIds,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves the layer that owns a raster, given only the raster id. Returns
    /// <see langword="null"/> when no raster with that id exists. Used by the admin raster CRUD
    /// surface where the route is keyed by raster id.
    /// </summary>
    /// <param name="rasterId">Raster identifier to resolve.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The owning layer id, or <see langword="null"/> when the raster does not exist.</returns>
    Task<int?> GetRasterLayerIdAsync(long rasterId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a raster and all dependent rows (statistics, tiles, sensor metadata cascade)
    /// from a layer.
    /// </summary>
    /// <param name="layerId">Layer identifier containing the raster.</param>
    /// <param name="rasterId">Raster identifier to delete.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><see langword="true"/> when a row was deleted; <see langword="false"/> when no matching raster existed.</returns>
    Task<bool> DeleteRasterAsync(int layerId, long rasterId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates the mutable descriptive metadata (name, description, acquisition date) of a
    /// raster. Only the fields present in <paramref name="update"/> are modified; absent fields
    /// are left unchanged.
    /// </summary>
    /// <param name="layerId">Layer identifier containing the raster.</param>
    /// <param name="rasterId">Raster identifier to update.</param>
    /// <param name="update">Fields to update; unset fields are left unchanged.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The updated raster metadata, or <see langword="null"/> when no matching raster existed.</returns>
    Task<RasterInfo?> UpdateRasterMetadataAsync(
        int layerId,
        long rasterId,
        RasterMetadataUpdate update,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Computes histograms for the requested bands of a raster.
    /// </summary>
    /// <param name="layerId">Layer identifier containing the raster.</param>
    /// <param name="rasterId">Raster identifier to analyze.</param>
    /// <param name="bands">Optional 1-based band selection. <c>null</c> requests every band.</param>
    /// <param name="binCount">Number of bins per band; clamped at the implementation level.</param>
    /// <param name="rendering">
    /// Optional rendering rule (clip/stretch/colormap). When supplied, the histogram is computed
    /// over the rendered pixels (post clip/stretch/colormap) instead of the raw source raster,
    /// matching the Esri ImageServer <c>computeStatisticsHistograms</c> <c>renderingRule</c>
    /// behaviour. When <c>null</c> the raw source histogram is returned (the default behaviour).
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>One histogram per requested band.</returns>
    Task<RasterHistogram[]> GetHistogramsAsync(
        int layerId,
        long rasterId,
        int[]? bands = null,
        int binCount = 256,
        RasterIdentifyRendering? rendering = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Computes histograms for a composited layer mosaic. When <paramref name="rendering"/> is
    /// supplied the histogram is computed over the rendered mosaic pixels instead.
    /// </summary>
    Task<RasterHistogram[]> GetMosaicHistogramsAsync(
        int layerId,
        long[] rasterIds,
        RasterMergeStrategy mergeStrategy,
        int[]? bands = null,
        int binCount = 256,
        RasterIdentifyRendering? rendering = null,
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
    /// <param name="rendering">
    /// Optional rendering rule (stretch/colormap) applied to the clipped pixels before the
    /// statistics are computed, so computeStatisticsHistograms with both an AOI geometry and a
    /// <c>renderingRule</c> reports the rendered values inside the AOI.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<RasterStatistics[]> GetClippedStatisticsAsync(
        int layerId,
        long rasterId,
        byte[] clipGeometry,
        int? clipSrid,
        int[]? bands = null,
        RasterIdentifyRendering? rendering = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Computes per-band histograms over the portion of a raster clipped to an
    /// area-of-interest geometry (WKB). Always computed fresh. When <paramref name="rendering"/>
    /// is supplied the histogram is computed over the rendered clipped pixels.
    /// </summary>
    Task<RasterHistogram[]> GetClippedHistogramsAsync(
        int layerId,
        long rasterId,
        byte[] clipGeometry,
        int? clipSrid,
        int[]? bands = null,
        int binCount = 256,
        RasterIdentifyRendering? rendering = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Computes per-band statistics over a composited layer mosaic clipped to an
    /// area-of-interest geometry (WKB). When <paramref name="rendering"/> is supplied the
    /// statistics are computed over the rendered clipped mosaic pixels.
    /// </summary>
    Task<RasterStatistics[]> GetClippedMosaicStatisticsAsync(
        int layerId,
        long[] rasterIds,
        RasterMergeStrategy mergeStrategy,
        byte[] clipGeometry,
        int? clipSrid,
        int[]? bands = null,
        RasterIdentifyRendering? rendering = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Computes per-band histograms over a composited layer mosaic clipped to an
    /// area-of-interest geometry (WKB). When <paramref name="rendering"/> is supplied the
    /// histogram is computed over the rendered clipped mosaic pixels.
    /// </summary>
    Task<RasterHistogram[]> GetClippedMosaicHistogramsAsync(
        int layerId,
        long[] rasterIds,
        RasterMergeStrategy mergeStrategy,
        byte[] clipGeometry,
        int? clipSrid,
        int[]? bands = null,
        int binCount = 256,
        RasterIdentifyRendering? rendering = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads the aligned per-pixel band vectors of a raster (or composited mosaic) inside an
    /// area-of-interest clip geometry (WKB), for multivariate analytics such as class-signature
    /// covariance. Only pixels valid in <b>every</b> requested band are returned, each as a
    /// vector ordered to match <see cref="RasterBandVectorSet.Bands"/>.
    /// </summary>
    /// <remarks>
    /// The read is memory-bounded by <paramref name="maxPixels"/>: when the clip bounding box
    /// exceeds that budget the implementation returns
    /// <see cref="RasterBandVectorSet.ExceededPixelBudget"/> = <see langword="true"/> with no
    /// pixels, so the caller rejects the request rather than reporting a truncated (and therefore
    /// wrong) covariance. Tiling/streaming the read to lift the budget is deferred follow-up scope.
    /// </remarks>
    /// <param name="layerId">Layer identifier containing the rasters.</param>
    /// <param name="rasterIds">Catalog raster ids; a single id reads one raster, many composite a mosaic.</param>
    /// <param name="mergeStrategy">Pixel-resolution operation applied to overlapping mosaic pixels.</param>
    /// <param name="clipGeometry">Training AOI clip geometry in Well-Known Binary form.</param>
    /// <param name="clipSrid">SRID of <paramref name="clipGeometry"/>; <see langword="null"/> assumes the raster SRID.</param>
    /// <param name="bands">Optional 1-based band selection; <see langword="null"/> reads every band.</param>
    /// <param name="maxPixels">Maximum clip bounding-box pixels to materialize before rejecting.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<RasterBandVectorSet> ReadClippedBandVectorsAsync(
        int layerId,
        long[] rasterIds,
        RasterMergeStrategy mergeStrategy,
        byte[] clipGeometry,
        int? clipSrid,
        int[]? bands,
        int maxPixels,
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
