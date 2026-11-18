// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Raster.Domain;

namespace Honua.Core.Features.Raster.CogParser;

// Serves a WebMercatorQuad tile finer than a COG's native zoom from the ancestor tile. Each output
// pixel centre falls inside exactly one source pixel, so nearest-neighbour is exact for a
// power-of-two zoom difference and matches GDAL's nearest resampling of the same extent.
public static class CogTileOverzoom
{
    public static bool CanUpsample(CogMetadata metadata, int shift)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        return shift is > 0 and < 31
            && metadata.PlanarConfiguration == 1
            && metadata.BitsPerSample % 8 == 0
            && metadata.TileWidth >> shift > 0
            && metadata.TileHeight >> shift > 0
            && metadata.TileWidth % (1 << shift) == 0
            && metadata.TileHeight % (1 << shift) == 0;
    }

    public static byte[] Upsample(byte[] samples, CogMetadata metadata, CogOverzoomQuadrant quadrant)
    {
        ArgumentNullException.ThrowIfNull(samples);
        var (shift, subColumn, subRow) = quadrant;
        if (!CanUpsample(metadata, shift) || (uint)subColumn >= 1u << shift || (uint)subRow >= 1u << shift)
        {
            throw new ArgumentOutOfRangeException(nameof(quadrant), quadrant, "The quadrant cannot be upsampled from this tile layout.");
        }

        var width = metadata.TileWidth;
        var pixelBytes = metadata.BandCount * metadata.BitsPerSample / 8;
        if (samples.Length != checked((long)width * metadata.TileHeight * pixelBytes))
        {
            throw new InvalidDataException("Decoded COG tile length does not match its pixel layout.");
        }

        var originX = subColumn * (width >> shift);
        var originY = subRow * (metadata.TileHeight >> shift);
        var output = new byte[samples.Length];
        for (var y = 0; y < metadata.TileHeight; y++)
        {
            var sourceRow = (originY + (y >> shift)) * width;
            for (var x = 0; x < width; x++)
            {
                samples.AsSpan((sourceRow + originX + (x >> shift)) * pixelBytes, pixelBytes)
                    .CopyTo(output.AsSpan(((y * width) + x) * pixelBytes));
            }
        }

        return output;
    }
}
