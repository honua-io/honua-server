// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Buffers;
using System.IO.Compression;

namespace Honua.Core.Features.Raster.CogParser;

/// <summary>
/// Decompresses COG tile data based on the compression type.
/// Supports JPEG passthrough (zero-copy), DEFLATE via <see cref="ZLibStream"/>,
/// TIFF LZW, ZSTD, and NONE, reversing the horizontal differencing predictor
/// (TIFF tag 317 = 2) when the tile declares one.
/// </summary>
public static class TileDecompressor
{
    /// <summary>
    /// Default ceiling for decompressed tile output. A legitimate COG tile is bounded by
    /// tileWidth * tileHeight * bands * bytesPerSample (a 1024x1024 tile with 16 float64 bands
    /// is 128 MiB), so anything beyond this is a malformed or hostile (decompression-bomb) tile.
    /// </summary>
    public const int DefaultMaxDecompressedBytes = 128 * 1024 * 1024;

    private static readonly string[] SupportedCodecs = ["JPEG", "DEFLATE", "LZW", "ZSTD", "NONE"];

    /// <summary>
    /// Decompresses tile data and returns the content type for the response.
    /// Equivalent to calling <see cref="Decompress(byte[], string, in TilePixelLayout, int)"/>
    /// with <see cref="TilePixelLayout.None"/>; use that overload for tiles that declare a predictor.
    /// </summary>
    /// <param name="tileData">Raw compressed tile bytes from the COG</param>
    /// <param name="compression">TIFF compression name (JPEG, DEFLATE, LZW, ZSTD, NONE)</param>
    /// <param name="maxDecompressedBytes">
    /// Maximum allowed decompressed size; pass the tile's expected pixel-buffer size when known.
    /// Exceeding it throws <see cref="InvalidDataException"/> (decompression-bomb guard).
    /// </param>
    /// <returns>Decompressed data and the appropriate content type</returns>
    public static (byte[] Data, string ContentType) Decompress(
        byte[] tileData,
        string compression,
        int maxDecompressedBytes = DefaultMaxDecompressedBytes)
        => Decompress(tileData, compression, TilePixelLayout.None, maxDecompressedBytes);

    /// <summary>
    /// Decompresses tile data and returns the content type for the response.
    /// JPEG tiles are standalone images and served directly (zero-copy passthrough).
    /// DEFLATE, LZW, ZSTD, and NONE tiles contain raw pixel data (not a valid image file);
    /// they are returned as <c>application/octet-stream</c> because re-encoding
    /// into a renderable image format requires band/dimension context that is
    /// outside this component's scope.
    /// </summary>
    /// <param name="tileData">Raw compressed tile bytes from the COG</param>
    /// <param name="compression">TIFF compression name (JPEG, DEFLATE, LZW, ZSTD, NONE)</param>
    /// <param name="layout">
    /// Pixel geometry of the tile, used to reverse the TIFF predictor. Predictors apply to the
    /// pixel-data codecs only; a predictor declared alongside JPEG is ignored, matching libtiff.
    /// </param>
    /// <param name="maxDecompressedBytes">
    /// Maximum allowed decompressed size; pass the tile's expected pixel-buffer size when known.
    /// Exceeding it throws <see cref="InvalidDataException"/> (decompression-bomb guard).
    /// </param>
    /// <returns>Decompressed data and the appropriate content type</returns>
    /// <exception cref="UnsupportedTileCodecException">The tile's codec cannot be decoded.</exception>
    /// <exception cref="UnsupportedTilePredictorException">The tile's predictor cannot be reversed.</exception>
    public static (byte[] Data, string ContentType) Decompress(
        byte[] tileData,
        string compression,
        in TilePixelLayout layout,
        int maxDecompressedBytes = DefaultMaxDecompressedBytes)
    {
        switch (compression)
        {
            case "JPEG":
                return (tileData, "image/jpeg"); // Zero-copy passthrough — tile is a standalone JPEG

            case "NONE" or "":
                // NONE tiles are already pixel data; a predictor still has to be reversed, but the
                // caller owns tileData so an in-place pass would mutate their buffer.
                return (ApplyPredictor(tileData, layout, copyFirst: true), "application/octet-stream");

            case "DEFLATE":
                return (ApplyPredictor(DecompressZlib(tileData, maxDecompressedBytes), layout, copyFirst: false),
                    "application/octet-stream");

            case "LZW":
                return (ApplyPredictor(
                        LzwDecoder.Decode(tileData, maxDecompressedBytes, ExpectedTileBytes(layout, maxDecompressedBytes)),
                        layout,
                        copyFirst: false),
                    "application/octet-stream");

            case "ZSTD":
                return (ApplyPredictor(DecompressZstd(tileData, maxDecompressedBytes), layout, copyFirst: false),
                    "application/octet-stream");

            default:
                throw new UnsupportedTileCodecException(compression, SupportedCodecs);
        }
    }

    /// <summary>
    /// Returns true if the compression type is supported for direct serving.
    /// </summary>
    public static bool IsSupported(string compression)
        => compression is "JPEG" or "DEFLATE" or "LZW" or "ZSTD" or "NONE" or "";

    private static byte[] ApplyPredictor(byte[] pixels, in TilePixelLayout layout, bool copyFirst)
    {
        if (!layout.HasPredictor)
        {
            return pixels;
        }

        var target = copyFirst ? (byte[])pixels.Clone() : pixels;
        TilePredictor.Undo(target, layout);
        return target;
    }

    /// <summary>
    /// Size of a fully-populated tile's pixel buffer, or 0 when the layout does not describe one.
    /// TIFF pads edge tiles to the full tile geometry, so this is exact for every tile in a level.
    /// </summary>
    private static int ExpectedTileBytes(in TilePixelLayout layout, int maxDecompressedBytes)
    {
        if (layout.TileWidth <= 0 || layout.SamplesPerPixel <= 0 || layout.BitsPerSample <= 0)
        {
            return 0;
        }

        // TileHeight is not carried on the layout; the row stride alone is enough to beat the
        // default growth heuristic without over-renting for tiles of unknown height.
        var rowStride = (long)layout.TileWidth * layout.SamplesPerPixel * (layout.BitsPerSample / 8);
        var estimate = rowStride * layout.TileWidth;
        return estimate is <= 0 or > int.MaxValue ? 0 : (int)Math.Min(estimate, maxDecompressedBytes);
    }

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

    /// <summary>
    /// TIFF ZSTD (compression code 50000) stores a single zstd frame per tile.
    /// GDAL writes the frame's content size in its header, so the common path sizes the
    /// output buffer exactly; frames without it fall back to the streaming decoder.
    /// </summary>
    private static byte[] DecompressZstd(byte[] compressedData, int maxDecompressedBytes)
    {
        var declaredSize = ZstdSharp.Decompressor.GetDecompressedSize(compressedData);

        // Trust the declared size only as an allocation hint after bounds-checking it — it is
        // attacker-controlled file content, and an inflated value is a decompression-bomb vector.
        if (declaredSize > (ulong)maxDecompressedBytes)
        {
            throw new InvalidDataException(
                $"ZSTD tile declares a {declaredSize}-byte frame, exceeding the {maxDecompressedBytes}-byte limit (possible decompression bomb).");
        }

        using var decompressor = new ZstdSharp.Decompressor();

        if (declaredSize > 0)
        {
            var exact = new byte[(int)declaredSize];
            var written = decompressor.Unwrap(compressedData, exact, offset: 0);
            if (written == exact.Length)
            {
                return exact;
            }

            var trimmed = new byte[written];
            Buffer.BlockCopy(exact, 0, trimmed, 0, written);
            return trimmed;
        }

        var decompressed = decompressor.Unwrap(compressedData, maxDecompressedBytes);
        return decompressed.ToArray();
    }
}
