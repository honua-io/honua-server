// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Shared.Models;

namespace Honua.Protocols.GeoServices;

internal readonly record struct ExportTileCoordinate(int Z, int X, int Y);

// Web Mercator tile grid for MapServer and ImageServer exportTiles. The tile count is
// computed before any coordinate is allocated and the build stops at the caller's cap, so a
// wide extent at a high zoom never materialises the full grid.
internal static class ExportTileGrid
{
    internal static long Count(double[] bounds, IReadOnlyList<int> levels)
    {
        long total = 0;
        foreach (var z in levels)
        {
            var (xMin, xMax, yMin, yMax) = Range(bounds, z);
            total = checked(total + ((long)(xMax - xMin + 1) * (yMax - yMin + 1)));
        }

        return total;
    }

    // Lazy enumeration in level order; Take stops it at the cap, so no more than maxTiles
    // coordinates are ever allocated.
    internal static ExportTileCoordinate[] Build(double[] bounds, IReadOnlyList<int> levels, int maxTiles)
        => levels.SelectMany(z => LevelTiles(bounds, z)).Take(Math.Max(0, maxTiles)).ToArray();

    private static IEnumerable<ExportTileCoordinate> LevelTiles(double[] bounds, int z)
    {
        var (xMin, xMax, yMin, yMax) = Range(bounds, z);
        for (var x = xMin; x <= xMax; x++)
        {
            for (var y = yMin; y <= yMax; y++)
            {
                yield return new ExportTileCoordinate(z, x, y);
            }
        }
    }

    private static (int XMin, int XMax, int YMin, int YMax) Range(double[] bounds, int z)
    {
        var n = 1 << z;
        return (LonToTileX(bounds[0], n), LonToTileX(bounds[2], n), LatToTileY(bounds[3], n), LatToTileY(bounds[1], n));
    }

    private static int LonToTileX(double lon, int n)
    {
        var clampedLon = Math.Clamp(lon, -180d, 180d);
        var x = (int)Math.Floor((clampedLon + 180d) / 360d * n);
        return Math.Clamp(x, 0, n - 1);
    }

    private static int LatToTileY(double lat, int n)
    {
        var clampedLat = Math.Clamp(lat, -SpatialConstants.WebMercatorMaxLatitude, SpatialConstants.WebMercatorMaxLatitude);
        var latRad = clampedLat * Math.PI / 180d;
        var y = (int)Math.Floor(
            (1d - Math.Log(Math.Tan(latRad) + 1d / Math.Cos(latRad)) / Math.PI) / 2d * n);
        return Math.Clamp(y, 0, n - 1);
    }
}
