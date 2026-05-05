// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Configuration;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Tiles;

namespace Honua.MySql.Features.FeatureStore;

/// <summary>
/// Tile provider that rejects all tile operations.
/// Registered when the MySQL/MariaDB provider is active since the slice does not support
/// native MVT or H3 tile generation.
/// </summary>
internal sealed class ReadOnlyMySqlTileProvider : ITileProvider
{
    /// <inheritdoc />
    public Task<byte[]?> GetMvtTileAsync(
        int layerId,
        int x,
        int y,
        int z,
        FeatureQuery? query,
        TileOptions tileOptions,
        TileLimits tileLimits,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("MySQL/MariaDB provider does not support native MVT tile generation.");

    /// <inheritdoc />
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
        => throw new NotSupportedException("MySQL/MariaDB provider does not support H3 MVT tile generation.");
}
