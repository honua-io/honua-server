// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Tiles.PMTiles;

/// <summary>
/// Hilbert curve mapping for PMTiles v3 tile IDs.
/// Converts (z, x, y) tile coordinates to a single Hilbert curve tile ID
/// per the PMTiles specification.
/// </summary>
public static class HilbertCurve
{
    /// <summary>
    /// Converts tile coordinates to a PMTiles v3 Hilbert tile ID.
    /// The tile ID is the position on the Hilbert curve at zoom level z,
    /// offset by the cumulative tile count of all preceding zoom levels.
    /// </summary>
    /// <param name="z">Zoom level (0-26).</param>
    /// <param name="x">Tile column.</param>
    /// <param name="y">Tile row.</param>
    /// <returns>The Hilbert tile ID.</returns>
    public static ulong XYZToTileId(int z, int x, int y)
    {
        if (z == 0)
        {
            return 0;
        }

        ArgumentOutOfRangeException.ThrowIfNegative(z);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(z, 26);
        var n = 1 << z;
        ArgumentOutOfRangeException.ThrowIfNegative(x);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(x, n);
        ArgumentOutOfRangeException.ThrowIfNegative(y);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(y, n);

        var offset = CumulativeTileCount(z);
        var hilbertIndex = XYToHilbert(n, x, y);
        return offset + (ulong)hilbertIndex;
    }

    /// <summary>
    /// Converts a PMTiles v3 Hilbert tile ID back to (z, x, y) coordinates.
    /// </summary>
    /// <param name="tileId">The Hilbert tile ID.</param>
    /// <returns>A tuple of (z, x, y) coordinates.</returns>
    public static (int Z, int X, int Y) TileIdToXYZ(ulong tileId)
    {
        if (tileId == 0)
        {
            return (0, 0, 0);
        }

        var z = 0;
        var acc = 1UL; // zoom 0 occupies tile ID 0
        for (var i = 1; i <= 26; i++)
        {
            var count = 1UL << (2 * i);
            if (acc + count > tileId)
            {
                z = i;
                break;
            }
            acc += count;
        }

        var n = 1 << z;
        var hilbertIndex = (long)(tileId - acc);
        var (x, y) = HilbertToXY(n, hilbertIndex);
        return (z, x, y);
    }

    private static ulong CumulativeTileCount(int z)
    {
        // Sum of 4^i for i=0..(z-1) = (4^z - 1) / 3
        // But tile ID 0 is zoom 0, so offset = 1 + sum(4^i for i=1..z-1)
        var acc = 0UL;
        for (var i = 0; i < z; i++)
        {
            acc += 1UL << (2 * i);
        }
        return acc;
    }

    private static long XYToHilbert(int n, int x, int y)
    {
        var d = 0L;
        var rx = 0;
        var ry = 0;
        var tmpX = x;
        var tmpY = y;

        for (var s = n / 2; s > 0; s /= 2)
        {
            rx = (tmpX & s) > 0 ? 1 : 0;
            ry = (tmpY & s) > 0 ? 1 : 0;
            d += (long)s * s * ((3 * rx) ^ ry);
            Rotate(s, ref tmpX, ref tmpY, rx, ry);
        }

        return d;
    }

    private static (int X, int Y) HilbertToXY(int n, long d)
    {
        var x = 0;
        var y = 0;
        var rx = 0;
        var ry = 0;
        var t = d;

        for (var s = 1; s < n; s *= 2)
        {
            rx = (int)(1 & (t / 2));
            ry = (int)(1 & (t ^ rx));
            Rotate(s, ref x, ref y, rx, ry);
            x += s * rx;
            y += s * ry;
            t /= 4;
        }

        return (x, y);
    }

    private static void Rotate(int n, ref int x, ref int y, int rx, int ry)
    {
        if (ry != 0)
        {
            return;
        }

        if (rx == 1)
        {
            x = n - 1 - x;
            y = n - 1 - y;
        }

        (x, y) = (y, x);
    }
}
