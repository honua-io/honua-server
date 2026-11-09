// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Raster.Domain;

namespace Honua.Core.Features.Raster.Abstractions;

public interface IRasterStore
{
    Task<RasterInfo?> GetRasterInfoAsync(int layerId, long rasterId, CancellationToken cancellationToken = default);

    Task<RasterInfo[]> QueryRastersAsync(
        int layerId,
        RasterSelectionQuery query,
        CancellationToken cancellationToken = default);

    // A provider that cannot push the predicate must use the bounded fallback and say so; the predicate is a bounding-box overlap, not exact geometry.
    Task<RasterCatalogPage> QueryCatalogAsync(
        int layerId,
        RasterCatalogQuery query,
        CancellationToken cancellationToken = default);

    Task<RasterResult> ExportImageAsync(int layerId, long rasterId, RasterQuery query, CancellationToken cancellationToken = default);

    Task<RasterResult> ExportMosaicAsync(
        int layerId,
        long[] rasterIds,
        RasterMergeStrategy mergeStrategy,
        RasterQuery query,
        RasterMosaicOrdering ordering = RasterMosaicOrdering.AcquisitionNewest,
        RasterMosaicAttributeSort? attributeSort = null,
        CancellationToken cancellationToken = default);

    // An extent with no raster data returns an empty image rather than an error, because a tiling client treats a non-image body as a failed tile.
    Task<RasterResult> ExportEmptyExtentAsync(
        int layerId,
        RasterQuery query,
        CancellationToken cancellationToken = default);

    Task<PixelValueResult> IdentifyAsync(
        int layerId,
        long rasterId,
        double x,
        double y,
        int? srid = null,
        RasterIdentifyRendering? rendering = null,
        CancellationToken cancellationToken = default);

    Task<PixelValueResult> IdentifyMosaicAsync(
        int layerId,
        long[] rasterIds,
        RasterMergeStrategy mergeStrategy,
        double x,
        double y,
        int? srid = null,
        RasterIdentifyRendering? rendering = null,
        RasterMosaicOrdering ordering = RasterMosaicOrdering.AcquisitionNewest,
        RasterMosaicAttributeSort? attributeSort = null,
        CancellationToken cancellationToken = default);

    Task<RasterResult?> GetImageTileAsync(
        int layerId,
        long rasterId,
        int level,
        int row,
        int col,
        RasterFormat format = RasterFormat.PNG,
        CancellationToken cancellationToken = default);

    Task<RasterResult?> GetMosaicImageTileAsync(
        int layerId,
        long[] rasterIds,
        RasterMergeStrategy mergeStrategy,
        int level,
        int row,
        int col,
        RasterFormat format = RasterFormat.PNG,
        CancellationToken cancellationToken = default);

    Task<RasterResult?> GetImageTileAsync(
        int layerId,
        long rasterId,
        RasterTileWindow window,
        RasterFormat format = RasterFormat.PNG,
        CancellationToken cancellationToken = default);

    Task<RasterResult?> GetMosaicImageTileAsync(
        int layerId,
        long[] rasterIds,
        RasterMergeStrategy mergeStrategy,
        RasterTileWindow window,
        RasterFormat format = RasterFormat.PNG,
        CancellationToken cancellationToken = default);

    // Persisted statistics are served unless a rendering rule is set, because a full-pixel scan is too slow to repeat and a rendered histogram cannot reuse the raw statistics.
    Task<RasterStatistics[]> GetStatisticsAsync(
        int layerId,
        long rasterId,
        int[]? bands = null,
        RasterIdentifyRendering? rendering = null,
        CancellationToken cancellationToken = default);

    // A rendering rule computes fresh statistics over the rendered mosaic; otherwise the persisted snapshot is served until membership changes.
    Task<RasterStatistics[]> GetMosaicStatisticsAsync(
        int layerId,
        long[] rasterIds,
        RasterMergeStrategy mergeStrategy,
        int[]? bands = null,
        RasterIdentifyRendering? rendering = null,
        CancellationToken cancellationToken = default);

    Task<RasterExtent?> GetExtentAsync(int layerId, long rasterId, CancellationToken cancellationToken = default);

    Task<RasterInfo?> GetPrimaryRasterInfoAsync(int layerId, CancellationToken cancellationToken = default);

    Task<RasterInfo[]> ListRastersAsync(int layerId, CancellationToken cancellationToken = default);

    Task<IReadOnlyDictionary<long, RasterSensorMetadata>> GetSensorMetadataAsync(
        IReadOnlyCollection<long> rasterIds,
        CancellationToken cancellationToken = default);

    Task<int?> GetRasterLayerIdAsync(long rasterId, CancellationToken cancellationToken = default);

    Task<bool> DeleteRasterAsync(int layerId, long rasterId, CancellationToken cancellationToken = default);

    Task<RasterInfo?> UpdateRasterMetadataAsync(
        int layerId,
        long rasterId,
        RasterMetadataUpdate update,
        CancellationToken cancellationToken = default);

    Task<RasterHistogram[]> GetHistogramsAsync(
        int layerId,
        long rasterId,
        int[]? bands = null,
        int binCount = 256,
        RasterIdentifyRendering? rendering = null,
        CancellationToken cancellationToken = default);

    Task<RasterHistogram[]> GetMosaicHistogramsAsync(
        int layerId,
        long[] rasterIds,
        RasterMergeStrategy mergeStrategy,
        int[]? bands = null,
        int binCount = 256,
        RasterIdentifyRendering? rendering = null,
        CancellationToken cancellationToken = default);

    Task<RasterStatistics[]> GetClippedStatisticsAsync(
        int layerId,
        long rasterId,
        byte[] clipGeometry,
        int? clipSrid,
        int[]? bands = null,
        RasterIdentifyRendering? rendering = null,
        CancellationToken cancellationToken = default);

    Task<RasterHistogram[]> GetClippedHistogramsAsync(
        int layerId,
        long rasterId,
        byte[] clipGeometry,
        int? clipSrid,
        int[]? bands = null,
        int binCount = 256,
        RasterIdentifyRendering? rendering = null,
        CancellationToken cancellationToken = default);

    Task<RasterStatistics[]> GetClippedMosaicStatisticsAsync(
        int layerId,
        long[] rasterIds,
        RasterMergeStrategy mergeStrategy,
        byte[] clipGeometry,
        int? clipSrid,
        int[]? bands = null,
        RasterIdentifyRendering? rendering = null,
        CancellationToken cancellationToken = default);

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

    Task<RasterBandVectorSet> ReadClippedBandVectorsAsync(
        int layerId,
        long[] rasterIds,
        RasterMergeStrategy mergeStrategy,
        byte[] clipGeometry,
        int? clipSrid,
        int[]? bands,
        int maxPixels,
        CancellationToken cancellationToken = default);

    Task<RasterZonalStatisticsRow[]> ComputeZonalStatisticsAsync(
        int layerId,
        long rasterId,
        int zonesLayerId,
        int band,
        IReadOnlyList<string> statistics,
        CancellationToken cancellationToken = default);
}
