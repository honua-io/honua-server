// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.IO.Compression;

namespace Honua.Core.Features.Infrastructure.IO;

// The single place Honua calls a Zstandard implementation. COG raster tiles and HTTP response
// encoding go through this type, so moving to the runtime's built-in Zstandard support changes
// only this file.
public static class ZstdCodec
{
    public const int OptimalLevel = 3;

    public const int SmallestSizeLevel = 19;

    public static int ToZstdLevel(CompressionLevel level) => level switch
    {
        CompressionLevel.Fastest => 1,
        CompressionLevel.SmallestSize => SmallestSizeLevel,
        _ => OptimalLevel,
    };

    // The frame's declared content size, or an upper bound from its block count when it declares
    // none. It is untrusted input: bound it before using it to size an allocation.
    public static ulong GetDecompressedBound(ReadOnlySpan<byte> compressed)
        => ZstdSharp.Decompressor.GetDecompressedSize(compressed);

    public static byte[] Decompress(ReadOnlySpan<byte> compressed, int maxDecompressedBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxDecompressedBytes);
        try
        {
            var bound = GetDecompressedBound(compressed);

            // An inflated header is a decompression-bomb vector: check the bound before allocating.
            if (bound > (ulong)maxDecompressedBytes)
            {
                throw new InvalidDataException(
                    $"ZSTD frame may decompress to {bound} bytes, exceeding the {maxDecompressedBytes}-byte limit (possible decompression bomb).");
            }

            var buffer = new byte[(int)bound];
            using var decompressor = new ZstdSharp.Decompressor();
            var written = decompressor.Unwrap(compressed, buffer);
            return written == buffer.Length ? buffer : buffer.AsSpan(0, written).ToArray();
        }
        catch (ZstdSharp.ZstdException ex)
        {
            throw new InvalidDataException("ZSTD payload is corrupt.", ex);
        }
    }

    public static byte[] Compress(ReadOnlySpan<byte> data, CompressionLevel level)
    {
        using var compressor = new ZstdSharp.Compressor(ToZstdLevel(level));
        return compressor.Wrap(data).ToArray();
    }

    // The caller keeps ownership of output: disposing the returned stream finishes the frame only.
    public static Stream CreateCompressionStream(Stream output, CompressionLevel level)
        => new ZstdSharp.CompressionStream(output, ToZstdLevel(level), leaveOpen: true);
}
