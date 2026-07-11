// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.Domain;

namespace Honua.Protocols.GeoServices.ImageServer.Services;

/// <summary>
/// Cohesive raster backend used by ImageServer export orchestration.
/// </summary>
internal sealed class ImageServerExportBackend(
    IRasterStore rasterStore,
    IZarrRasterSliceReader zarrRasterSliceReader)
{
    public Task<RasterInfo[]> QueryRastersAsync(
        int layerId,
        RasterSelectionQuery query,
        CancellationToken cancellationToken)
        => rasterStore.QueryRastersAsync(layerId, query, cancellationToken);

    public Task<RasterResult> ExportAsync(
        int layerId,
        RasterInfo[] rasters,
        RasterMergeStrategy mergeStrategy,
        RasterQuery query,
        RasterMosaicOrdering ordering,
        RasterMosaicAttributeSort? attributeSort,
        CancellationToken cancellationToken)
        => rasters.Length == 1
            ? rasterStore.ExportImageAsync(layerId, rasters[0].Id, query, cancellationToken)
            : rasterStore.ExportMosaicAsync(
                layerId,
                rasters.Select(static raster => raster.Id).ToArray(),
                mergeStrategy,
                query,
                ordering,
                attributeSort,
                cancellationToken);

    public Task<RasterExtent?> GetExtentAsync(
        int layerId,
        long rasterId,
        CancellationToken cancellationToken)
        => rasterStore.GetExtentAsync(layerId, rasterId, cancellationToken);

    public Task<ZarrRasterSliceReadResult> ReadZarrSliceAsync(
        int layerId,
        ZarrRasterSliceReadRequest request,
        CancellationToken cancellationToken)
        => zarrRasterSliceReader.ReadAsync(layerId, request, cancellationToken);
}
