// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.IO.Compression;

namespace Honua.Core.Features.Raster.CogParser;

/// <summary>
/// Decompresses COG tile data based on the compression type.
/// Supports JPEG passthrough (zero-copy) and DEFLATE via <see cref="ZLibStream"/>.
/// LZW and ZSTD are deferred to follow-up work.
/// </summary>
public static class TileDecompressor
{
    /// <summary>
    /// Decompresses tile data and returns the content type for the response.
    /// JPEG tiles are standalone images and served directly (zero-copy passthrough).
    /// DEFLATE and NONE tiles contain raw pixel data (not a valid image file);
    /// they are returned as <c>application/octet-stream</c> because re-encoding
    /// into a renderable image format requires band/dimension context that is
    /// outside this component's scope.
    /// </summary>
    /// <param name="tileData">Raw compressed tile bytes from the COG</param>
    /// <param name="compression">TIFF compression name (JPEG, DEFLATE, NONE, etc.)</param>
    /// <returns>Decompressed data and the appropriate content type</returns>
    public static (byte[] Data, string ContentType) Decompress(byte[] tileData, string compression)
    {
        return compression switch
        {
            "JPEG" => (tileData, "image/jpeg"), // Zero-copy passthrough — tile is a standalone JPEG
            "DEFLATE" => (DecompressZlib(tileData), "application/octet-stream"),
            "NONE" or "" => (tileData, "application/octet-stream"),
            _ => throw new NotSupportedException(
                $"COG tile compression '{compression}' is not supported. Supported: JPEG (passthrough), DEFLATE, NONE.")
        };
    }

    /// <summary>
    /// Returns true if the compression type is supported for direct serving.
    /// </summary>
    public static bool IsSupported(string compression) => compression is "JPEG" or "DEFLATE" or "NONE" or "";

    /// <summary>
    /// TIFF DEFLATE compression uses zlib (RFC 1950) wrapping, not raw DEFLATE (RFC 1951).
    /// GDAL and all major COG producers write zlib-wrapped data for compression codes 8 and 32946.
    /// </summary>
    private static byte[] DecompressZlib(byte[] compressedData)
    {
        using var input = new MemoryStream(compressedData);
        using var zlib = new ZLibStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        zlib.CopyTo(output);
        return output.ToArray();
    }
}
