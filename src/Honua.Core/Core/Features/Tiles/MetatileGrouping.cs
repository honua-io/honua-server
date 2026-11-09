// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System;
using System.Collections.Generic;

namespace Honua.Core.Features.Tiles;

public readonly record struct TileIndex(int Z, int X, int Y);

public readonly record struct Metatile(int Z, int MinX, int MinY, IReadOnlyList<TileIndex> Tiles);

public static class MetatileGrouping
{
    public static int NormalizeFactor(int factor) => factor < 1 ? 1 : factor;

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
