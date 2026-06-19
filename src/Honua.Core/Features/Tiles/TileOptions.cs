// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Tiles;

/// <summary>
/// Configuration options for tile rendering and caching.
/// Operational limits live in <see cref="Honua.Core.Configuration.LimitsOptions.Tiles" />.
/// </summary>
public sealed class TileOptions
{
    /// <summary>
    /// Configuration section name
    /// </summary>
    public const string SectionName = "TileOptions";

    /// <summary>
    /// Zoom level below which geometries are simplified (default: 10)
    /// </summary>
    public int SimplifyZoom { get; init; } = 10;

    /// <summary>
    /// Global default cache control max-age in seconds (default: 3600 = 1 hour).
    /// Used as the fallback TTL on the tile serve path when a tileset has no
    /// per-tileset override in <see cref="TilesetLifecycle" />.
    /// </summary>
    public int CacheMaxAge { get; init; } = 3600;

    /// <summary>
    /// Optional per-tileset cache lifecycle overrides, keyed by tileset identity.
    /// The key is built by <see cref="TilesetTtlResolver.BuildKey(string, string, string)" />
    /// from <c>serviceId</c>, <c>layerId</c>, and <c>tileMatrixSetId</c>. When a
    /// request resolves to a key present in this map, its
    /// <see cref="TilesetCacheLifecycle.TtlSeconds" /> overrides
    /// <see cref="CacheMaxAge" /> on the serve path. A <see langword="null" /> or
    /// empty map means every tileset uses the global <see cref="CacheMaxAge" />.
    /// </summary>
    public Dictionary<string, TilesetCacheLifecycle>? TilesetLifecycle { get; init; }

    /// <summary>
    /// MVT tile extent (default: 4096)
    /// </summary>
    public int TileExtent { get; init; } = 4096;

    /// <summary>
    /// MVT buffer size in pixels (default: 256)
    /// </summary>
    public int TileBuffer { get; init; } = 256;
}

/// <summary>
/// Per-tileset cache lifecycle configuration. Carries the cache-related
/// overrides for a single tileset identity (see
/// <see cref="TileOptions.TilesetLifecycle" />).
/// </summary>
/// <remarks>
/// Only serve-path TTL is honoured today. Size-quota / LRU eviction and
/// scheduled time-based invalidation are intentionally deferred (#1794); when
/// those seams land they will add further members here.
/// </remarks>
public sealed class TilesetCacheLifecycle
{
    /// <summary>
    /// Effective cache <c>max-age</c>, in seconds, advertised on the
    /// <c>Cache-Control</c> header for this tileset's served tiles. When
    /// <see langword="null" />, the resolver falls back to the global
    /// <see cref="TileOptions.CacheMaxAge" />.
    /// </summary>
    public int? TtlSeconds { get; init; }
}
