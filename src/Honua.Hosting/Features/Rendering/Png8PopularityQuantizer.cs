// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Infrastructure.Rendering;

// Palette for images with more than 256 distinct RGBA values. Pixels are binned at
// 4 bits per colour channel and 3 bits of alpha, the 255 most used bins become the
// palette (their mean colours), and index 0 stays fully transparent. Alpha is binned
// like colour, so semi-transparent fills and anti-aliased edges keep their opacity.
internal static class Png8PopularityQuantizer
{
    private const int BinCount = 1 << 15;
    private const int MaxOpaqueOrPartialEntries = 255;

    internal static (uint[] Palette, byte[] Indices) Quantize(byte[] rgba, byte[] indices)
    {
        var bins = BinStats.Accumulate(rgba);
        var palette = SelectPalette(bins);
        var binToIndex = new int[BinCount];
        Array.Fill(binToIndex, -1);

        for (var i = 0; i < indices.Length; i++)
        {
            var offset = i * 4;
            if (rgba[offset + 3] == 0)
            {
                indices[i] = 0;
                continue;
            }

            var bin = BinOf(rgba, offset);
            if (binToIndex[bin] < 0)
            {
                binToIndex[bin] = NearestIndex(palette, bins.MeanColor(bin));
            }

            indices[i] = (byte)binToIndex[bin];
        }

        return (palette, indices);
    }

    private static int BinOf(byte[] rgba, int offset)
        => ((rgba[offset] >> 4) << 11)
            | ((rgba[offset + 1] >> 4) << 7)
            | ((rgba[offset + 2] >> 4) << 3)
            | (rgba[offset + 3] >> 5);

    private static uint[] SelectPalette(BinStats bins)
    {
        var chosen = Enumerable.Range(0, BinCount)
            .Where(bin => bins.Counts[bin] > 0)
            .OrderByDescending(bin => bins.Counts[bin])
            .ThenBy(bin => bin)
            .Take(MaxOpaqueOrPartialEntries)
            .Select(bins.MeanColor);
        return [0u, .. chosen];
    }

    private static int NearestIndex(uint[] palette, uint color)
    {
        var best = 0;
        var bestDistance = long.MaxValue;
        for (var i = 0; i < palette.Length; i++)
        {
            var distance = Distance(palette[i], color);
            if (distance < bestDistance)
            {
                best = i;
                bestDistance = distance;
            }
        }

        return best;
    }

    private static long Distance(uint left, uint right)
    {
        long Channel(int shift) => ((left >> shift) & 0xFF) - (long)((right >> shift) & 0xFF);
        var (r, g, b, a) = (Channel(0), Channel(8), Channel(16), Channel(24));

        // Alpha errors are weighted double: a wrong opacity is more visible on a map than a small hue shift.
        return (r * r) + (g * g) + (b * b) + (2 * a * a);
    }

    private sealed class BinStats
    {
        internal int[] Counts { get; } = new int[BinCount];

        private long[] Sums { get; } = new long[BinCount * 4];

        internal static BinStats Accumulate(byte[] rgba)
        {
            var stats = new BinStats();
            for (var offset = 0; offset < rgba.Length; offset += 4)
            {
                if (rgba[offset + 3] == 0)
                {
                    continue;
                }

                var bin = BinOf(rgba, offset);
                stats.Counts[bin]++;
                for (var channel = 0; channel < 4; channel++)
                {
                    stats.Sums[(bin * 4) + channel] += rgba[offset + channel];
                }
            }

            return stats;
        }

        internal uint MeanColor(int bin)
        {
            var count = Math.Max(1, Counts[bin]);
            uint Mean(int channel) => (uint)((Sums[(bin * 4) + channel] + (count / 2)) / count);
            return Mean(0) | (Mean(1) << 8) | (Mean(2) << 16) | (Mean(3) << 24);
        }
    }
}
