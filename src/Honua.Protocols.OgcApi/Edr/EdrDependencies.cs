// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Raster.Abstractions;

namespace Honua.Protocols.Ogc.Api.Edr;

/// <summary>
/// Shared dependency bag for the OGC API - EDR handler. EDR is a thin query layer over the
/// same registered coverages/datacubes that OGC API - Coverages and WCS expose, so it adapts
/// to the shared metadata catalog (<see cref="IMetadataV2GraphProvider"/>) and the canonical
/// raster read pipeline (<see cref="IRasterStore"/>) rather than reimplementing data access.
/// The <see cref="ICoordinateTransformService"/> reprojects storage-CRS raster extents to CRS84.
/// </summary>
internal sealed class EdrDependencies
{
    public EdrDependencies(
        IMetadataV2GraphProvider graphProvider,
        IRasterStore rasterStore,
        ICoordinateTransformService coordinateTransformService)
    {
        GraphProvider = graphProvider ?? throw new ArgumentNullException(nameof(graphProvider));
        RasterStore = rasterStore ?? throw new ArgumentNullException(nameof(rasterStore));
        CoordinateTransformService = coordinateTransformService ?? throw new ArgumentNullException(nameof(coordinateTransformService));
    }

    public IMetadataV2GraphProvider GraphProvider { get; }

    public IRasterStore RasterStore { get; }

    public ICoordinateTransformService CoordinateTransformService { get; }
}
