// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Buffers;
using System.IO.Compression;

namespace Honua.Core.Features.Raster.CogParser;

public static class TileDecompressor
{
    public const int DefaultMaxDecompressedBytes = 128 * 1024 * 1024;

    private static readonly string[] SupportedCodecs = ["JPEG", "DEFLATE", "LZW", "ZSTD", "NONE"];

    public static (byte[] Data, string ContentType) Decompress(
        byte[] tileData,
        string compression,
        int maxDecompressedBytes = DefaultMaxDecompressedBytes)
        => Decompress(tileData, compression, TilePixelLayout.None, maxDecompressedBytes);

    // JPEG is passed through only after shared tables are assembled; pixel codecs stay raw bytes, and a predictor on JPEG is ignored.
    public static (byte[] Data, string ContentType) Decompress(
        byte[] tileData,
        string compression,
        in TilePixelLayout layout,
        int maxDecompressedBytes = DefaultMaxDecompressedBytes)
    {
        switch (compression)
        {
            case "JPEG":
                return (tileData, "image/jpeg"); // Zero-copy passthrough of an already-assembled JPEG stream

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

    // TIFF DEFLATE is zlib-wrapped, not raw DEFLATE.
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
