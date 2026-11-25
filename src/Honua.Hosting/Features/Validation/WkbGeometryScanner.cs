// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Buffers.Binary;

namespace Honua.Infrastructure.Validation;

internal struct WkbScanState
{
    public string? RootGeometryType;
    public int? RootSrid;
    public long VertexCount;
    public long RingCount;
    public bool HasZ;
    public bool HasM;
}

internal enum WkbScanOutcome : byte
{
    Invalid = 0,
    Empty = 1,
    NonEmpty = 2,
}

internal ref struct WkbGeometryScanner(ReadOnlySpan<byte> wkb)
{
    private const uint EwkbZFlag = 0x80000000u;
    private const uint EwkbMFlag = 0x40000000u;
    private const uint EwkbSridFlag = 0x20000000u;
    private const uint EwkbFlagMask = EwkbZFlag | EwkbMFlag | EwkbSridFlag;
    private const int MaxWkbNestingDepth = 128;

    private readonly ReadOnlySpan<byte> _wkb = wkb;
    private int _offset;
    private WkbScanState _state;

    internal readonly WkbScanState State => _state;

    internal WkbScanOutcome ScanRoot() => ScanGeometry(expectedType: null, depth: 0);

    private WkbScanOutcome ScanGeometry(uint? expectedType, int depth)
    {
        if (ReadNodeHeader(expectedType, depth) is not { } header)
        {
            return WkbScanOutcome.Invalid;
        }

        return header.BaseType switch
        {
            1 => ScanPoint(header),
            2 => ScanLineString(header),
            3 => ScanPolygon(header),
            4 or 5 or 6 or 7 => ScanCollection(header, depth),
            _ => WkbScanOutcome.Invalid
        };
    }

    private WkbNodeHeader? ReadNodeHeader(uint? expectedType, int depth)
    {
        if (depth > MaxWkbNestingDepth || _offset >= _wkb.Length)
        {
            return null;
        }

        var byteOrder = _wkb[_offset++];
        if (byteOrder is not (0 or 1))
        {
            return null;
        }

        var flags = byteOrder == 1 ? WkbNodeFlags.LittleEndian : WkbNodeFlags.None;
        if (ReadUInt32(flags) is not { } rawType)
        {
            return null;
        }

        var typeWithoutFlags = rawType & ~EwkbFlagMask;
        var isoDimension = typeWithoutFlags / 1000;
        var baseType = typeWithoutFlags % 1000;
        if (isoDimension > 3 || baseType is < 1 or > 7 || (expectedType.HasValue && baseType != expectedType.Value))
        {
            return null;
        }

        if ((rawType & EwkbSridFlag) != 0 && !TryReadHeaderSrid(flags, depth))
        {
            return null;
        }

        if (depth == 0)
        {
            _state.RootGeometryType = ResolveGeometryTypeName(baseType);
        }

        return BuildNodeHeader(rawType, flags);
    }

    private bool TryReadHeaderSrid(WkbNodeFlags flags, int depth)
    {
        if (ReadInt32(flags) is not { } srid)
        {
            return false;
        }

        if (depth == 0 && srid > 0)
        {
            _state.RootSrid = srid;
        }

        return true;
    }

    private static WkbNodeHeader BuildNodeHeader(uint rawType, WkbNodeFlags endianness)
    {
        var typeWithoutFlags = rawType & ~EwkbFlagMask;
        var isoDimension = typeWithoutFlags / 1000;
        var baseType = typeWithoutFlags % 1000;
        var flags = endianness;
        if ((rawType & EwkbZFlag) != 0 || isoDimension is 1 or 3)
        {
            flags |= WkbNodeFlags.HasZ;
        }

        if ((rawType & EwkbMFlag) != 0 || isoDimension is 2 or 3)
        {
            flags |= WkbNodeFlags.HasM;
        }

        var ordinateCount = 2 + ((flags & WkbNodeFlags.HasZ) != 0 ? 1 : 0) + ((flags & WkbNodeFlags.HasM) != 0 ? 1 : 0);
        return new WkbNodeHeader
        {
            BaseType = baseType,
            OrdinateCount = ordinateCount,
            Flags = flags
        };
    }

    private WkbScanOutcome ScanPoint(WkbNodeHeader header)
    {
        var pointBytes = header.OrdinateCount * sizeof(double);
        if (_wkb.Length - _offset < pointBytes)
        {
            return WkbScanOutcome.Invalid;
        }

        var x = ReadDouble(_wkb.Slice(_offset, sizeof(double)), header.Flags);
        var y = ReadDouble(_wkb.Slice(_offset + sizeof(double), sizeof(double)), header.Flags);
        if (double.IsNaN(x) && double.IsNaN(y))
        {
            _offset += pointBytes;
            return WkbScanOutcome.Empty;
        }

        _state.VertexCount++;
        UpdateDimensionsForVertex(_offset, header);
        _offset += pointBytes;
        return WkbScanOutcome.NonEmpty;
    }

    private WkbScanOutcome ScanLineString(WkbNodeHeader header)
    {
        if (ReadUInt32(header.Flags) is not { } numPoints || numPoints == 1)
        {
            return WkbScanOutcome.Invalid;
        }

        if (!ScanCoordinateSequence(numPoints, header))
        {
            return WkbScanOutcome.Invalid;
        }

        return numPoints == 0 ? WkbScanOutcome.Empty : WkbScanOutcome.NonEmpty;
    }

    private WkbScanOutcome ScanPolygon(WkbNodeHeader header)
    {
        if (ReadUInt32(header.Flags) is not { } numRings
            || numRings > (uint)((_wkb.Length - _offset) / sizeof(uint)))
        {
            return WkbScanOutcome.Invalid;
        }

        if (numRings == 0)
        {
            _state.RingCount += 1;
            return WkbScanOutcome.Empty;
        }

        _state.RingCount += numRings;
        var polygonEmpty = true;
        for (uint ring = 0; ring < numRings; ring++)
        {
            if (ReadUInt32(header.Flags) is not { } numPoints || !ScanCoordinateSequence(numPoints, header))
            {
                return WkbScanOutcome.Invalid;
            }

            if (ring == 0 && numPoints > 0)
            {
                polygonEmpty = false;
            }
        }

        return polygonEmpty ? WkbScanOutcome.Empty : WkbScanOutcome.NonEmpty;
    }

    private WkbScanOutcome ScanCollection(WkbNodeHeader header, int depth)
    {
        if (ReadUInt32(header.Flags) is not { } numGeometries
            || numGeometries > (uint)((_wkb.Length - _offset) / 5))
        {
            return WkbScanOutcome.Invalid;
        }

        uint? childExpected = header.BaseType == 7 ? null : header.BaseType - 3;
        var collectionEmpty = true;
        for (uint i = 0; i < numGeometries; i++)
        {
            var childOutcome = ScanGeometry(childExpected, depth + 1);
            if (childOutcome == WkbScanOutcome.Invalid)
            {
                return WkbScanOutcome.Invalid;
            }

            if (childOutcome == WkbScanOutcome.NonEmpty)
            {
                collectionEmpty = false;
            }
        }

        return collectionEmpty ? WkbScanOutcome.Empty : WkbScanOutcome.NonEmpty;
    }

    private bool ScanCoordinateSequence(uint numPoints, WkbNodeHeader header)
    {
        var byteCount = (ulong)numPoints * (ulong)header.OrdinateCount * sizeof(double);
        if (byteCount > (ulong)(_wkb.Length - _offset))
        {
            return false;
        }

        _state.VertexCount += numPoints;
        if (numPoints > 0 && ((header.HasZ && !_state.HasZ) || (header.HasM && !_state.HasM)))
        {
            ScanSequenceDimensions(_offset, numPoints, header);
        }

        _offset += (int)byteCount;
        return true;
    }

    private void ScanSequenceDimensions(int startOffset, uint numPoints, WkbNodeHeader header)
    {
        var stride = header.OrdinateCount * sizeof(double);
        var pos = startOffset;
        for (uint i = 0; i < numPoints && ((header.HasZ && !_state.HasZ) || (header.HasM && !_state.HasM)); i++)
        {
            UpdateDimensionsForVertex(pos, header);
            pos += stride;
        }
    }

    private void UpdateDimensionsForVertex(int vertexOffset, WkbNodeHeader header)
    {
        if (header.HasZ && !_state.HasZ)
        {
            var z = ReadDouble(_wkb.Slice(vertexOffset + (2 * sizeof(double)), sizeof(double)), header.Flags);
            if (!double.IsNaN(z))
            {
                _state.HasZ = true;
            }
        }

        if (header.HasM && !_state.HasM)
        {
            var mOffset = (header.HasZ ? 3 : 2) * sizeof(double);
            var m = ReadDouble(_wkb.Slice(vertexOffset + mOffset, sizeof(double)), header.Flags);
            if (!double.IsNaN(m))
            {
                _state.HasM = true;
            }
        }
    }

    private uint? ReadUInt32(WkbNodeFlags flags)
    {
        if (_wkb.Length - _offset < sizeof(uint))
        {
            return null;
        }

        var slice = _wkb.Slice(_offset, sizeof(uint));
        _offset += sizeof(uint);
        return (flags & WkbNodeFlags.LittleEndian) != 0
            ? BinaryPrimitives.ReadUInt32LittleEndian(slice)
            : BinaryPrimitives.ReadUInt32BigEndian(slice);
    }

    private int? ReadInt32(WkbNodeFlags flags)
    {
        if (_wkb.Length - _offset < sizeof(int))
        {
            return null;
        }

        var slice = _wkb.Slice(_offset, sizeof(int));
        _offset += sizeof(int);
        return (flags & WkbNodeFlags.LittleEndian) != 0
            ? BinaryPrimitives.ReadInt32LittleEndian(slice)
            : BinaryPrimitives.ReadInt32BigEndian(slice);
    }

    private static double ReadDouble(ReadOnlySpan<byte> slice, WkbNodeFlags flags)
        => (flags & WkbNodeFlags.LittleEndian) != 0
            ? BinaryPrimitives.ReadDoubleLittleEndian(slice)
            : BinaryPrimitives.ReadDoubleBigEndian(slice);

    private static string ResolveGeometryTypeName(uint baseType) => baseType switch
    {
        1 => "Point",
        2 => "LineString",
        3 => "Polygon",
        4 => "MultiPoint",
        5 => "MultiLineString",
        6 => "MultiPolygon",
        7 => "GeometryCollection",
        _ => string.Empty
    };

    [Flags]
    private enum WkbNodeFlags : byte
    {
        None = 0,
        LittleEndian = 1,
        HasZ = 2,
        HasM = 4,
    }

    private readonly struct WkbNodeHeader
    {
        public uint BaseType { get; init; }

        public int OrdinateCount { get; init; }

        public WkbNodeFlags Flags { get; init; }

        public bool HasZ => (Flags & WkbNodeFlags.HasZ) != 0;

        public bool HasM => (Flags & WkbNodeFlags.HasM) != 0;
    }
}
