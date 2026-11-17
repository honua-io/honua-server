// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Raster.Domain;

namespace Honua.Server.Features.Protocols.Cog;

// The stored tile that answers a request, and the window of it to enlarge when the request is
// deeper than the COG's full-resolution zoom. ZoomDelta 0 is a stored tile served as is.
internal sealed record CogTileWindow
{
    public required int TileIndex { get; init; }

    public int ZoomDelta { get; init; }

    public int OffsetX { get; init; }

    public int OffsetY { get; init; }
}

// Requests deeper than the full-resolution level of a WebMercatorQuad-aligned COG are answered
// from the ancestor tile at that level: its quadrant window is enlarged with nearest-neighbour
// sampling, so every output pixel is a stored sample (no interpolated values). Six levels keep the
// window at four or more source pixels for a 256-pixel tile and bound the ancestor search.
internal static class CogOverzoomTile
{
    internal const int MaxZoomDelta = 6;

    internal static CogTileWindow? CreateWindow(CogMetadata metadata, int ancestorTileIndex, (int ZoomDelta, int Row, int Col) request)
    {
        var factor = 1 << request.ZoomDelta;
        if (metadata.TileWidth != metadata.TileHeight || metadata.TileWidth % factor != 0)
        {
            return null;
        }

        var windowSize = metadata.TileWidth / factor;
        return new CogTileWindow
        {
            TileIndex = ancestorTileIndex,
            ZoomDelta = request.ZoomDelta,
            OffsetX = (request.Col & (factor - 1)) * windowSize,
            OffsetY = (request.Row & (factor - 1)) * windowSize
        };
    }

    // Enlarges the window of a decoded, pixel-interleaved tile to the full tile size. Returns null
    // for a layout this path cannot address sample by sample.
    internal static byte[]? Enlarge(byte[] samples, CogMetadata metadata, CogTileWindow window)
    {
        if (metadata.PlanarConfiguration != 1 || metadata.BitsPerSample % 8 != 0 || metadata.BitsPerSample <= 0)
        {
            return null;
        }

        var pixelBytes = metadata.BandCount * (metadata.BitsPerSample / 8);
        var size = metadata.TileWidth;
        if (pixelBytes <= 0 || samples.Length != (long)size * size * pixelBytes)
        {
            return null;
        }

        var factor = 1 << window.ZoomDelta;
        var output = new byte[samples.Length];
        var rowBytes = size * pixelBytes;
        for (var y = 0; y < size; y++)
        {
            var sourceRow = (window.OffsetY + (y / factor)) * rowBytes;
            for (var x = 0; x < size; x++)
            {
                var source = sourceRow + ((window.OffsetX + (x / factor)) * pixelBytes);
                Buffer.BlockCopy(samples, source, output, (y * rowBytes) + (x * pixelBytes), pixelBytes);
            }
        }

        return output;
    }
}
