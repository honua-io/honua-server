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
    /// Default ceiling for decompressed tile output. A legitimate COG tile is bounded by
    /// tileWidth * tileHeight * bands * bytesPerSample (a 1024x1024 tile with 16 float64 bands
    /// is 128 MiB), so anything beyond this is a malformed or hostile (decompression-bomb) tile.
    /// </summary>
    public const int DefaultMaxDecompressedBytes = 128 * 1024 * 1024;

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
    /// <param name="maxDecompressedBytes">
    /// Maximum allowed decompressed size; pass the tile's expected pixel-buffer size when known.
    /// Exceeding it throws <see cref="InvalidDataException"/> (decompression-bomb guard).
    /// </param>
    /// <returns>Decompressed data and the appropriate content type</returns>
    public static (byte[] Data, string ContentType) Decompress(
        byte[] tileData,
        string compression,
        int maxDecompressedBytes = DefaultMaxDecompressedBytes)
    {
        return compression switch
        {
            "JPEG" => (tileData, "image/jpeg"), // Zero-copy passthrough — tile is a standalone JPEG
            "DEFLATE" => (DecompressZlib(tileData, maxDecompressedBytes), "application/octet-stream"),
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
    private static byte[] DecompressZlib(byte[] compressedData, int maxDecompressedBytes)
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
                    if (written >= maxDecompressedBytes)
                    {
                        throw new InvalidDataException(
                            $"DEFLATE tile decompressed beyond the {maxDecompressedBytes}-byte limit; refusing to inflate further (possible decompression bomb).");
                    }

                    var bigger = pool.Rent((int)Math.Min(scratch.Length * 2L, maxDecompressedBytes));
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

            if (written > maxDecompressedBytes)
            {
                throw new InvalidDataException(
                    $"DEFLATE tile decompressed to {written} bytes, exceeding the {maxDecompressedBytes}-byte limit (possible decompression bomb).");
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
