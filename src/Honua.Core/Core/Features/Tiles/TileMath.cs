// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System;
using Honua.Core.Features.Shared.Models;

namespace Honua.Core.Features.Tiles;

public static class TileMath
{
    private const double WebMercatorExtent = SpatialConstants.WebMercatorExtent;

    public const int MaxSupportedZoomLevel = 24;

    public static TileBounds GetTileBounds(int x, int y, int z)
    {
        var tileSize = 2.0 * WebMercatorExtent / Math.Pow(2, z);
        var xMin = -WebMercatorExtent + x * tileSize;
        var yMax = WebMercatorExtent - y * tileSize;
        var xMax = xMin + tileSize;
        var yMin = yMax - tileSize;

        return new TileBounds(xMin, yMin, xMax, yMax);
    }

    public static TileBounds? GetTileBounds(GridGeometry geometry, int x, int y, int z)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        return geometry.GetTileBounds(x, y, z);
    }

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

    public static bool ValidateTileCoordinates(int x, int y, int z)
    {
        if (z is < 0 or > MaxSupportedZoomLevel)
            return false;

        var maxTile = Math.Pow(2, z);
        return x >= 0 && x < maxTile && y >= 0 && y < maxTile;
    }

    public static bool ValidateTileCoordinatesGeographic(int x, int y, int z)
    {
        if (z is < 0 or > MaxSupportedZoomLevel)
            return false;

        var maxCols = 2 * Math.Pow(2, z);
        var maxRows = Math.Pow(2, z);
        return x >= 0 && x < maxCols && y >= 0 && y < maxRows;
    }
}

public record TileBounds(double XMin, double YMin, double XMax, double YMax);
