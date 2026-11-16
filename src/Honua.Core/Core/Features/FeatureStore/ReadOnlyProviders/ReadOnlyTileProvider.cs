// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Configuration;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Tiles;

namespace Honua.Core.Features.FeatureStore.ReadOnlyProviders;

public sealed class ReadOnlyTileProvider : ITileProvider
{
    private readonly string _providerName;

    public ReadOnlyTileProvider(string providerName)
    {
        _providerName = providerName;
    }

    public Task<byte[]?> GetMvtTileAsync(
        int layerId,
        int x,
        int y,
        int z,
        FeatureQuery? query,
        TileOptions tileOptions,
        TileLimits tileLimits,
        GridGeometry? gridGeometry = null,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException($"{_providerName} provider does not support MVT tile generation.");

    public Task<byte[]?> GetH3MvtTileAsync(
        int layerId,
        int x,
        int y,
        int z,
        int resolution,
        FeatureQuery? query,
        TileOptions tileOptions,
        TileLimits tileLimits,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException($"{_providerName} provider does not support H3 MVT tile generation.");
}
