// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Buffers.Binary;

namespace Honua.Core.Features.Raster.CogParser;

/// <summary>
/// AOT-safe TIFF/BigTIFF IFD chain parser.
/// Uses only <see cref="Span{T}"/>, <see cref="BinaryPrimitives"/>, and primitive types.
/// No reflection, no external TIFF library.
/// </summary>
internal sealed class TiffIfdParser
{
    private readonly bool _isLittleEndian;
    private readonly bool _isBigTiff;

    /// <summary>
    /// Whether the TIFF uses little-endian byte order.
    /// </summary>
    public bool IsLittleEndian => _isLittleEndian;

    /// <summary>
    /// Whether this is a BigTIFF (8-byte offsets) rather than classic TIFF (4-byte offsets).
    /// </summary>
    public bool IsBigTiff => _isBigTiff;

    private TiffIfdParser(bool isLittleEndian, bool isBigTiff)
    {
        _isLittleEndian = isLittleEndian;
        _isBigTiff = isBigTiff;
    }

    /// <summary>
    /// Parses the TIFF header from the first 16 bytes.
    /// Returns the parser instance and the offset to the first IFD.
    /// </summary>
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
        }
        else
        {
            throw new InvalidDataException($"Invalid TIFF magic number: {magic}. Expected 42 (classic) or 43 (BigTIFF).");
        }

        return (new TiffIfdParser(isLittleEndian, isBigTiff), firstIfdOffset);
    }

    /// <summary>
    /// Parses IFD entries from a buffer positioned at the start of an IFD.
    /// Returns the parsed entries and the offset to the next IFD (0 if none).
    /// </summary>
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

        if (entryCount > 65535)
        {
            throw new InvalidDataException($"IFD entry count {entryCount} exceeds maximum.");
        }

        var entrySize = _isBigTiff ? TiffConstants.BigTiffIfdEntrySize : TiffConstants.ClassicIfdEntrySize;
        var entriesEnd = dataStart + (int)entryCount * entrySize;
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

    /// <summary>
    /// Calculates the byte size needed to read an IFD at the given entry count.
    /// </summary>
    public int CalculateIfdReadSize(long estimatedEntryCount)
    {
        var entrySize = _isBigTiff ? TiffConstants.BigTiffIfdEntrySize : TiffConstants.ClassicIfdEntrySize;
        var countSize = _isBigTiff ? 8 : 2;
        var nextPtrSize = _isBigTiff ? 8 : 4;
        return countSize + (int)estimatedEntryCount * entrySize + nextPtrSize;
    }

    /// <summary>
    /// Reads an array of long values from a byte buffer (for tile offsets/byte counts).
    /// </summary>
    public long[] ReadLongArray(ReadOnlySpan<byte> data, int count, ushort type)
    {
        var result = new long[count];
        var typeSize = TiffConstants.GetTypeSize(type);

        for (var i = 0; i < count; i++)
        {
            var pos = i * typeSize;
            result[i] = type switch
            {
                TiffConstants.TypeShort => ReadUInt16(data, pos, _isLittleEndian),
                TiffConstants.TypeLong => ReadUInt32(data, pos, _isLittleEndian),
                TiffConstants.TypeLong8 => ReadInt64(data, pos, _isLittleEndian),
                _ => ReadUInt32(data, pos, _isLittleEndian)
            };
        }

        return result;
    }

    /// <summary>
    /// Reads an array of int values from a byte buffer.
    /// </summary>
    public int[] ReadIntArray(ReadOnlySpan<byte> data, int count, ushort type)
    {
        var result = new int[count];
        var typeSize = TiffConstants.GetTypeSize(type);

        for (var i = 0; i < count; i++)
        {
            var pos = i * typeSize;
            result[i] = type switch
            {
                TiffConstants.TypeShort => ReadUInt16(data, pos, _isLittleEndian),
                TiffConstants.TypeLong => (int)ReadUInt32(data, pos, _isLittleEndian),
                TiffConstants.TypeLong8 => checked((int)ReadInt64(data, pos, _isLittleEndian)),
                _ => (int)ReadUInt32(data, pos, _isLittleEndian)
            };
        }

        return result;
    }

    /// <summary>
    /// Reads an inline array of long values from an IFD entry's value/offset field.
    /// </summary>
    public long[] ReadInlineLongArray(long valueOrOffset, int count, ushort type)
    {
        Span<byte> inlineData = stackalloc byte[_isBigTiff ? 8 : 4];
        WriteInlineValue(inlineData, valueOrOffset);
        return ReadLongArray(inlineData[..(count * TiffConstants.GetTypeSize(type))], count, type);
    }

    /// <summary>
    /// Reads an inline array of int values from an IFD entry's value/offset field.
    /// </summary>
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

        // For classic TIFF, if the value fits in 4 bytes, it's inline
        var typeSize = TiffConstants.GetTypeSize(type);
        var totalSize = count * typeSize;
        var isInline = _isBigTiff ? totalSize <= 8 : totalSize <= 4;

        // For inline values, re-read according to the field type so that
        // left-justified SHORT values parse correctly in big-endian TIFFs.
        // TIFF spec: values shorter than the value/offset field are stored
        // in the lower-numbered bytes (left-justified).
        if (isInline && count == 1)
        {
            var valueFieldOffset = _isBigTiff ? offset + 12 : offset + 8;
            valueOrOffset = type switch
            {
                TiffConstants.TypeByte or TiffConstants.TypeSByte
                    => data[valueFieldOffset],
                TiffConstants.TypeShort or TiffConstants.TypeSShort
                    => ReadUInt16(data, valueFieldOffset, _isLittleEndian),
                _ => valueOrOffset // LONG, LONG8, etc. already read correctly
            };
        }

        return new IfdEntry(tag, type, count, valueOrOffset, isInline);
    }

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

/// <summary>
/// A parsed TIFF IFD entry (directory tag).
/// </summary>
internal readonly record struct IfdEntry(
    ushort Tag,
    ushort Type,
    long Count,
    long ValueOrOffset,
    bool IsInline);
