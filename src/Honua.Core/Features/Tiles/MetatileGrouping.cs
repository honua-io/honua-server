// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System;
using System.Collections.Generic;

namespace Honua.Core.Features.Tiles;

/// <summary>
/// A single tile coordinate (zoom level, column, row) used by the metatiling grouping helper.
/// </summary>
/// <param name="Z">Tile matrix (zoom) level.</param>
/// <param name="X">Tile column index.</param>
/// <param name="Y">Tile row index.</param>
public readonly record struct TileIndex(int Z, int X, int Y);

/// <summary>
/// A metatile: an aligned N×N block of tiles at a single zoom level (#1837). The seed/render
/// path materializes one metatile per pass so the provider amortizes per-tile setup and reduces
/// edge artifacts at block seams. A metatile factor of <c>1</c> degenerates to one tile per
/// block, preserving the original per-tile behavior.
/// </summary>
/// <param name="Z">Tile matrix (zoom) level shared by every member tile.</param>
/// <param name="MinX">Inclusive minimum tile column in the block.</param>
/// <param name="MinY">Inclusive minimum tile row in the block.</param>
/// <param name="Tiles">The member tiles in the block (only those actually requested).</param>
public readonly record struct Metatile(int Z, int MinX, int MinY, IReadOnlyList<TileIndex> Tiles);

/// <summary>
/// Pure helper that partitions a set of tile coordinates into aligned N×N metatile blocks (#1837).
/// Grouping is deterministic and depends only on its inputs so it can be unit-tested in isolation
/// and reused by every seed/render adapter. The blocks are aligned to the metatile grid (a block's
/// origin column/row is a multiple of the factor) so adjacent requests share the same blocks and
/// the cache/seed work is not duplicated across calls.
/// </summary>
public static class MetatileGrouping
{
    /// <summary>
    /// Normalizes a configured metatile factor to a usable value (minimum <c>1</c>).
    /// </summary>
    /// <param name="factor">The configured factor (e.g. <see cref="TileOptions.MetatileFactor" />).</param>
    /// <returns>The effective factor, never less than <c>1</c>.</returns>
    public static int NormalizeFactor(int factor) => factor < 1 ? 1 : factor;

    /// <summary>
    /// Groups the supplied tiles into aligned N×N metatile blocks. Tiles are first partitioned by
    /// zoom level, then by metatile block origin (the tile column/row floored to the nearest
    /// multiple of <paramref name="factor" />). A <paramref name="factor" /> of <c>1</c> yields one
    /// tile per block. The block order is deterministic: ascending zoom, then ascending block
    /// origin row, then ascending block origin column, and member tiles ascend by row then column.
    /// </summary>
    /// <param name="tiles">The requested tile coordinates.</param>
    /// <param name="factor">The metatile factor (N); values below 1 are treated as 1.</param>
    /// <returns>The metatile blocks covering exactly the requested tiles.</returns>
    public static IReadOnlyList<Metatile> Group(IEnumerable<TileIndex> tiles, int factor)
    {
        ArgumentNullException.ThrowIfNull(tiles);

        var effectiveFactor = NormalizeFactor(factor);

        // Block key: (z, blockOriginX, blockOriginY). Use a sorted dictionary so the output order
        // is deterministic without a separate sort pass.
        var blocks = new SortedDictionary<(int Z, int BlockX, int BlockY), List<TileIndex>>();

        foreach (var tile in tiles)
        {
            var blockX = FloorToBlock(tile.X, effectiveFactor);
            var blockY = FloorToBlock(tile.Y, effectiveFactor);
            var key = (tile.Z, blockX, blockY);

            if (!blocks.TryGetValue(key, out var members))
            {
                members = new List<TileIndex>(effectiveFactor * effectiveFactor);
                blocks[key] = members;
            }

            members.Add(tile);
        }

        var result = new List<Metatile>(blocks.Count);
        foreach (var entry in blocks)
        {
            var members = entry.Value;
            members.Sort(static (a, b) => a.Y != b.Y ? a.Y.CompareTo(b.Y) : a.X.CompareTo(b.X));
            result.Add(new Metatile(entry.Key.Z, entry.Key.BlockX, entry.Key.BlockY, members));
        }

        return result;
    }

    private static int FloorToBlock(int coordinate, int factor)
    {
        // Floor toward negative infinity so negative coordinates (defensive) still align.
        var remainder = coordinate % factor;
        if (remainder < 0)
        {
            remainder += factor;
        }

        return coordinate - remainder;
    }
}
