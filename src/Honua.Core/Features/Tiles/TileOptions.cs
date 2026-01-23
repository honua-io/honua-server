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
    /// Cache control max-age in seconds (default: 3600 = 1 hour)
    /// </summary>
    public int CacheMaxAge { get; init; } = 3600;

    /// <summary>
    /// MVT tile extent (default: 4096)
    /// </summary>
    public int TileExtent { get; init; } = 4096;

    /// <summary>
    /// MVT buffer size in pixels (default: 256)
    /// </summary>
    public int TileBuffer { get; init; } = 256;
}
