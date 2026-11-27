// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Buffers.Binary;

namespace Honua.Core.Features.Infrastructure.Validation;

public static class BoundedGeometryNestingGuard
{
    public const int MaxWktNestingDepth = 32;
    public const int MaxWkbNestingDepth = 32;

    public static void EnsureWktNestingDepth(string wkt, string description)
    {
        ArgumentNullException.ThrowIfNull(wkt);
        ArgumentException.ThrowIfNullOrWhiteSpace(description);

        var depth = 0;
        foreach (var ch in wkt)
        {
            depth = UpdateParenDepth(depth, ch, description);
            if (depth < 0)
            {
                return;
            }
        }
    }

    public static void EnsureWkbNestingDepth(ReadOnlySpan<byte> wkb, string description)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(description);

        if (wkb.IsEmpty)
        {
            return;
        }

        var scanner = new WkbNestingScanner(wkb);
        if (scanner.ExceedsMaxDepth(MaxWkbNestingDepth))
        {
            throw new ArgumentException(
                $"{description} exceeds the maximum nesting depth of {MaxWkbNestingDepth}.");
        }
    }

    private static int UpdateParenDepth(int depth, char ch, string description)
    {
        if (ch == '(')
        {
            var nextDepth = depth + 1;
            if (nextDepth > MaxWktNestingDepth)
            {
                throw new ArgumentException(
                    $"{description} exceeds the maximum nesting depth of {MaxWktNestingDepth}.");
            }

            return nextDepth;
        }

        return ch == ')' ? depth - 1 : depth;
    }

    private enum WkbByteOrder : byte
    {
        BigEndian = 0,
        LittleEndian = 1,
    }

    private ref struct WkbNestingScanner(ReadOnlySpan<byte> wkb)
    {
        private const uint EwkbZFlag = 0x80000000u;
        private const uint EwkbMFlag = 0x40000000u;
        private const uint EwkbSridFlag = 0x20000000u;
        private const uint EwkbFlagMask = EwkbZFlag | EwkbMFlag | EwkbSridFlag;

        private readonly ReadOnlySpan<byte> _wkb = wkb;
        private int _offset;
        private bool _depthExceeded;

        public bool ExceedsMaxDepth(int maxDepth)
        {
            ScanGeometry(depth: 0, maxDepth);
            return _depthExceeded;
        }

        private bool ScanGeometry(int depth, int maxDepth)
        {
            if (depth > maxDepth)
            {
                _depthExceeded = true;
                return false;
            }

            if (ReadHeader() is not { } header)
            {
                return false;
            }

            return header.BaseType switch
            {
                1 => SkipBytes(header.OrdinateCount * sizeof(double)),
                2 => SkipCoordinateSequence(header.OrdinateCount, header.ByteOrder),
                3 => SkipPolygon(header),
                4 or 5 or 6 or 7 => ScanCollection(header.ByteOrder, depth, maxDepth),
                _ => false
            };
        }

        private WkbHeader? ReadHeader()
        {
            if (_offset >= _wkb.Length)
            {
                return null;
            }

            var rawByteOrder = _wkb[_offset++];
            if (rawByteOrder is not (0 or 1))
            {
                return null;
            }

            var byteOrder = (WkbByteOrder)rawByteOrder;
            var rawType = ReadUInt32(byteOrder);
            if (!rawType.HasValue)
            {
                return null;
            }

            if ((rawType.Value & EwkbSridFlag) != 0 && !SkipBytes(sizeof(int)))
            {
                return null;
            }

            var header = BuildHeader(rawType.Value, byteOrder);
            return header.BaseType is >= 1 and <= 7 ? header : null;
        }

        private static WkbHeader BuildHeader(uint rawType, WkbByteOrder byteOrder)
        {
            var typeWithoutFlags = rawType & ~EwkbFlagMask;
            var isoDimension = typeWithoutFlags / 1000;
            var baseType = typeWithoutFlags % 1000;
            var hasZ = (rawType & EwkbZFlag) != 0 || isoDimension is 1 or 3;
            var hasM = (rawType & EwkbMFlag) != 0 || isoDimension is 2 or 3;
            var ordinateCount = 2 + (hasZ ? 1 : 0) + (hasM ? 1 : 0);
            return new WkbHeader(baseType, ordinateCount, byteOrder);
        }

        private bool SkipPolygon(WkbHeader header)
        {
            var ringCount = ReadUInt32(header.ByteOrder);
            if (!ringCount.HasValue || ringCount.Value > (uint)((_wkb.Length - _offset) / sizeof(uint)))
            {
                return false;
            }

            for (uint ring = 0; ring < ringCount.Value; ring++)
            {
                if (!SkipCoordinateSequence(header.OrdinateCount, header.ByteOrder))
                {
                    return false;
                }
            }

            return true;
        }

        private bool ScanCollection(WkbByteOrder byteOrder, int depth, int maxDepth)
        {
            var count = ReadUInt32(byteOrder);
            if (!count.HasValue || count.Value > (uint)((_wkb.Length - _offset) / 5))
            {
                return false;
            }

            for (uint i = 0; i < count.Value; i++)
            {
                if (!ScanGeometry(depth + 1, maxDepth))
                {
                    return false;
                }
            }

            return true;
        }

        private bool SkipCoordinateSequence(int ordinateCount, WkbByteOrder byteOrder)
        {
            var pointCount = ReadUInt32(byteOrder);
            if (!pointCount.HasValue)
            {
                return false;
            }

            var byteCount = (ulong)pointCount.Value * (ulong)ordinateCount * sizeof(double);
            if (byteCount > (ulong)(_wkb.Length - _offset))
            {
                return false;
            }

            _offset += (int)byteCount;
            return true;
        }

        private bool SkipBytes(int count)
        {
            if (count < 0 || _wkb.Length - _offset < count)
            {
                return false;
            }

            _offset += count;
            return true;
        }

        private uint? ReadUInt32(WkbByteOrder byteOrder)
        {
            if (_wkb.Length - _offset < sizeof(uint))
            {
                return null;
            }

            var slice = _wkb.Slice(_offset, sizeof(uint));
            _offset += sizeof(uint);
            return byteOrder == WkbByteOrder.LittleEndian
                ? BinaryPrimitives.ReadUInt32LittleEndian(slice)
                : BinaryPrimitives.ReadUInt32BigEndian(slice);
        }
    }

    private readonly record struct WkbHeader(uint BaseType, int OrdinateCount, WkbByteOrder ByteOrder);
}
