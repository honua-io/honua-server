// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Raster.Domain;

namespace Honua.Core.Features.Raster.Abstractions;

public readonly record struct CogTileLookup(RasterResult? Result, bool EditionGateHit);

public interface ICogTileResolver
{
    Task<RasterResult?> GetTileAsync(
        CogRegistration registration,
        int level,
        int row,
        int col,
        RasterFormat format,
        CancellationToken cancellationToken = default);

    Task<CogTileLookup> GetTileForLayerAsync(
        int layerId,
        int level,
        int row,
        int col,
        RasterFormat format,
        CancellationToken cancellationToken = default);
}
