// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Honua.Core.Features.Raster.CogParser;

// TIFF horizontal differences wrap at the sample bit depth, so the prefix sum is unchecked.
internal static class TilePredictor
{
    public static void Undo(Span<byte> pixels, in TilePixelLayout layout)
    {
        if (!layout.HasPredictor || pixels.IsEmpty)
        {
            return;
        }

        if (layout.Predictor != TilePixelLayout.PredictorHorizontalDifferencing)
        {
            throw new UnsupportedTilePredictorException(
                layout.Predictor,
                layout.Predictor == TilePixelLayout.PredictorFloatingPoint
                    ? "floating-point predictors are not implemented; re-encode with PREDICTOR=1 or PREDICTOR=2."
                    : "only predictor 1 (none) and 2 (horizontal differencing) are implemented.");
        }

        if (layout.BitsPerSample is not (8 or 16 or 32 or 64))
        {
            throw new UnsupportedTilePredictorException(
                layout.Predictor,
                $"horizontal differencing requires 8, 16, 32, or 64 bits per sample, but the tile declares {layout.BitsPerSample}.");
        }

        var bytesPerSample = layout.BitsPerSample / 8;
        var samplesPerRow = layout.TileWidth * layout.SamplesPerPixel;
        var rowStride = samplesPerRow * bytesPerSample;

        if (rowStride <= 0 || layout.SamplesPerPixel <= 0)
        {
            throw new InvalidDataException(
                $"Cannot reverse the horizontal predictor: tile geometry is invalid (tileWidth={layout.TileWidth}, samplesPerPixel={layout.SamplesPerPixel}, bitsPerSample={layout.BitsPerSample}).");
        }

        if (pixels.Length % rowStride != 0)
        {
            throw new InvalidDataException(
                $"Cannot reverse the horizontal predictor: decompressed tile is {pixels.Length} bytes, which is not a whole number of {rowStride}-byte rows.");
        }

        for (var offset = 0; offset < pixels.Length; offset += rowStride)
        {
            var row = pixels.Slice(offset, rowStride);
            switch (bytesPerSample)
            {
                case 1:
                    UndoRow8(row, layout.SamplesPerPixel);
                    break;
                case 2:
                    UndoRow16(row, layout.SamplesPerPixel, layout.IsLittleEndian);
                    break;
                case 4:
                    UndoRow32(row, layout.SamplesPerPixel, layout.IsLittleEndian);
                    break;
                default:
                    UndoRow64(row, layout.SamplesPerPixel, layout.IsLittleEndian);
                    break;
            }
        }
    }

    private static void UndoRow8(Span<byte> row, int samplesPerPixel)
    {
        var offset = samplesPerPixel;
        if (Sse2.IsSupported && samplesPerPixel is 1 or 2 or 4 or 8)
        {
            offset = UndoVectorRow8(row, samplesPerPixel);
        }

        for (var i = offset; i < row.Length; i++)
        {
            row[i] = unchecked((byte)(row[i] + row[i - samplesPerPixel]));
        }
    }

    private static int UndoVectorRow8(Span<byte> row, int samplesPerPixel)
    {
        var offset = 0;
        var carry = Vector128<byte>.Zero;
        for (; offset <= row.Length - Vector128<byte>.Count; offset += Vector128<byte>.Count)
        {
            var values = MemoryMarshal.Read<Vector128<byte>>(row[offset..]);
            // Inclusive scans run independently for each interleaved sample channel.
            if (samplesPerPixel == 1)
            {
                values = Sse2.Add(values, Sse2.ShiftLeftLogical128BitLane(values, 1));
            }

            if (samplesPerPixel <= 2)
            {
                values = Sse2.Add(values, Sse2.ShiftLeftLogical128BitLane(values, 2));
            }

            if (samplesPerPixel <= 4)
            {
                values = Sse2.Add(values, Sse2.ShiftLeftLogical128BitLane(values, 4));
            }

            values = Sse2.Add(values, Sse2.ShiftLeftLogical128BitLane(values, 8));
            values = Sse2.Add(values, carry);
            MemoryMarshal.Write(row[offset..], in values);
            carry = samplesPerPixel switch
            {
                1 => Vector128.Create(values.GetElement(15)),
                2 => Vector128.Create(values.AsUInt16().GetElement(7)).AsByte(),
                4 => Vector128.Create(values.AsUInt32().GetElement(3)).AsByte(),
                _ => Vector128.Create(values.AsUInt64().GetElement(1)).AsByte()
            };
        }

        return Math.Max(offset, samplesPerPixel);
    }

    private static void UndoRow16(Span<byte> row, int samplesPerPixel, bool isLittleEndian)
    {
        var count = row.Length / 2;
        for (var i = samplesPerPixel; i < count; i++)
        {
            var previous = ReadUInt16(row, (i - samplesPerPixel) * 2, isLittleEndian);
            var current = ReadUInt16(row, i * 2, isLittleEndian);
            WriteUInt16(row, i * 2, unchecked((ushort)(current + previous)), isLittleEndian);
        }
    }

    private static void UndoRow32(Span<byte> row, int samplesPerPixel, bool isLittleEndian)
    {
        var count = row.Length / 4;
        for (var i = samplesPerPixel; i < count; i++)
        {
            var previous = ReadUInt32(row, (i - samplesPerPixel) * 4, isLittleEndian);
            var current = ReadUInt32(row, i * 4, isLittleEndian);
            WriteUInt32(row, i * 4, unchecked(current + previous), isLittleEndian);
        }
    }

    private static void UndoRow64(Span<byte> row, int samplesPerPixel, bool isLittleEndian)
    {
        var count = row.Length / 8;
        for (var i = samplesPerPixel; i < count; i++)
        {
            var previous = ReadUInt64(row, (i - samplesPerPixel) * 8, isLittleEndian);
            var current = ReadUInt64(row, i * 8, isLittleEndian);
            WriteUInt64(row, i * 8, unchecked(current + previous), isLittleEndian);
        }
    }

    private static ushort ReadUInt16(ReadOnlySpan<byte> data, int offset, bool isLittleEndian)
        => isLittleEndian
            ? BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(offset))
            : BinaryPrimitives.ReadUInt16BigEndian(data.Slice(offset));

    private static void WriteUInt16(Span<byte> data, int offset, ushort value, bool isLittleEndian)
    {
        if (isLittleEndian)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(data.Slice(offset), value);
        }
        else
        {
            BinaryPrimitives.WriteUInt16BigEndian(data.Slice(offset), value);
        }
    }

    private static uint ReadUInt32(ReadOnlySpan<byte> data, int offset, bool isLittleEndian)
        => isLittleEndian
            ? BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset))
            : BinaryPrimitives.ReadUInt32BigEndian(data.Slice(offset));

    private static void WriteUInt32(Span<byte> data, int offset, uint value, bool isLittleEndian)
    {
        if (isLittleEndian)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(data.Slice(offset), value);
        }
        else
        {
            BinaryPrimitives.WriteUInt32BigEndian(data.Slice(offset), value);
        }
    }

    private static ulong ReadUInt64(ReadOnlySpan<byte> data, int offset, bool isLittleEndian)
        => isLittleEndian
            ? BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(offset))
            : BinaryPrimitives.ReadUInt64BigEndian(data.Slice(offset));

    private static void WriteUInt64(Span<byte> data, int offset, ulong value, bool isLittleEndian)
    {
        if (isLittleEndian)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(data.Slice(offset), value);
        }
        else
        {
            BinaryPrimitives.WriteUInt64BigEndian(data.Slice(offset), value);
        }
    }
}
