using System.ComponentModel.DataAnnotations;

namespace Honua.Core.Configuration;

public class TileLimits
{
    public int MaxTilesPerRequest { get; set; } = 64;

    [Range(0, 25)]
    public int MaxZoomLevel { get; set; } = 18;

    [Range(0, 25)]
    public int MinZoomLevel { get; set; } = 0;

    [Range(1, long.MaxValue)]
    public long MaxTileSize { get; set; } = 512000;

    public TimeSpan TileGenerationTimeout { get; set; } = TimeSpan.FromSeconds(30);

    // Additional properties expected by tests
    [Range(0, 25)]
    public int MaxTileZoom
    {
        get => MaxZoomLevel;
        set => MaxZoomLevel = value;
    }

    [Range(0, 25)]
    public int MinTileZoom
    {
        get => MinZoomLevel;
        set => MinZoomLevel = value;
    }

    [Range(1, 100000)]
    public int MaxFeaturesPerTile { get; set; } = 50000;

    public TimeSpan TileTimeout
    {
        get => TileGenerationTimeout;
        set => TileGenerationTimeout = value;
    }
}
