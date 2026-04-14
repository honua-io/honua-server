using System.ComponentModel.DataAnnotations;

namespace Honua.Core.Configuration;

/// <summary>
/// Limits for tile-based operations to prevent resource exhaustion.
/// </summary>
public class TileLimits
{
    /// <summary>
    /// Maximum number of tiles that can be requested in a single operation.
    /// </summary>
    public int MaxTilesPerRequest { get; set; } = 64;

    /// <summary>
    /// Maximum zoom level allowed for tile requests.
    /// </summary>
    [Range(0, 25)]
    public int MaxZoomLevel { get; set; } = 18;

    /// <summary>
    /// Minimum zoom level allowed for tile requests.
    /// </summary>
    [Range(0, 25)]
    public int MinZoomLevel { get; set; } = 0;

    /// <summary>
    /// Maximum tile size in pixels for rendered tiles.
    /// </summary>
    [Range(1, long.MaxValue)]
    public long MaxTileSize { get; set; } = 512;

    /// <summary>
    /// Timeout for tile generation operations.
    /// </summary>
    public TimeSpan TileGenerationTimeout { get; set; } = TimeSpan.FromSeconds(30);

    // Additional properties expected by tests
    /// <summary>
    /// Maximum tile zoom level (alias for MaxZoomLevel for test compatibility).
    /// </summary>
    [Range(0, 25)]
    public int MaxTileZoom
    {
        get => MaxZoomLevel;
        set => MaxZoomLevel = value;
    }

    /// <summary>
    /// Minimum tile zoom level (alias for MinZoomLevel for test compatibility).
    /// </summary>
    [Range(0, 25)]
    public int MinTileZoom
    {
        get => MinZoomLevel;
        set => MinZoomLevel = value;
    }

    /// <summary>
    /// Maximum features per tile.
    /// </summary>
    [Range(1, 100000)]
    public int MaxFeaturesPerTile { get; set; } = 50000;

    /// <summary>
    /// Tile timeout (alias for TileGenerationTimeout for test compatibility).
    /// </summary>
    public TimeSpan TileTimeout
    {
        get => TileGenerationTimeout;
        set => TileGenerationTimeout = value;
    }
}
