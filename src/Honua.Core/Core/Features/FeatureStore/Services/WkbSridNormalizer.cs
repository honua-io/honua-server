// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Buffers.Binary;

namespace Honua.Core.Features.FeatureStore.Services;

public static class WkbSridNormalizer
{
    private const uint EwkbZFlag = 0x80000000;
    private const uint EwkbMFlag = 0x40000000;
    private const uint EwkbSridFlag = 0x20000000;
    private const uint EwkbFlagMask = EwkbZFlag | EwkbMFlag | EwkbSridFlag;
    private const int DefaultMaxGeometryDepth = 128;

    public static byte[] RemoveEmbeddedSrid(byte[] wkb)
    {
        ArgumentNullException.ThrowIfNull(wkb);

        ReadOnlySpan<byte> input = wkb;
        var offset = 0;
        var sridBytesRemoved = 0;
        var hasZ = false;
        var hasM = false;
        if (!TryInspectGeometry(input, ref offset, ref sridBytesRemoved, ref hasZ, ref hasM) ||
            offset != input.Length ||
            sridBytesRemoved == 0)
        {
            return wkb;
        }

        var output = new byte[input.Length - sridBytesRemoved];
        var stripper = new WkbSridStripper(input, output);
        stripper.CopyGeometry();
        return output;
    }

    public static bool TryGetDeclaredOrdinates(byte[] wkb, out bool hasZ, out bool hasM)
    {
        hasZ = false;
        hasM = false;
        if (wkb == null)
        {
            return false;
        }

        var offset = 0;
        var sridBytesRemoved = 0;
        if (TryInspectGeometry(wkb, ref offset, ref sridBytesRemoved, ref hasZ, ref hasM, maximumDepth: 128) &&
            offset == wkb.Length)
        {
            return true;
        }

        hasZ = false;
        hasM = false;
        return false;
    }

    public static bool HasZOrMOrdinates(byte[] wkb)
    {
        ArgumentNullException.ThrowIfNull(wkb);

        ReadOnlySpan<byte> input = wkb;
        var offset = 0;
        if (!TryReadByteOrder(input, ref offset, out var littleEndian) ||
            !TryReadUInt32(input, ref offset, littleEndian, out var rawType))
        {
            return false;
        }

        if (!TryResolveType(rawType, out _, out var ordinateCount))
        {
            return false;
        }

        return ordinateCount > 2;
    }

    private static bool TryInspectGeometry(
        ReadOnlySpan<byte> input,
        ref int offset,
        ref int sridBytesRemoved,
        ref bool hasZ,
        ref bool hasM,
        uint? expectedGeometryType = null,
        int depth = 0,
        int maximumDepth = DefaultMaxGeometryDepth)
    {
        if (depth >= maximumDepth ||
            !TryReadByteOrder(input, ref offset, out var littleEndian) ||
            !TryReadUInt32(input, ref offset, littleEndian, out var rawType))
        {
            return false;
        }

        if ((rawType & EwkbSridFlag) != 0)
        {
            if (!TrySkip(input, ref offset, sizeof(uint)))
            {
                return false;
            }

            sridBytesRemoved += sizeof(uint);
        }

        if (!TryResolveType(rawType, out var geometryType, out var ordinateCount, out var nodeHasZ, out var nodeHasM))
        {
            return false;
        }

        hasZ |= nodeHasZ;
        hasM |= nodeHasM;

        if (expectedGeometryType.HasValue && geometryType != expectedGeometryType.Value)
        {
            return false;
        }

        return geometryType switch
        {
            1 => TrySkipCoordinates(input, ref offset, 1, ordinateCount),
            2 => TryInspectCoordinateSequence(input, ref offset, littleEndian, ordinateCount),
            3 => TryInspectPolygon(input, ref offset, littleEndian, ordinateCount),
            4 or 5 or 6 or 7 => TryInspectCollection(
                input,
                ref offset,
                littleEndian,
                geometryType == 7 ? null : geometryType - 3,
                ref sridBytesRemoved,
                ref hasZ,
                ref hasM,
                depth + 1,
                maximumDepth),
            _ => false
        };
    }

    private static bool TryInspectPolygon(
        ReadOnlySpan<byte> input,
        ref int offset,
        bool littleEndian,
        int ordinateCount)
    {
        if (!TryReadUInt32(input, ref offset, littleEndian, out var ringCount))
        {
            return false;
        }

        for (uint ring = 0; ring < ringCount; ring++)
        {
            if (!TryInspectCoordinateSequence(input, ref offset, littleEndian, ordinateCount))
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryInspectCollection(
        ReadOnlySpan<byte> input,
        ref int offset,
        bool littleEndian,
        uint? expectedChildType,
        ref int sridBytesRemoved,
        ref bool hasZ,
        ref bool hasM,
        int depth,
        int maximumDepth)
    {
        if (!TryReadUInt32(input, ref offset, littleEndian, out var geometryCount))
        {
            return false;
        }

        for (uint geometry = 0; geometry < geometryCount; geometry++)
        {
            if (!TryInspectGeometry(input, ref offset, ref sridBytesRemoved, ref hasZ, ref hasM,
                    expectedChildType, depth, maximumDepth))
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryInspectCoordinateSequence(
        ReadOnlySpan<byte> input,
        ref int offset,
        bool littleEndian,
        int ordinateCount)
    {
        return TryReadUInt32(input, ref offset, littleEndian, out var coordinateCount) &&
            TrySkipCoordinates(input, ref offset, coordinateCount, ordinateCount);
    }

    private static bool TrySkipCoordinates(
        ReadOnlySpan<byte> input,
        ref int offset,
        uint coordinateCount,
        int ordinateCount)
    {
        var byteCount = (long)coordinateCount * ordinateCount * sizeof(double);
        if (byteCount > int.MaxValue || !TrySkip(input, ref offset, (int)byteCount))
        {
            return false;
        }

        return true;
    }

    private enum WkbByteOrder : byte
    {
        BigEndian = 0,
        LittleEndian = 1,
    }

    private ref struct WkbSridStripper
    {
        private readonly ReadOnlySpan<byte> _input;
        private readonly Span<byte> _output;
        private int _readOffset;
        private int _writeOffset;

        public WkbSridStripper(ReadOnlySpan<byte> input, Span<byte> output)
        {
            _input = input;
            _output = output;
        }

        public void CopyGeometry()
        {
            var rawByteOrder = _input[_readOffset++];
            _output[_writeOffset++] = rawByteOrder;
            var byteOrder = rawByteOrder == 1 ? WkbByteOrder.LittleEndian : WkbByteOrder.BigEndian;

            var rawType = ReadAndRewriteGeometryType(byteOrder);
            TryResolveType(rawType, out var geometryType, out var ordinateCount);
            CopyGeometryPayload(geometryType, ordinateCount, byteOrder);
        }

        private uint ReadAndRewriteGeometryType(WkbByteOrder byteOrder)
        {
            var rawType = ReadUInt32(_input.Slice(_readOffset, sizeof(uint)), byteOrder);
            _readOffset += sizeof(uint);

            WriteUInt32(_output.Slice(_writeOffset, sizeof(uint)), rawType & ~EwkbSridFlag, byteOrder);
            _writeOffset += sizeof(uint);

            if ((rawType & EwkbSridFlag) != 0)
            {
                _readOffset += sizeof(uint);
            }

            return rawType;
        }

        private void CopyGeometryPayload(uint geometryType, int ordinateCount, WkbByteOrder byteOrder)
        {
            switch (geometryType)
            {
                case 1:
                    CopyBytes(ordinateCount * sizeof(double));
                    break;
                case 2:
                    CopyCoordinateSequence(byteOrder, ordinateCount);
                    break;
                case 3:
                    CopyPolygonRings(byteOrder, ordinateCount);
                    break;
                case 4 or 5 or 6 or 7:
                    CopyCollectionChildren(byteOrder);
                    break;
            }
        }

        private void CopyPolygonRings(WkbByteOrder byteOrder, int ordinateCount)
        {
            var ringCount = CopyCount(byteOrder);
            for (uint ring = 0; ring < ringCount; ring++)
            {
                CopyCoordinateSequence(byteOrder, ordinateCount);
            }
        }

        private void CopyCollectionChildren(WkbByteOrder byteOrder)
        {
            var geometryCount = CopyCount(byteOrder);
            for (uint geometry = 0; geometry < geometryCount; geometry++)
            {
                CopyGeometry();
            }
        }

        private void CopyCoordinateSequence(WkbByteOrder byteOrder, int ordinateCount)
        {
            var coordinateCount = CopyCount(byteOrder);
            var byteCount = (int)((long)coordinateCount * ordinateCount * sizeof(double));
            CopyBytes(byteCount);
        }

        private uint CopyCount(WkbByteOrder byteOrder)
        {
            var slice = _input.Slice(_readOffset, sizeof(uint));
            var count = ReadUInt32(slice, byteOrder);
            slice.CopyTo(_output.Slice(_writeOffset, sizeof(uint)));
            _readOffset += sizeof(uint);
            _writeOffset += sizeof(uint);
            return count;
        }

        private void CopyBytes(int byteCount)
        {
            _input.Slice(_readOffset, byteCount).CopyTo(_output.Slice(_writeOffset, byteCount));
            _readOffset += byteCount;
            _writeOffset += byteCount;
        }
    }

    private static bool TryResolveType(uint rawType, out uint geometryType, out int ordinateCount)
        => TryResolveType(rawType, out geometryType, out ordinateCount, out _, out _);

    private static bool TryResolveType(
        uint rawType, out uint geometryType, out int ordinateCount, out bool hasZ, out bool hasM)
    {
        hasZ = false;
        hasM = false;
        var type = rawType & ~EwkbFlagMask;
        var isoDimension = type / 1000;
        if (isoDimension is > 3)
        {
            geometryType = 0;
            ordinateCount = 0;
            return false;
        }

        geometryType = type % 1000;
        hasZ = (rawType & EwkbZFlag) != 0 || isoDimension is 1 or 3;
        hasM = (rawType & EwkbMFlag) != 0 || isoDimension is 2 or 3;
        ordinateCount = 2 + (hasZ ? 1 : 0) + (hasM ? 1 : 0);
        return geometryType is >= 1 and <= 7;
    }

    private static bool TryReadByteOrder(ReadOnlySpan<byte> input, ref int offset, out bool littleEndian)
    {
        if (offset >= input.Length || input[offset] is not (0 or 1))
        {
            littleEndian = false;
            return false;
        }

        littleEndian = input[offset++] == 1;
        return true;
    }

    private static bool TryReadUInt32(ReadOnlySpan<byte> input, ref int offset, bool littleEndian, out uint value)
    {
        if (input.Length - offset < sizeof(uint))
        {
            value = 0;
            return false;
        }

        var byteOrder = littleEndian ? WkbByteOrder.LittleEndian : WkbByteOrder.BigEndian;
        value = ReadUInt32(input.Slice(offset, sizeof(uint)), byteOrder);
        offset += sizeof(uint);
        return true;
    }

    private static uint ReadUInt32(ReadOnlySpan<byte> bytes, WkbByteOrder byteOrder)
        => byteOrder == WkbByteOrder.LittleEndian
            ? BinaryPrimitives.ReadUInt32LittleEndian(bytes)
            : BinaryPrimitives.ReadUInt32BigEndian(bytes);

    private static void WriteUInt32(Span<byte> destination, uint value, WkbByteOrder byteOrder)
    {
        if (byteOrder == WkbByteOrder.LittleEndian)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(destination, value);
        }
        else
        {
            BinaryPrimitives.WriteUInt32BigEndian(destination, value);
        }
    }

    private static bool TrySkip(ReadOnlySpan<byte> input, ref int offset, int byteCount)
    {
        if (byteCount < 0 || input.Length - offset < byteCount)
        {
            return false;
        }

        offset += byteCount;
        return true;
    }
}
