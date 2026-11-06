// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

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

    /// <summary>
    /// Converts a typed fill value for numeric metadata surfaces. Non-finite values
    /// and integers that cannot be represented exactly as doubles are omitted.
    /// Binary subset synthesis preserves their original dtype and bits separately.
    /// </summary>
    public static bool TryGetFiniteFillValue(object? fillValue, out double value)
    {
        value = double.NaN;
        switch (fillValue)
        {
            case bool boolean: value = boolean ? 1 : 0; break;
            case float single: value = single; break;
            case double number: value = number; break;
            case long signed:
                value = signed;
                try { if (checked((long)value) != signed) return false; }
                catch (OverflowException) { return false; }
                break;
            case ulong unsigned:
                value = unsigned;
                try { if (checked((ulong)value) != unsigned) return false; }
                catch (OverflowException) { return false; }
                break;
            default: return false;
        }
        return double.IsFinite(value);
    }

    // Honua's nodata policy is explicit at each caller. Compare source scalars before
    // widening integers: adjacent int64/uint64 values can share one double value.
    internal static bool TryDecodeWithFill(ReadOnlySpan<byte> data, string dtype, object? fillValue, out double? value)
    {
        value = null;
        if (IsFillValue(data, dtype, fillValue)) return true;
        if (!TryDecode(data, dtype, out var decoded)) return false;
        if (double.IsFinite(decoded)) value = decoded;
        return true;
    }

    private static bool IsFillValue(ReadOnlySpan<byte> data, string dtype, object? fill)
    {
        if (fill is null) return false;
        var kind = NormalizeDataType(dtype);
        var size = kind switch
        {
            "b1" or "i1" or "u1" => 1,
            "i2" or "u2" => 2,
            "i4" or "u4" or "f4" => 4,
            "i8" or "u8" or "f8" => 8,
            _ => 0
        };
        if (size == 0 || data.Length < size) return false;
        return kind switch
        {
            "i1" => TrySignedFill(fill, out var sentinel) && (sbyte)data[0] == sentinel,
            "i2" => TrySignedFill(fill, out var sentinel) && BinaryPrimitives.ReadInt16LittleEndian(data) == sentinel,
            "i4" => TrySignedFill(fill, out var sentinel) && BinaryPrimitives.ReadInt32LittleEndian(data) == sentinel,
            "i8" => TrySignedFill(fill, out var sentinel) && BinaryPrimitives.ReadInt64LittleEndian(data) == sentinel,
            "b1" or "u1" => TryUnsignedFill(fill, out var sentinel) && data[0] == sentinel,
            "u2" => TryUnsignedFill(fill, out var sentinel) && BinaryPrimitives.ReadUInt16LittleEndian(data) == sentinel,
            "u4" => TryUnsignedFill(fill, out var sentinel) && BinaryPrimitives.ReadUInt32LittleEndian(data) == sentinel,
            "u8" => TryUnsignedFill(fill, out var sentinel) && BinaryPrimitives.ReadUInt64LittleEndian(data) == sentinel,
            "f4" => TryGetFiniteFillValue(fill, out var sentinel) && ((double)BinaryPrimitives.ReadSingleLittleEndian(data)).Equals(sentinel),
            "f8" => TryGetFiniteFillValue(fill, out var sentinel) && BinaryPrimitives.ReadDoubleLittleEndian(data).Equals(sentinel),
            _ => false
        };
    }

    private static bool TrySignedFill(object fill, out long value)
    {
        switch (fill)
        {
            case long number: value = number; return true;
            case ulong number when number <= long.MaxValue: value = (long)number; return true;
            case bool boolean: value = boolean ? 1 : 0; return true;
            case float number: return TrySignedNumber(number, out value);
            case double number: return TrySignedNumber(number, out value);
            default: value = 0; return false;
        }
    }

    private static bool TrySignedNumber(double number, out long value)
    {
        // Upper bound is exclusive: (double)long.MaxValue is already 2^63.
        if (number >= -9223372036854775808d && number < 9223372036854775808d && number == Math.Truncate(number))
        {
            value = (long)number;
            return true;
        }
        value = 0;
        return false;
    }

    private static bool TryUnsignedFill(object fill, out ulong value)
    {
        switch (fill)
        {
            case ulong number: value = number; return true;
            case long number when number >= 0: value = (ulong)number; return true;
            case bool boolean: value = boolean ? 1UL : 0UL; return true;
            case float number: return TryUnsignedNumber(number, out value);
            case double number: return TryUnsignedNumber(number, out value);
            default: value = 0; return false;
        }
    }

    private static bool TryUnsignedNumber(double number, out ulong value)
    {
        // Upper bound is exclusive: (double)ulong.MaxValue is already 2^64.
        if (number >= 0 && number < 18446744073709551616d && number == Math.Truncate(number))
        {
            value = (ulong)number;
            return true;
        }
        value = 0;
        return false;
    }

    private static string NormalizeDataType(string dtype)
        => dtype.Length > 0 && dtype[0] is '<' or '|' or '=' ? dtype[1..] : dtype;
}
