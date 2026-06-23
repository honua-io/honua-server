// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Buffers.Binary;
using Honua.Core.Features.Raster.Domain;

namespace Honua.Core.Features.Raster.ZarrParser;

/// <summary>
/// Decodes scalar values from a little-endian <see cref="ZarrSubsetResult"/>
/// payload. Used by point-sampling callers (e.g. ImageServer getSamples) that
/// read a single pinned cell and need its numeric value.
/// </summary>
public static class ZarrValueDecoder
{
    /// <summary>
    /// Decodes the first element of a subset payload to a double. Returns false
    /// when the payload is empty or the dtype is unsupported.
    /// </summary>
    public static bool TryDecodeFirst(ZarrSubsetResult result, out double value)
    {
        ArgumentNullException.ThrowIfNull(result);
        return TryDecode(result.Data, result.DataType, out value);
    }

    /// <summary>
    /// Decodes the first element of a little-endian buffer of the given numpy dtype.
    /// </summary>
    public static bool TryDecode(ReadOnlySpan<byte> data, string dataType, out double value)
    {
        value = double.NaN;
        var normalized = NormalizeDataType(dataType);
        switch (normalized)
        {
            case "f4":
                if (data.Length < 4) return false;
                value = BinaryPrimitives.ReadSingleLittleEndian(data);
                return true;
            case "f8":
                if (data.Length < 8) return false;
                value = BinaryPrimitives.ReadDoubleLittleEndian(data);
                return true;
            case "i1":
                if (data.Length < 1) return false;
                value = (sbyte)data[0];
                return true;
            case "u1":
            case "b1":
                if (data.Length < 1) return false;
                value = data[0];
                return true;
            case "i2":
                if (data.Length < 2) return false;
                value = BinaryPrimitives.ReadInt16LittleEndian(data);
                return true;
            case "u2":
                if (data.Length < 2) return false;
                value = BinaryPrimitives.ReadUInt16LittleEndian(data);
                return true;
            case "i4":
                if (data.Length < 4) return false;
                value = BinaryPrimitives.ReadInt32LittleEndian(data);
                return true;
            case "u4":
                if (data.Length < 4) return false;
                value = BinaryPrimitives.ReadUInt32LittleEndian(data);
                return true;
            case "i8":
                if (data.Length < 8) return false;
                value = BinaryPrimitives.ReadInt64LittleEndian(data);
                return true;
            case "u8":
                if (data.Length < 8) return false;
                value = BinaryPrimitives.ReadUInt64LittleEndian(data);
                return true;
            default:
                return false;
        }
    }

    private static string NormalizeDataType(string dtype)
        => dtype.Length > 0 && dtype[0] is '<' or '|' or '=' ? dtype[1..] : dtype;
}
