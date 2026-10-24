// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.Domain;

namespace Honua.Protocols.Ogc.Classic.Wcs20;

/// <summary>
/// Cohesive canonical raster backend for WCS coverage metadata and pixel reads.
/// </summary>
internal sealed class Wcs20CoverageBackend(
    IRasterStore rasterStore,
    IZarrStore zarrStore,
    IZarrRasterSliceReader zarrRasterSliceReader)
{
    internal Task<RasterInfo?> GetPrimaryRasterInfoAsync(int layerId, CancellationToken cancellationToken)
        => rasterStore.GetPrimaryRasterInfoAsync(layerId, cancellationToken);

    internal Task<RasterExtent?> GetExtentAsync(int layerId, long rasterId, CancellationToken cancellationToken)
        => rasterStore.GetExtentAsync(layerId, rasterId, cancellationToken);

    internal Task<RasterResult> ExportImageAsync(
        int layerId,
        long rasterId,
        RasterQuery query,
        CancellationToken cancellationToken)
        => rasterStore.ExportImageAsync(layerId, rasterId, query, cancellationToken);

    internal Task<ZarrRegistration[]> ListZarrRegistrationsAsync(
        int layerId,
        CancellationToken cancellationToken)
        => zarrStore.ListByLayerAsync(layerId, cancellationToken);

    internal Task<ZarrRasterSliceReadResult> ReadZarrSliceAsync(
        int layerId,
        ZarrRasterSliceReadRequest request,
        CancellationToken cancellationToken)
        // The classic WCS GetCoverage path serves native-CRS slices only (transformed
        // WCS Zarr output is tracked as a follow-up under #2717); no transform service is
        // threaded here.
        => zarrRasterSliceReader.ReadAsync(layerId, request, cancellationToken: cancellationToken);
}
