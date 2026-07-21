// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Buffers.Binary;

namespace Honua.Core.Features.FeatureStore.Services;

/// <summary>
/// Removes embedded EWKB SRID words while preserving geometry payload bytes exactly.
/// </summary>
internal static class WkbSridNormalizer
{
    private const uint EwkbZFlag = 0x80000000;
    private const uint EwkbMFlag = 0x40000000;
    private const uint EwkbSridFlag = 0x20000000;
    private const uint EwkbFlagMask = EwkbZFlag | EwkbMFlag | EwkbSridFlag;

    /// <summary>
    /// Returns structurally valid WKB/EWKB with every embedded SRID word removed. Z/M flags,
    /// coordinate bytes, byte order, counts, and all other payload bytes are copied unchanged.
    /// Invalid, unsupported, truncated, or trailing-junk inputs are returned by reference so the
    /// database parser remains responsible for the error instead of this boundary repairing it.
    /// </summary>
    public static byte[] RemoveEmbeddedSrid(byte[] wkb)
    {
        ArgumentNullException.ThrowIfNull(wkb);

        using var output = new MemoryStream(wkb.Length);
        var offset = 0;
        var changed = false;
        if (!TryCopyGeometry(wkb, ref offset, output, ref changed) || offset != wkb.Length)
        {
            return wkb;
        }

        return changed ? output.ToArray() : wkb;
    }

    private static bool TryCopyGeometry(
        byte[] input,
        ref int offset,
        MemoryStream output,
        ref bool changed,
        uint? expectedGeometryType = null)
    {
        if (!TryReadByteOrder(input, ref offset, output, out var littleEndian) ||
            !TryReadUInt32(input, ref offset, littleEndian, out var rawType))
        {
            return false;
        }

        var hasSrid = (rawType & EwkbSridFlag) != 0;
        WriteUInt32(output, rawType & ~EwkbSridFlag, littleEndian);

        if (hasSrid)
        {
            if (!TrySkip(input, ref offset, sizeof(uint)))
            {
                return false;
            }

            changed = true;
        }

        if (!TryResolveType(rawType, out var geometryType, out var ordinateCount))
        {
            return false;
        }

        if (expectedGeometryType.HasValue && geometryType != expectedGeometryType.Value)
        {
            return false;
        }

        return geometryType switch
        {
            1 => TryCopyCoordinates(input, ref offset, output, 1, ordinateCount),
            2 => TryCopyCoordinateSequence(input, ref offset, output, littleEndian, ordinateCount),
            3 => TryCopyPolygon(input, ref offset, output, littleEndian, ordinateCount),
            4 or 5 or 6 or 7 => TryCopyCollection(
                input,
                ref offset,
                output,
                littleEndian,
                geometryType == 7 ? null : geometryType - 3,
                ref changed),
            _ => false
        };
    }

    private static bool TryCopyPolygon(
        byte[] input,
        ref int offset,
        MemoryStream output,
        bool littleEndian,
        int ordinateCount)
    {
        if (!TryReadAndCopyCount(input, ref offset, output, littleEndian, out var ringCount))
        {
            return false;
        }

        for (uint ring = 0; ring < ringCount; ring++)
        {
            if (!TryCopyCoordinateSequence(input, ref offset, output, littleEndian, ordinateCount))
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryCopyCollection(
        byte[] input,
        ref int offset,
        MemoryStream output,
        bool littleEndian,
        uint? expectedChildType,
        ref bool changed)
    {
        if (!TryReadAndCopyCount(input, ref offset, output, littleEndian, out var geometryCount))
        {
            return false;
        }

        for (uint geometry = 0; geometry < geometryCount; geometry++)
        {
            if (!TryCopyGeometry(input, ref offset, output, ref changed, expectedChildType))
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryCopyCoordinateSequence(
        byte[] input,
        ref int offset,
        MemoryStream output,
        bool littleEndian,
        int ordinateCount)
    {
        return TryReadAndCopyCount(input, ref offset, output, littleEndian, out var coordinateCount) &&
            TryCopyCoordinates(input, ref offset, output, coordinateCount, ordinateCount);
    }

    private static bool TryCopyCoordinates(
        byte[] input,
        ref int offset,
        MemoryStream output,
        uint coordinateCount,
        int ordinateCount)
    {
        var byteCount = (long)coordinateCount * ordinateCount * sizeof(double);
        if (byteCount > int.MaxValue || !TryCopy(input, ref offset, output, (int)byteCount))
        {
            return false;
        }

        return true;
    }

    private static bool TryResolveType(uint rawType, out uint geometryType, out int ordinateCount)
    {
        var type = rawType & ~EwkbFlagMask;
        var isoDimension = type / 1000;
        if (isoDimension is > 3)
        {
            geometryType = 0;
            ordinateCount = 0;
            return false;
        }

        geometryType = type % 1000;
        var hasZ = (rawType & EwkbZFlag) != 0 || isoDimension is 1 or 3;
        var hasM = (rawType & EwkbMFlag) != 0 || isoDimension is 2 or 3;
        ordinateCount = 2 + (hasZ ? 1 : 0) + (hasM ? 1 : 0);
        return geometryType is >= 1 and <= 7;
    }

    private static bool TryReadByteOrder(
        byte[] input,
        ref int offset,
        MemoryStream output,
        out bool littleEndian)
    {
        if (offset >= input.Length || input[offset] is not (0 or 1))
        {
            littleEndian = false;
            return false;
        }

        var byteOrder = input[offset++];
        output.WriteByte(byteOrder);
        littleEndian = byteOrder == 1;
        return true;
    }

    private static bool TryReadAndCopyCount(
        byte[] input,
        ref int offset,
        MemoryStream output,
        bool littleEndian,
        out uint count)
    {
        if (!TryReadUInt32(input, ref offset, littleEndian, out count))
        {
            return false;
        }

        WriteUInt32(output, count, littleEndian);
        return true;
    }

    private static bool TryReadUInt32(byte[] input, ref int offset, bool littleEndian, out uint value)
    {
        if (input.Length - offset < sizeof(uint))
        {
            value = 0;
            return false;
        }

        var bytes = input.AsSpan(offset, sizeof(uint));
        value = littleEndian
            ? BinaryPrimitives.ReadUInt32LittleEndian(bytes)
            : BinaryPrimitives.ReadUInt32BigEndian(bytes);
        offset += sizeof(uint);
        return true;
    }

    private static void WriteUInt32(MemoryStream output, uint value, bool littleEndian)
    {
        Span<byte> bytes = stackalloc byte[sizeof(uint)];
        if (littleEndian)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        }
        else
        {
            BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        }

        output.Write(bytes);
    }

    private static bool TryCopy(byte[] input, ref int offset, MemoryStream output, int byteCount)
    {
        if (byteCount < 0 || input.Length - offset < byteCount)
        {
            return false;
        }

        output.Write(input, offset, byteCount);
        offset += byteCount;
        return true;
    }

    private static bool TrySkip(byte[] input, ref int offset, int byteCount)
    {
        if (input.Length - offset < byteCount)
        {
            return false;
        }

        offset += byteCount;
        return true;
    }
}
