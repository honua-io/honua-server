// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Configuration;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Tiles;

namespace Honua.Core.Features.FeatureStore.ReadOnlyProviders;

/// <summary>
/// Tile provider that rejects all tile operations with <see cref="NotSupportedException"/>.
/// Registered by feature providers (DuckDB, MySQL/MariaDB) that do not support native MVT or
/// H3 tile generation so DI consumers requiring <see cref="ITileProvider"/> can activate.
/// </summary>
public sealed class ReadOnlyTileProvider : ITileProvider
{
    private readonly string _providerName;

    /// <summary>
    /// Initializes a new instance of the <see cref="ReadOnlyTileProvider"/> class.
    /// </summary>
    /// <param name="providerName">
    /// Display name of the provider, used in the rejection message
    /// (for example <c>"DuckDB"</c> or <c>"MySQL/MariaDB"</c>).
    /// </param>
    public ReadOnlyTileProvider(string providerName)
    {
        _providerName = providerName;
    }

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
        => throw new NotSupportedException($"{_providerName} provider does not support MVT tile generation.");

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
        => throw new NotSupportedException($"{_providerName} provider does not support H3 MVT tile generation.");
}
