// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Buffers.Binary;

namespace Honua.Core.Features.Raster.CogParser;

internal sealed class TiffIfdParser
{
    public const int MaxIfdEntryCount = 65535;

    private readonly bool _isLittleEndian;
    private readonly bool _isBigTiff;

    public bool IsLittleEndian => _isLittleEndian;

    public bool IsBigTiff => _isBigTiff;

    private TiffIfdParser(bool isLittleEndian, bool isBigTiff)
    {
        _isLittleEndian = isLittleEndian;
        _isBigTiff = isBigTiff;
    }

    public static (TiffIfdParser Parser, long FirstIfdOffset) ParseHeader(ReadOnlySpan<byte> header)
    {
        if (header.Length < TiffConstants.ClassicHeaderSize)
        {
            throw new InvalidDataException("Buffer too small for TIFF header.");
        }

        var byteOrder = ReadUInt16(header, 0, isLittleEndian: true);
        bool isLittleEndian;
        if (byteOrder == TiffConstants.LittleEndianMarker)
        {
            isLittleEndian = true;
        }
        else if (byteOrder == TiffConstants.BigEndianMarker)
        {
            isLittleEndian = false;
        }
        else
        {
            throw new InvalidDataException($"Invalid TIFF byte order marker: 0x{byteOrder:X4}");
        }

        var magic = ReadUInt16(header, 2, isLittleEndian);
        bool isBigTiff;
        long firstIfdOffset;

        if (magic == TiffConstants.ClassicTiffMagic)
        {
            isBigTiff = false;
            firstIfdOffset = ReadUInt32(header, 4, isLittleEndian);
        }
        else if (magic == TiffConstants.BigTiffMagic)
        {
            isBigTiff = true;
            if (header.Length < TiffConstants.BigTiffHeaderSize)
            {
                throw new InvalidDataException("Buffer too small for BigTIFF header.");
            }

            // BigTIFF: bytes 4-5 = offset byte size (must be 8), 6-7 = always 0
            firstIfdOffset = ReadInt64(header, 8, isLittleEndian);
            if (firstIfdOffset < 0)
            {
                throw new InvalidDataException("BigTIFF header declares a negative IFD offset.");
            }
        }
        else
        {
            throw new InvalidDataException($"Invalid TIFF magic number: {magic}. Expected 42 (classic) or 43 (BigTIFF).");
        }

        return (new TiffIfdParser(isLittleEndian, isBigTiff), firstIfdOffset);
    }

    public (IfdEntry[] Entries, long NextIfdOffset) ParseIfd(ReadOnlySpan<byte> ifdData)
    {
        long entryCount;
        int dataStart;

        if (_isBigTiff)
        {
            if (ifdData.Length < 8)
            {
                throw new InvalidDataException("Buffer too small for BigTIFF IFD entry count.");
            }

            entryCount = ReadInt64(ifdData, 0, _isLittleEndian);
            dataStart = 8;
        }
        else
        {
            if (ifdData.Length < 2)
            {
                throw new InvalidDataException("Buffer too small for TIFF IFD entry count.");
            }

            entryCount = ReadUInt16(ifdData, 0, _isLittleEndian);
            dataStart = 2;
        }

        if (entryCount is < 0 or > MaxIfdEntryCount)
        {
            throw new InvalidDataException($"IFD entry count {entryCount} is outside the supported range.");
        }

        var entrySize = _isBigTiff ? TiffConstants.BigTiffIfdEntrySize : TiffConstants.ClassicIfdEntrySize;
        var entriesEnd = dataStart + (int)entryCount * entrySize;
        if (ifdData.Length < entriesEnd)
        {
            throw new InvalidDataException($"Buffer too small for {entryCount} TIFF IFD entries.");
        }

        var entries = new IfdEntry[(int)entryCount];

        for (var i = 0; i < (int)entryCount; i++)
        {
            var offset = dataStart + i * entrySize;
            entries[i] = ParseIfdEntry(ifdData, offset);
        }

        // Next IFD offset follows entries
        long nextIfdOffset = 0;
        if (ifdData.Length >= entriesEnd + (_isBigTiff ? 8 : 4))
        {
            nextIfdOffset = _isBigTiff
                ? ReadInt64(ifdData, entriesEnd, _isLittleEndian)
                : ReadUInt32(ifdData, entriesEnd, _isLittleEndian);
        }

        return (entries, nextIfdOffset);
    }

    public long ReadIfdEntryCount(ReadOnlySpan<byte> ifdData)
    {
        if (_isBigTiff)
        {
            if (ifdData.Length < 8)
            {
                throw new InvalidDataException("Buffer too small for BigTIFF IFD entry count.");
            }

            return ReadInt64(ifdData, 0, _isLittleEndian);
        }

        if (ifdData.Length < 2)
        {
            throw new InvalidDataException("Buffer too small for TIFF IFD entry count.");
        }

        return ReadUInt16(ifdData, 0, _isLittleEndian);
    }

    public int CalculateIfdReadSize(long estimatedEntryCount)
    {
        var entrySize = _isBigTiff ? TiffConstants.BigTiffIfdEntrySize : TiffConstants.ClassicIfdEntrySize;
        var countSize = _isBigTiff ? 8 : 2;
        var nextPtrSize = _isBigTiff ? 8 : 4;
        return countSize + (int)estimatedEntryCount * entrySize + nextPtrSize;
    }

    public long[] ReadLongArray(ReadOnlySpan<byte> data, int count, ushort type)
    {
        var typeSize = TiffConstants.GetTypeSize(type);
        ValidateArrayBounds(data.Length, count, typeSize);
        var result = new long[count];

        for (var i = 0; i < count; i++)
        {
            var pos = i * typeSize;
            result[i] = type switch
            {
                TiffConstants.TypeByte or TiffConstants.TypeAscii => data[pos],
                TiffConstants.TypeShort => ReadUInt16(data, pos, _isLittleEndian),
                TiffConstants.TypeLong => ReadUInt32(data, pos, _isLittleEndian),
                TiffConstants.TypeLong8 => ReadInt64(data, pos, _isLittleEndian),
                _ => ReadUInt32(data, pos, _isLittleEndian)
            };
        }

        return result;
    }

    public int[] ReadIntArray(ReadOnlySpan<byte> data, int count, ushort type)
    {
        var typeSize = TiffConstants.GetTypeSize(type);
        ValidateArrayBounds(data.Length, count, typeSize);
        var result = new int[count];

        for (var i = 0; i < count; i++)
        {
            var pos = i * typeSize;
            result[i] = type switch
            {
                TiffConstants.TypeByte or TiffConstants.TypeAscii or TiffConstants.TypeUndefined => data[pos],
                TiffConstants.TypeShort => ReadUInt16(data, pos, _isLittleEndian),
                TiffConstants.TypeLong => (int)ReadUInt32(data, pos, _isLittleEndian),
                TiffConstants.TypeLong8 => ReadBoundedInt32FromInt64(data, pos),
                _ => (int)ReadUInt32(data, pos, _isLittleEndian)
            };
        }

        return result;
    }

    private int ReadBoundedInt32FromInt64(ReadOnlySpan<byte> data, int pos)
    {
        var value = ReadInt64(data, pos, _isLittleEndian);
        if (value is < int.MinValue or > int.MaxValue)
        {
            throw new InvalidDataException($"TIFF LONG8 array element {value} is outside the 32-bit integer range.");
        }

        return (int)value;
    }

    private static void ValidateArrayBounds(int dataLength, int count, int typeSize)
    {
        if (count < 0 || (long)count * typeSize > dataLength)
        {
            throw new InvalidDataException(
                $"TIFF array requires {(long)count * typeSize} bytes, exceeding the {dataLength}-byte buffer.");
        }
    }

    public long[] ReadInlineLongArray(long valueOrOffset, int count, ushort type)
    {
        Span<byte> inlineData = stackalloc byte[_isBigTiff ? 8 : 4];
        WriteInlineValue(inlineData, valueOrOffset);
        return ReadLongArray(inlineData[..(count * TiffConstants.GetTypeSize(type))], count, type);
    }

    public int[] ReadInlineIntArray(long valueOrOffset, int count, ushort type)
    {
        Span<byte> inlineData = stackalloc byte[_isBigTiff ? 8 : 4];
        WriteInlineValue(inlineData, valueOrOffset);
        return ReadIntArray(inlineData[..(count * TiffConstants.GetTypeSize(type))], count, type);
    }

    private IfdEntry ParseIfdEntry(ReadOnlySpan<byte> data, int offset)
    {
        var tag = ReadUInt16(data, offset, _isLittleEndian);
        var type = ReadUInt16(data, offset + 2, _isLittleEndian);

        long count;
        long valueOrOffset;

        if (_isBigTiff)
        {
            count = ReadInt64(data, offset + 4, _isLittleEndian);
            valueOrOffset = ReadInt64(data, offset + 12, _isLittleEndian);
        }
        else
        {
            count = ReadUInt32(data, offset + 4, _isLittleEndian);
            valueOrOffset = ReadUInt32(data, offset + 8, _isLittleEndian);
        }

        if (count < 0)
        {
            throw new InvalidDataException($"TIFF IFD entry (tag {tag}) declares negative count {count}.");
        }

        // For classic TIFF, if the value fits in 4 bytes, it's inline
        var typeSize = TiffConstants.GetTypeSize(type);
        var inlineLimit = _isBigTiff ? 8L : 4L;
        var isInline = count <= inlineLimit / typeSize;

        // For inline scalar values, re-read according to the exact field type so left-justified
        // BYTE/SHORT/LONG values parse correctly in big-endian TIFFs and BigTIFFs without
        // shifting or incorporating trailing padding bytes.
        if (isInline && count == 1)
        {
            var valueFieldOffset = _isBigTiff ? offset + 12 : offset + 8;
            valueOrOffset = ReadInlineScalarValue(data.Slice(valueFieldOffset), type, valueOrOffset);
        }

        return new IfdEntry(tag, type, count, valueOrOffset, isInline);
    }

    private long ReadInlineScalarValue(ReadOnlySpan<byte> valueField, ushort type, long rawValue)
        => type switch
        {
            TiffConstants.TypeByte or TiffConstants.TypeSByte
                => valueField[0],
            TiffConstants.TypeShort or TiffConstants.TypeSShort
                => ReadUInt16(valueField, 0, _isLittleEndian),
            TiffConstants.TypeLong
                => ReadUInt32(valueField, 0, _isLittleEndian),
            TiffConstants.TypeSLong
                => ReadInt32(valueField, 0),
            _ => rawValue
        };

    private int ReadInt32(ReadOnlySpan<byte> data, int offset)
        => _isLittleEndian
            ? BinaryPrimitives.ReadInt32LittleEndian(data.Slice(offset))
            : BinaryPrimitives.ReadInt32BigEndian(data.Slice(offset));

    // Endian-aware read helpers using BinaryPrimitives
    private static ushort ReadUInt16(ReadOnlySpan<byte> data, int offset, bool isLittleEndian)
        => isLittleEndian
            ? BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(offset))
            : BinaryPrimitives.ReadUInt16BigEndian(data.Slice(offset));

    private static uint ReadUInt32(ReadOnlySpan<byte> data, int offset, bool isLittleEndian)
        => isLittleEndian
            ? BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset))
            : BinaryPrimitives.ReadUInt32BigEndian(data.Slice(offset));

    private static long ReadInt64(ReadOnlySpan<byte> data, int offset, bool isLittleEndian)
        => isLittleEndian
            ? BinaryPrimitives.ReadInt64LittleEndian(data.Slice(offset))
            : BinaryPrimitives.ReadInt64BigEndian(data.Slice(offset));

    private void WriteInlineValue(Span<byte> buffer, long valueOrOffset)
    {
        if (_isBigTiff)
        {
            if (_isLittleEndian)
            {
                BinaryPrimitives.WriteInt64LittleEndian(buffer, valueOrOffset);
            }
            else
            {
                BinaryPrimitives.WriteInt64BigEndian(buffer, valueOrOffset);
            }

            return;
        }

        var rawValue = unchecked((uint)valueOrOffset);
        if (_isLittleEndian)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(buffer, rawValue);
        }
        else
        {
            BinaryPrimitives.WriteUInt32BigEndian(buffer, rawValue);
        }
    }
}

internal readonly record struct IfdEntry(
    ushort Tag,
    ushort Type,
    long Count,
    long ValueOrOffset,
    bool IsInline);
