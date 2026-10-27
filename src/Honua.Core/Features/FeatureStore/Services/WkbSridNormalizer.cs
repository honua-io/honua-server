// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Buffers.Binary;

namespace Honua.Core.Features.FeatureStore.Services;

/// <summary>
/// Inspects WKB dimensions and removes embedded EWKB SRID words without changing coordinate bytes.
/// </summary>
public static class WkbSridNormalizer
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
        var hasZ = false;
        var hasM = false;
        if (!TryCopyGeometry(wkb, ref offset, output, ref changed, ref hasZ, ref hasM) || offset != wkb.Length)
        {
            return wkb;
        }

        return changed ? output.ToArray() : wkb;
    }

    /// <summary>
    /// Validates the complete WKB/EWKB payload and reports declared Z and M ordinates across
    /// every geometry node, including empty geometries. Supports geometry types 1 through 7
    /// and at most 128 nested geometry levels. Malformed, unsupported, over-depth, or
    /// trailing-junk input returns false with both flags reset; coordinate values are not inferred.
    /// </summary>
    public static bool TryGetDeclaredOrdinates(byte[] wkb, out bool hasZ, out bool hasM)
    {
        hasZ = false;
        hasM = false;
        if (wkb == null)
        {
            return false;
        }

        var offset = 0;
        var changed = false;
        if (TryCopyGeometry(wkb, ref offset, Stream.Null, ref changed, ref hasZ, ref hasM,
                maximumDepth: 128) && offset == wkb.Length)
        {
            return true;
        }

        hasZ = false;
        hasM = false;
        return false;
    }

    /// <summary>
    /// Reports whether the outermost geometry declares a Z and/or M ordinate, via either the
    /// PostGIS-style EWKB high-bit flags or the ISO SQL/MM type-code offset. Callers that bind
    /// into a provider WKB constructor with no Z/M-flavor translation of its own (MySQL's
    /// canonical-2D <c>ST_GeomFromWKB</c>, SQL Server's ISO/OGC-flavored <c>STGeomFromWKB</c>)
    /// should reject dimensioned input here instead of binding a payload the engine cannot parse
    /// the way <see cref="RemoveEmbeddedSrid"/> emits it (SRID stripped, dimension flags
    /// otherwise byte-preserved as-is). Malformed input reports <see langword="false"/> so the
    /// database parser stays responsible for the resulting error.
    /// </summary>
    public static bool HasZOrMOrdinates(byte[] wkb)
    {
        ArgumentNullException.ThrowIfNull(wkb);

        var offset = 0;
        if (!TryReadByteOrderRaw(wkb, ref offset, out var littleEndian) ||
            !TryReadUInt32Raw(wkb, ref offset, littleEndian, out var rawType))
        {
            return false;
        }

        if (!TryResolveType(rawType, out _, out var ordinateCount))
        {
            return false;
        }

        return ordinateCount > 2;
    }

    private static bool TryReadByteOrderRaw(byte[] input, ref int offset, out bool littleEndian)
    {
        if (offset >= input.Length || input[offset] is not (0 or 1))
        {
            littleEndian = false;
            return false;
        }

        littleEndian = input[offset++] == 1;
        return true;
    }

    private static bool TryReadUInt32Raw(byte[] input, ref int offset, bool littleEndian, out uint value)
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

    private static bool TryCopyGeometry(
        byte[] input,
        ref int offset,
        Stream output,
        ref bool changed,
        ref bool hasZ,
        ref bool hasM,
        uint? expectedGeometryType = null,
        int depth = 0,
        int maximumDepth = int.MaxValue)
    {
        if (depth >= maximumDepth ||
            !TryReadByteOrder(input, ref offset, output, out var littleEndian) ||
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
            1 => TryCopyCoordinates(input, ref offset, output, 1, ordinateCount),
            2 => TryCopyCoordinateSequence(input, ref offset, output, littleEndian, ordinateCount),
            3 => TryCopyPolygon(input, ref offset, output, littleEndian, ordinateCount),
            4 or 5 or 6 or 7 => TryCopyCollection(
                input,
                ref offset,
                output,
                littleEndian,
                geometryType == 7 ? null : geometryType - 3,
                ref changed,
                ref hasZ,
                ref hasM,
                depth + 1,
                maximumDepth),
            _ => false
        };
    }

    private static bool TryCopyPolygon(
        byte[] input,
        ref int offset,
        Stream output,
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
        Stream output,
        bool littleEndian,
        uint? expectedChildType,
        ref bool changed,
        ref bool hasZ,
        ref bool hasM,
        int depth,
        int maximumDepth)
    {
        if (!TryReadAndCopyCount(input, ref offset, output, littleEndian, out var geometryCount))
        {
            return false;
        }

        for (uint geometry = 0; geometry < geometryCount; geometry++)
        {
            if (!TryCopyGeometry(input, ref offset, output, ref changed, ref hasZ, ref hasM,
                    expectedChildType, depth, maximumDepth))
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryCopyCoordinateSequence(
        byte[] input,
        ref int offset,
        Stream output,
        bool littleEndian,
        int ordinateCount)
    {
        return TryReadAndCopyCount(input, ref offset, output, littleEndian, out var coordinateCount) &&
            TryCopyCoordinates(input, ref offset, output, coordinateCount, ordinateCount);
    }

    private static bool TryCopyCoordinates(
        byte[] input,
        ref int offset,
        Stream output,
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

    private static bool TryReadByteOrder(
        byte[] input,
        ref int offset,
        Stream output,
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
        Stream output,
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

    private static void WriteUInt32(Stream output, uint value, bool littleEndian)
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

    private static bool TryCopy(byte[] input, ref int offset, Stream output, int byteCount)
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
