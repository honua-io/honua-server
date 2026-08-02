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
    public int SimplifyZoom { get; set; } = 10;

    /// <summary>
    /// Global default cache control max-age in seconds (default: 3600 = 1 hour).
    /// Used as the fallback TTL on the tile serve path when a tileset has no
    /// per-tileset override in <see cref="TilesetLifecycle" />.
    /// </summary>
    public int CacheMaxAge { get; set; } = 3600;

    /// <summary>
    /// Optional per-tileset cache lifecycle overrides, keyed by tileset identity.
    /// The key is built by <see cref="TilesetTtlResolver.BuildKey(string, string, string)" />
    /// from <c>serviceId</c>, <c>layerId</c>, and <c>tileMatrixSetId</c>. When a
    /// request resolves to a key present in this map, its
    /// <see cref="TilesetCacheLifecycle.TtlSeconds" /> overrides
    /// <see cref="CacheMaxAge" /> on the serve path. A <see langword="null" /> or
    /// empty map means every tileset uses the global <see cref="CacheMaxAge" />.
    /// </summary>
    public Dictionary<string, TilesetCacheLifecycle>? TilesetLifecycle { get; set; }

    /// <summary>
    /// MVT tile extent (default: 4096)
    /// </summary>
    public int TileExtent { get; set; } = 4096;

    /// <summary>
    /// MVT buffer size in pixels (default: 256)
    /// </summary>
    public int TileBuffer { get; set; } = 256;

    /// <summary>
    /// Metatile factor (N): the seed/render path renders tiles in N×N metatile blocks per pass
    /// instead of one tile at a time (#1837). A factor of <c>1</c> (the default) disables
    /// metatiling and preserves the per-tile behavior; larger factors group neighbouring tiles
    /// so the provider amortizes per-tile setup and reduces edge artifacts at block seams. Values
    /// below <c>1</c> are treated as <c>1</c>.
    /// </summary>
    public int MetatileFactor { get; set; } = 1;

    /// <summary>
    /// Size-quota / LRU eviction policy for the tile cache (#1837). When
    /// <see cref="TileCacheEvictionOptions.Enabled" /> is <see langword="false" /> (the default)
    /// no eviction is applied and the cache grows under the configured TTLs only.
    /// </summary>
    public TileCacheEvictionOptions Eviction { get; set; } = new();
}

/// <summary>
/// Size-quota / LRU eviction policy for the tile cache key index (#1837). The policy is a pure,
/// declarative description of when cached tiles should be evicted; an evictor consults it against
/// the current cache footprint (entry count / byte size) to decide how many least-recently-used
/// tile entries to drop.
/// </summary>
public sealed class TileCacheEvictionOptions
{
    /// <summary>
    /// Whether size-quota / LRU eviction is enabled. Defaults to <see langword="false" /> so the
    /// baseline serve path is unchanged (TTL-only) until an operator opts in.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Maximum number of cached tile entries before least-recently-used entries are evicted.
    /// <c>null</c> or a non-positive value means "no entry-count cap".
    /// </summary>
    public long? MaxEntries { get; set; }

    /// <summary>
    /// Maximum total cached tile bytes before least-recently-used entries are evicted.
    /// <c>null</c> or a non-positive value means "no byte-size cap".
    /// </summary>
    public long? MaxBytes { get; set; }
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
    public int? TtlSeconds { get; set; }
}
