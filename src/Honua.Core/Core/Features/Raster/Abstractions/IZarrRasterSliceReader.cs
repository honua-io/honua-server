// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Raster.Domain;

namespace Honua.Core.Features.Raster.Abstractions;

public interface IZarrRasterSliceReader
{
    Task<ZarrRasterSliceReadResult> ReadAsync(
        int layerId,
        ZarrRasterSliceReadRequest request,
        ICoordinateTransformService? coordinateTransform = null,
        CancellationToken cancellationToken = default);
}
