// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Tiles;

/// <summary>
/// Pure resolver for the effective cache TTL (<c>Cache-Control: max-age</c>) of a
/// served tile. Applies the per-tileset override from
/// <see cref="TileOptions.TilesetLifecycle" /> when present, otherwise falls back
/// to the global <see cref="TileOptions.CacheMaxAge" />.
/// </summary>
/// <remarks>
/// This type has no dependencies and performs no I/O; it is a deterministic
/// function of its inputs so it can be unit-tested in isolation and reused by
/// every tile serve adapter. Cache eviction (size-quota / LRU) and scheduled
/// invalidation are deferred (#1794) and are intentionally out of scope here.
/// </remarks>
public static class TilesetTtlResolver
{
    /// <summary>
    /// Builds the canonical tileset-identity key used to look up per-tileset
    /// lifecycle overrides in <see cref="TileOptions.TilesetLifecycle" />.
    /// The key form is <c>serviceId/layerId/tileMatrixSetId</c>.
    /// </summary>
    /// <param name="serviceId">Logical service identity (e.g. the protocol/service id).</param>
    /// <param name="layerId">Layer (or collection) identity within the service.</param>
    /// <param name="tileMatrixSetId">Tile matrix set identity (e.g. <c>WebMercatorQuad</c>).</param>
    /// <returns>The composed lookup key.</returns>
    public static string BuildKey(string serviceId, string layerId, string tileMatrixSetId)
        => $"{serviceId}/{layerId}/{tileMatrixSetId}";

    /// <summary>
    /// Resolves the effective cache TTL (seconds) for a served tile given its
    /// identity. Returns the per-tileset <see cref="TilesetCacheLifecycle.TtlSeconds" />
    /// when the identity has a configured override; otherwise returns the global
    /// <see cref="TileOptions.CacheMaxAge" />.
    /// </summary>
    /// <param name="options">The tile options carrying the global default and any per-tileset overrides.</param>
    /// <param name="serviceId">Logical service identity for the request.</param>
    /// <param name="layerId">Layer (or collection) identity for the request.</param>
    /// <param name="tileMatrixSetId">Tile matrix set identity for the request.</param>
    /// <returns>The effective <c>max-age</c> in seconds.</returns>
    public static int Resolve(
        TileOptions options,
        string serviceId,
        string layerId,
        string tileMatrixSetId)
    {
        ArgumentNullException.ThrowIfNull(options);

        var key = BuildKey(serviceId, layerId, tileMatrixSetId);
        return Resolve(options, key);
    }

    /// <summary>
    /// Resolves the effective cache TTL (seconds) for an already-composed tileset
    /// key. Returns the per-tileset <see cref="TilesetCacheLifecycle.TtlSeconds" />
    /// when the key has a configured override; otherwise returns the global
    /// <see cref="TileOptions.CacheMaxAge" />.
    /// </summary>
    /// <param name="options">The tile options carrying the global default and any per-tileset overrides.</param>
    /// <param name="tilesetKey">The tileset-identity key, e.g. from <see cref="BuildKey(string, string, string)" />.</param>
    /// <returns>The effective <c>max-age</c> in seconds.</returns>
    public static int Resolve(TileOptions options, string tilesetKey)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (!string.IsNullOrEmpty(tilesetKey)
            && options.TilesetLifecycle is { Count: > 0 } lifecycle
            && lifecycle.TryGetValue(tilesetKey, out var entry)
            && entry?.TtlSeconds is { } ttl)
        {
            return ttl;
        }

        return options.CacheMaxAge;
    }
}
