// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Raster.Abstractions;

namespace Honua.Protocols.Ogc.Api.Edr;

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
