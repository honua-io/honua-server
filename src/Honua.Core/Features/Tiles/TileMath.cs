// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Tiles;

/// <summary>
/// Utility class for tile coordinate and bounds calculations
/// </summary>
public static class TileMath
{
    /// <summary>
    /// Web Mercator bounds in meters
    /// </summary>
    private const double WebMercatorExtent = 20037508.342789244;

    /// <summary>
    /// Gets the bounding box for a tile in Web Mercator coordinates (EPSG:3857)
    /// </summary>
    /// <param name="x">Tile X coordinate</param>
    /// <param name="y">Tile Y coordinate</param>
    /// <param name="z">Zoom level</param>
    /// <returns>Bounding box as (xmin, ymin, xmax, ymax)</returns>
    public static TileBounds GetTileBounds(int x, int y, int z)
    {
        var tileSize = 2.0 * WebMercatorExtent / Math.Pow(2, z);
        var xMin = -WebMercatorExtent + x * tileSize;
        var yMax = WebMercatorExtent - y * tileSize;
        var xMax = xMin + tileSize;
        var yMin = yMax - tileSize;

        return new TileBounds(xMin, yMin, xMax, yMax);
    }

    /// <summary>
    /// Calculates geometry simplification tolerance based on zoom level
    /// </summary>
    /// <param name="zoom">Zoom level</param>
    /// <returns>Tolerance in meters for geometry simplification</returns>
    public static double GetSimplificationTolerance(int zoom)
    {
        // More aggressive simplification at lower zoom levels
        return zoom switch
        {
            <= 5 => 1000.0,   // 1km tolerance
            <= 8 => 500.0,    // 500m tolerance
            <= 10 => 100.0,   // 100m tolerance
            <= 12 => 50.0,    // 50m tolerance
            _ => 0.0           // No simplification at high zoom
        };
    }

    /// <summary>
    /// Validates tile coordinates are within valid ranges
    /// </summary>
    /// <param name="x">Tile X coordinate</param>
    /// <param name="y">Tile Y coordinate</param>
    /// <param name="z">Zoom level</param>
    /// <returns>True if coordinates are valid</returns>
    public static bool ValidateTileCoordinates(int x, int y, int z)
    {
        if (z is < 0 or > 22)
            return false;

        var maxTile = Math.Pow(2, z);
        return x >= 0 && x < maxTile && y >= 0 && y < maxTile;
    }
}

/// <summary>
/// Represents a tile bounding box in Web Mercator coordinates
/// </summary>
/// <param name="XMin">Minimum X coordinate</param>
/// <param name="YMin">Minimum Y coordinate</param>
/// <param name="XMax">Maximum X coordinate</param>
/// <param name="YMax">Maximum Y coordinate</param>
public record TileBounds(double XMin, double YMin, double XMax, double YMax);
