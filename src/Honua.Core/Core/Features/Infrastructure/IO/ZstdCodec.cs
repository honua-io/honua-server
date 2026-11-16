// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

#if NET11_0_OR_GREATER
using System.Buffers;
#endif
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
#if NET11_0_OR_GREATER
        => ZstandardDecoder.TryGetMaxDecompressedLength(compressed, out var length) && length >= 0
            ? (ulong)length
            : throw new InvalidDataException("ZSTD payload is corrupt.");
#else
    {
        try
        {
            return ZstdSharp.Decompressor.GetDecompressedSize(compressed);
        }
        catch (ZstdSharp.ZstdException ex)
        {
            throw new InvalidDataException("ZSTD payload is corrupt.", ex);
        }
    }
#endif

    public static byte[] Decompress(ReadOnlySpan<byte> compressed, int maxDecompressedBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxDecompressedBytes);
        if (compressed.IsEmpty)
        {
            return [];
        }

        var bound = GetDecompressedBound(compressed);

        // An inflated header is a decompression-bomb vector: check the bound before allocating.
        if (bound > (ulong)maxDecompressedBytes)
        {
            throw new InvalidDataException(
                $"ZSTD frame may decompress to {bound} bytes, exceeding the {maxDecompressedBytes}-byte limit (possible decompression bomb).");
        }

        var buffer = new byte[(int)bound];
#if NET11_0_OR_GREATER
        if (!ZstandardDecoder.TryDecompress(compressed, buffer, out var written))
        {
            throw new InvalidDataException("ZSTD payload is corrupt.");
        }

#else
        int written;
        try
        {
            using var decompressor = new ZstdSharp.Decompressor();
            written = decompressor.Unwrap(compressed, buffer);
        }
        catch (ZstdSharp.ZstdException ex)
        {
            throw new InvalidDataException("ZSTD payload is corrupt.", ex);
        }
#endif
        return written == buffer.Length ? buffer : buffer.AsSpan(0, written).ToArray();
    }

    public static byte[] Compress(ReadOnlySpan<byte> data, CompressionLevel level)
    {
#if NET11_0_OR_GREATER
        using var encoder = new ZstandardEncoder(Options(level));
        encoder.SetSourceLength(data.Length);
        var destination = new byte[ZstandardEncoder.GetMaxCompressedLength(data.Length)];
        var status = encoder.Compress(data, destination, out _, out var written, isFinalBlock: true);
        if (status != OperationStatus.Done)
        {
            throw new InvalidOperationException($"ZSTD compression stopped with {status}.");
        }

        return destination.AsSpan(0, written).ToArray();
#else
        using var compressor = new ZstdSharp.Compressor(ToZstdLevel(level));
        return compressor.Wrap(data).ToArray();
#endif
    }

    // The caller keeps ownership of output: disposing the returned stream finishes the frame only.
    public static Stream CreateCompressionStream(Stream output, CompressionLevel level)
#if NET11_0_OR_GREATER
        => new ZstandardStream(output, Options(level), leaveOpen: true);

    private static ZstandardCompressionOptions Options(CompressionLevel level) => new() { Quality = ToZstdLevel(level) };
#else
        => new ZstdSharp.CompressionStream(output, ToZstdLevel(level), leaveOpen: true);
#endif
}
