// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Tiles;

/// <summary>
/// Configuration options for tile generation
/// </summary>
public sealed class TileOptions
{
    /// <summary>
    /// Configuration section name
    /// </summary>
    public const string SectionName = "TileOptions";

    /// <summary>
    /// Maximum features per tile (default: 10,000)
    /// </summary>
    public int MaxFeaturesPerTile { get; init; } = 10_000;

    /// <summary>
    /// Query timeout for tile generation in seconds (default: 10)
    /// </summary>
    public int TileTimeoutSeconds { get; init; } = 10;

    /// <summary>
    /// Zoom level below which geometries are simplified (default: 10)
    /// </summary>
    public int SimplifyZoom { get; init; } = 10;

    /// <summary>
    /// Minimum supported zoom level (default: 0)
    /// </summary>
    public int MinZoom { get; init; }

    /// <summary>
    /// Maximum supported zoom level (default: 22)
    /// </summary>
    public int MaxZoom { get; init; } = 22;

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
