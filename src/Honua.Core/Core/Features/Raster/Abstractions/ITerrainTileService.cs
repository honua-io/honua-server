// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Raster.Domain;

namespace Honua.Core.Features.Raster.Abstractions;

public interface ITerrainTileService
{
    Task<TerrainDatasetMetadata?> GetDatasetMetadataAsync(
        int layerId,
        RasterMergeStrategy mergeStrategy,
        CancellationToken cancellationToken = default);

    Task<TerrainTileResult> GetTerrainRgbTileAsync(
        TerrainTileRequest request,
        CancellationToken cancellationToken = default);
}
