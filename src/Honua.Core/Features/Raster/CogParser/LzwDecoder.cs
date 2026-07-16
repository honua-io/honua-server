// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Buffers;

namespace Honua.Core.Features.Raster.CogParser;

/// <summary>
/// TIFF-flavoured LZW decoder (TIFF 6.0 section 13).
/// Codes are packed MSB-first and the code width grows one entry earlier than
/// textbook/GIF LZW — the Aldus "early change" — so widths step up at table
/// entries 511, 1023, and 2047 rather than 512, 1024, and 2048.
/// Uses only spans, primitives, and pooled scratch: no reflection, AOT-safe.
/// </summary>
internal static class LzwDecoder
{
    private const int ClearCode = 256;
    private const int EndOfInformationCode = 257;
    private const int FirstFreeCode = 258;
    private const int TableSize = 4096;
    private const int MinBitWidth = 9;
    private const int MaxBitWidth = 12;

    /// <summary>
    /// Decodes an LZW-compressed TIFF tile/strip segment.
    /// </summary>
    /// <param name="input">Compressed segment bytes.</param>
    /// <param name="maxDecompressedBytes">Decompression-bomb ceiling for the output.</param>
    /// <param name="expectedBytes">
    /// Expected output size when the caller knows the tile's pixel-buffer size, used to size
    /// the initial scratch buffer; pass 0 when unknown.
    /// </param>
    public static byte[] Decode(ReadOnlySpan<byte> input, int maxDecompressedBytes, int expectedBytes = 0)
    {
        var bytePool = ArrayPool<byte>.Shared;
        var shortPool = ArrayPool<short>.Shared;

        // prefix/suffix form the code table as a linked trie: each entry points at its prefix
        // code and carries one suffix byte, so expansion never allocates per-code strings.
        var prefix = shortPool.Rent(TableSize);
        var suffix = bytePool.Rent(TableSize);
        // A single code can expand to at most one byte per table entry.
        var stack = bytePool.Rent(TableSize);

        var initialCapacity = expectedBytes > 0
            ? Math.Min(expectedBytes, maxDecompressedBytes)
            : Math.Max(input.Length * 3, 4096);
        var output = bytePool.Rent(Math.Max(initialCapacity, 4096));
        var written = 0;

        try
        {
            for (var i = 0; i < 256; i++)
            {
                prefix[i] = -1;
                suffix[i] = (byte)i;
            }

            var nextCode = FirstFreeCode;
            var bitWidth = MinBitWidth;
            var oldCode = -1;

            uint bitBuffer = 0;
            var bitCount = 0;
            var position = 0;

            while (true)
            {
                while (bitCount < bitWidth && position < input.Length)
                {
                    bitBuffer = (bitBuffer << 8) | input[position++];
                    bitCount += 8;
                }

                // A segment that runs out of bits without an EOI code is tolerated: GDAL and
                // libtiff both accept LZW segments truncated at a byte boundary.
                if (bitCount < bitWidth)
                {
                    break;
                }

                var code = (int)((bitBuffer >> (bitCount - bitWidth)) & ((1u << bitWidth) - 1));
                bitCount -= bitWidth;
                bitBuffer &= (1u << bitCount) - 1;

                if (code == EndOfInformationCode)
                {
                    break;
                }

                if (code == ClearCode)
                {
                    nextCode = FirstFreeCode;
                    bitWidth = MinBitWidth;
                    oldCode = -1;
                    continue;
                }

                if (oldCode < 0)
                {
                    // The first code of a run is always a root literal; the table holds nothing else yet.
                    if (code >= 256)
                    {
                        throw new InvalidDataException(
                            $"LZW tile is corrupt: first code after a clear was {code}, which is not a literal.");
                    }

                    EnsureCapacity(ref output, written, 1, maxDecompressedBytes, bytePool);
                    output[written++] = (byte)code;
                    oldCode = code;
                    continue;
                }

                if (code > nextCode)
                {
                    throw new InvalidDataException(
                        $"LZW tile is corrupt: code {code} references table entry {nextCode} before it is defined.");
                }

                // code == nextCode is the KwKwK case: the encoder emitted an entry it defined on
                // this very step, so the string is string(oldCode) + firstChar(string(oldCode)).
                var isDeferredCode = code == nextCode;
                var expandCode = isDeferredCode ? oldCode : code;

                var stackTop = 0;
                var current = expandCode;
                while (current >= 0)
                {
                    stack[stackTop++] = suffix[current];
                    current = prefix[current];
                }

                var firstChar = stack[stackTop - 1];

                EnsureCapacity(ref output, written, stackTop + 1, maxDecompressedBytes, bytePool);
                for (var i = stackTop - 1; i >= 0; i--)
                {
                    output[written++] = stack[i];
                }

                if (isDeferredCode)
                {
                    output[written++] = firstChar;
                }

                if (nextCode < TableSize)
                {
                    prefix[nextCode] = (short)oldCode;
                    suffix[nextCode] = firstChar;
                    nextCode++;

                    // Early change: step up while one code short of the width's ceiling.
                    if (nextCode >= (1 << bitWidth) - 1 && bitWidth < MaxBitWidth)
                    {
                        bitWidth++;
                    }
                }

                oldCode = code;
            }

            var result = new byte[written];
            Buffer.BlockCopy(output, 0, result, 0, written);
            return result;
        }
        finally
        {
            shortPool.Return(prefix);
            bytePool.Return(suffix);
            bytePool.Return(stack);
            bytePool.Return(output);
        }
    }

    private static void EnsureCapacity(
        ref byte[] buffer, int written, int additional, int maxDecompressedBytes, ArrayPool<byte> pool)
    {
        var required = (long)written + additional;
        if (required > maxDecompressedBytes)
        {
            throw new InvalidDataException(
                $"LZW tile decompressed beyond the {maxDecompressedBytes}-byte limit; refusing to inflate further (possible decompression bomb).");
        }

        if (required <= buffer.Length)
        {
            return;
        }

        var capacity = (long)buffer.Length;
        while (capacity < required)
        {
            capacity *= 2;
        }

        var bigger = pool.Rent((int)Math.Min(capacity, maxDecompressedBytes));
        Buffer.BlockCopy(buffer, 0, bigger, 0, written);
        pool.Return(buffer);
        buffer = bigger;
    }
}
