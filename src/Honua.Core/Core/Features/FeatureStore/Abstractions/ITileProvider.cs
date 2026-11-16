// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Configuration;
using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.Core.Features.FeatureStore.Abstractions;

public interface ITileProvider
{
    Task<byte[]?> GetMvtTileAsync(
        int layerId,
        int x,
        int y,
        int z,
        FeatureQuery? query,
        Honua.Core.Features.Tiles.TileOptions tileOptions,
        TileLimits tileLimits,
        Honua.Core.Features.Tiles.GridGeometry? gridGeometry = null,
        CancellationToken cancellationToken = default);

    Task<byte[]?> GetH3MvtTileAsync(
        int layerId,
        int x,
        int y,
        int z,
        int resolution,
        FeatureQuery? query,
        Honua.Core.Features.Tiles.TileOptions tileOptions,
        TileLimits tileLimits,
        CancellationToken cancellationToken = default);
}
