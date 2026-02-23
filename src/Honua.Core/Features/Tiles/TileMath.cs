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
    /// Gets the bounding box for a tile in geographic coordinates (EPSG:4326)
    /// using the WorldCRS84Quad tile matrix set.
    /// At zoom 0 the world is covered by 2 tiles wide x 1 tile tall.
    /// </summary>
    /// <param name="x">Tile X coordinate</param>
    /// <param name="y">Tile Y coordinate</param>
    /// <param name="z">Zoom level</param>
    /// <returns>Bounding box as (xmin, ymin, xmax, ymax) in degrees</returns>
    public static TileBounds GetTileBoundsGeographic(int x, int y, int z)
    {
        // WorldCRS84Quad: at zoom 0, 2 cols x 1 row covering the full globe
        var numCols = (int)(2 * Math.Pow(2, z));
        var numRows = (int)Math.Pow(2, z);
        var tileWidth = 360.0 / numCols;
        var tileHeight = 180.0 / numRows;

        var xMin = -180.0 + x * tileWidth;
        var yMax = 90.0 - y * tileHeight;
        var xMax = xMin + tileWidth;
        var yMin = yMax - tileHeight;

        return new TileBounds(xMin, yMin, xMax, yMax);
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

    /// <summary>
    /// Validates tile coordinates for the WorldCRS84Quad tile matrix set.
    /// At zoom 0 the grid is 2 columns x 1 row.
    /// </summary>
    /// <param name="x">Tile X coordinate</param>
    /// <param name="y">Tile Y coordinate</param>
    /// <param name="z">Zoom level</param>
    /// <returns>True if coordinates are valid for WorldCRS84Quad</returns>
    public static bool ValidateTileCoordinatesGeographic(int x, int y, int z)
    {
        if (z is < 0 or > 22)
            return false;

        var maxCols = 2 * Math.Pow(2, z);
        var maxRows = Math.Pow(2, z);
        return x >= 0 && x < maxCols && y >= 0 && y < maxRows;
    }
}

/// <summary>
/// Represents a tile bounding box in map coordinates.
/// </summary>
/// <param name="XMin">Minimum X coordinate</param>
/// <param name="YMin">Minimum Y coordinate</param>
/// <param name="XMax">Maximum X coordinate</param>
/// <param name="YMax">Maximum Y coordinate</param>
public record TileBounds(double XMin, double YMin, double XMax, double YMax);
