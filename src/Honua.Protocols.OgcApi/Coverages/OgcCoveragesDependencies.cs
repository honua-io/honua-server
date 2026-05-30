// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Raster.Abstractions;

namespace Honua.Server.Features.Protocols.Ogc.Api.Coverages;

internal sealed class OgcCoveragesDependencies
{
    public OgcCoveragesDependencies(
        IMetadataV2GraphProvider graphProvider,
        IRasterStore rasterStore,
        ICoordinateTransformService coordinateTransformService,
        ICrsRegistry crsRegistry)
    {
        GraphProvider = graphProvider ?? throw new ArgumentNullException(nameof(graphProvider));
        RasterStore = rasterStore ?? throw new ArgumentNullException(nameof(rasterStore));
        CoordinateTransformService = coordinateTransformService ?? throw new ArgumentNullException(nameof(coordinateTransformService));
        CrsRegistry = crsRegistry ?? throw new ArgumentNullException(nameof(crsRegistry));
    }

    public IMetadataV2GraphProvider GraphProvider { get; }
    public IRasterStore RasterStore { get; }
    public ICoordinateTransformService CoordinateTransformService { get; }
    public ICrsRegistry CrsRegistry { get; }
}
