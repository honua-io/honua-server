// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Buffers;
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
    // Tradeoff: callers consume the returned byte[] across async hops and ownership boundaries,
    // so we still allocate a sized byte[] for the result but pool the growing scratch buffer
    // (the unbounded intermediate that previously came from MemoryStream's internal doubling).
    private static byte[] DecompressZlib(byte[] compressedData)
    {
        // MemoryStream(byte[]) is a non-copying wrapper over the input, so no pooling needed there.
        using var input = new MemoryStream(compressedData);
        using var zlib = new ZLibStream(input, CompressionMode.Decompress);

        // Start with a buffer sized to the compressed input (decompressed output is typically
        // 2-4x larger, but we grow on demand from the pool rather than via MemoryStream doubling).
        var pool = ArrayPool<byte>.Shared;
        var scratch = pool.Rent(Math.Max(compressedData.Length * 2, 4096));
        var written = 0;
        try
        {
            while (true)
            {
                if (written == scratch.Length)
                {
                    var bigger = pool.Rent(scratch.Length * 2);
                    Buffer.BlockCopy(scratch, 0, bigger, 0, written);
                    pool.Return(scratch);
                    scratch = bigger;
                }

                var read = zlib.Read(scratch.AsSpan(written));
                if (read == 0)
                {
                    break;
                }
                written += read;
            }

            var result = new byte[written];
            Buffer.BlockCopy(scratch, 0, result, 0, written);
            return result;
        }
        finally
        {
            pool.Return(scratch);
        }
    }
}
