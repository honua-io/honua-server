// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Honua.Core.Features.Raster.Domain;

namespace Honua.Core.Features.Raster.ZarrParser;

public static class ZarrValueDecoder
{
    public static bool TryDecodeFirst(ZarrSubsetResult result, out double value)
    {
        ArgumentNullException.ThrowIfNull(result);
        return TryDecode(result.Data, result.DataType, out value);
    }

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
        var kind = NormalizeDataType(dtype);
        switch (kind)
        {
            case "i1":
                if (data.Length < 1) return false;
                var i1 = (sbyte)data[0];
                if (fillValue is not null && TrySignedFill(fillValue, out var i1Fill) && i1 == i1Fill) return true;
                value = i1;
                return true;
            case "i2":
                if (data.Length < 2) return false;
                var i2 = BinaryPrimitives.ReadInt16LittleEndian(data);
                if (fillValue is not null && TrySignedFill(fillValue, out var i2Fill) && i2 == i2Fill) return true;
                value = i2;
                return true;
            case "i4":
                if (data.Length < 4) return false;
                var i4 = BinaryPrimitives.ReadInt32LittleEndian(data);
                if (fillValue is not null && TrySignedFill(fillValue, out var i4Fill) && i4 == i4Fill) return true;
                value = i4;
                return true;
            case "i8":
                if (data.Length < 8) return false;
                var i8 = BinaryPrimitives.ReadInt64LittleEndian(data);
                if (fillValue is not null && TrySignedFill(fillValue, out var i8Fill) && i8 == i8Fill) return true;
                value = i8;
                return true;
            case "b1":
            case "u1":
                if (data.Length < 1) return false;
                var u1 = data[0];
                if (fillValue is not null && TryUnsignedFill(fillValue, out var u1Fill) && u1 == u1Fill) return true;
                value = u1;
                return true;
            case "u2":
                if (data.Length < 2) return false;
                var u2 = BinaryPrimitives.ReadUInt16LittleEndian(data);
                if (fillValue is not null && TryUnsignedFill(fillValue, out var u2Fill) && u2 == u2Fill) return true;
                value = u2;
                return true;
            case "u4":
                if (data.Length < 4) return false;
                var u4 = BinaryPrimitives.ReadUInt32LittleEndian(data);
                if (fillValue is not null && TryUnsignedFill(fillValue, out var u4Fill) && u4 == u4Fill) return true;
                value = u4;
                return true;
            case "u8":
                if (data.Length < 8) return false;
                var u8 = BinaryPrimitives.ReadUInt64LittleEndian(data);
                if (fillValue is not null && TryUnsignedFill(fillValue, out var u8Fill) && u8 == u8Fill) return true;
                value = u8;
                return true;
            case "f4":
                if (data.Length < 4) return false;
                double f4 = BinaryPrimitives.ReadSingleLittleEndian(data);
                if (fillValue is not null && TryGetFiniteFillValue(fillValue, out var f4Fill) && f4.CompareTo(f4Fill) == 0) return true;
                if (double.IsFinite(f4)) value = f4;
                return true;
            case "f8":
                if (data.Length < 8) return false;
                var f8 = BinaryPrimitives.ReadDoubleLittleEndian(data);
                if (fillValue is not null && TryGetFiniteFillValue(fillValue, out var f8Fill) && f8.CompareTo(f8Fill) == 0) return true;
                if (double.IsFinite(f8)) value = f8;
                return true;
            default:
                return false;
        }
    }

    internal static bool TryDecodeBufferWithFill(
        ReadOnlySpan<byte> data,
        string normalizedDtype,
        object? fillValue,
        Span<double> destination)
    {
        var count = destination.Length;
        switch (normalizedDtype)
        {
            case "i1":
                if (data.Length < count) return false;
                DecodeSignedByteBuffer(data[..count], fillValue, destination);
                return true;
            case "u1":
            case "b1":
                if (data.Length < count) return false;
                DecodeUnsignedByteBuffer(data[..count], fillValue, destination);
                return true;
            case "i2":
                if (data.Length < count * 2) return false;
                DecodeInt16Buffer(data[..(count * 2)], fillValue, destination);
                return true;
            case "u2":
                if (data.Length < count * 2) return false;
                DecodeUInt16Buffer(data[..(count * 2)], fillValue, destination);
                return true;
            case "i4":
                if (data.Length < count * 4) return false;
                DecodeInt32Buffer(data[..(count * 4)], fillValue, destination);
                return true;
            case "u4":
                if (data.Length < count * 4) return false;
                DecodeUInt32Buffer(data[..(count * 4)], fillValue, destination);
                return true;
            case "i8":
                if (data.Length < count * 8) return false;
                DecodeInt64Buffer(data[..(count * 8)], fillValue, destination);
                return true;
            case "u8":
                if (data.Length < count * 8) return false;
                DecodeUInt64Buffer(data[..(count * 8)], fillValue, destination);
                return true;
            case "f4":
                if (data.Length < count * 4) return false;
                DecodeFloat32Buffer(data[..(count * 4)], fillValue, destination);
                return true;
            case "f8":
                if (data.Length < count * 8) return false;
                DecodeFloat64Buffer(data[..(count * 8)], fillValue, destination);
                return true;
            default:
                return false;
        }
    }

    private static void DecodeSignedByteBuffer(ReadOnlySpan<byte> bytes, object? fillValue, Span<double> destination)
    {
        var src = MemoryMarshal.Cast<byte, sbyte>(bytes);
        if (fillValue is not null && TrySignedFill(fillValue, out var fill))
        {
            for (var i = 0; i < destination.Length; i++)
            {
                var v = src[i];
                destination[i] = v == fill ? double.NaN : v;
            }
        }
        else
        {
            for (var i = 0; i < destination.Length; i++)
            {
                destination[i] = src[i];
            }
        }
    }

    private static void DecodeUnsignedByteBuffer(ReadOnlySpan<byte> bytes, object? fillValue, Span<double> destination)
    {
        if (fillValue is not null && TryUnsignedFill(fillValue, out var fill))
        {
            for (var i = 0; i < destination.Length; i++)
            {
                var v = bytes[i];
                destination[i] = v == fill ? double.NaN : v;
            }
        }
        else
        {
            for (var i = 0; i < destination.Length; i++)
            {
                destination[i] = bytes[i];
            }
        }
    }

    private static void DecodeInt16Buffer(ReadOnlySpan<byte> bytes, object? fillValue, Span<double> destination)
    {
        long fill = 0;
        var hasFill = fillValue is not null && TrySignedFill(fillValue, out fill);
        if (BitConverter.IsLittleEndian)
        {
            var src = MemoryMarshal.Cast<byte, short>(bytes);
            if (hasFill)
            {
                for (var i = 0; i < destination.Length; i++)
                {
                    var v = src[i];
                    destination[i] = v == fill ? double.NaN : v;
                }
            }
            else
            {
                for (var i = 0; i < destination.Length; i++)
                {
                    destination[i] = src[i];
                }
            }
            return;
        }

        for (var i = 0; i < destination.Length; i++)
        {
            var v = BinaryPrimitives.ReadInt16LittleEndian(bytes.Slice(i * 2, 2));
            destination[i] = hasFill && v == fill ? double.NaN : v;
        }
    }

    private static void DecodeUInt16Buffer(ReadOnlySpan<byte> bytes, object? fillValue, Span<double> destination)
    {
        ulong fill = 0;
        var hasFill = fillValue is not null && TryUnsignedFill(fillValue, out fill);
        if (BitConverter.IsLittleEndian)
        {
            var src = MemoryMarshal.Cast<byte, ushort>(bytes);
            if (hasFill)
            {
                for (var i = 0; i < destination.Length; i++)
                {
                    var v = src[i];
                    destination[i] = v == fill ? double.NaN : v;
                }
            }
            else
            {
                for (var i = 0; i < destination.Length; i++)
                {
                    destination[i] = src[i];
                }
            }
            return;
        }

        for (var i = 0; i < destination.Length; i++)
        {
            var v = BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(i * 2, 2));
            destination[i] = hasFill && v == fill ? double.NaN : v;
        }
    }

    private static void DecodeInt32Buffer(ReadOnlySpan<byte> bytes, object? fillValue, Span<double> destination)
    {
        long fill = 0;
        var hasFill = fillValue is not null && TrySignedFill(fillValue, out fill);
        if (BitConverter.IsLittleEndian)
        {
            var src = MemoryMarshal.Cast<byte, int>(bytes);
            if (hasFill)
            {
                for (var i = 0; i < destination.Length; i++)
                {
                    var v = src[i];
                    destination[i] = v == fill ? double.NaN : v;
                }
            }
            else
            {
                for (var i = 0; i < destination.Length; i++)
                {
                    destination[i] = src[i];
                }
            }
            return;
        }

        for (var i = 0; i < destination.Length; i++)
        {
            var v = BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(i * 4, 4));
            destination[i] = hasFill && v == fill ? double.NaN : v;
        }
    }

    private static void DecodeUInt32Buffer(ReadOnlySpan<byte> bytes, object? fillValue, Span<double> destination)
    {
        ulong fill = 0;
        var hasFill = fillValue is not null && TryUnsignedFill(fillValue, out fill);
        if (BitConverter.IsLittleEndian)
        {
            var src = MemoryMarshal.Cast<byte, uint>(bytes);
            if (hasFill)
            {
                for (var i = 0; i < destination.Length; i++)
                {
                    var v = src[i];
                    destination[i] = v == fill ? double.NaN : v;
                }
            }
            else
            {
                for (var i = 0; i < destination.Length; i++)
                {
                    destination[i] = src[i];
                }
            }
            return;
        }

        for (var i = 0; i < destination.Length; i++)
        {
            var v = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(i * 4, 4));
            destination[i] = hasFill && v == fill ? double.NaN : v;
        }
    }

    private static void DecodeInt64Buffer(ReadOnlySpan<byte> bytes, object? fillValue, Span<double> destination)
    {
        long fill = 0;
        var hasFill = fillValue is not null && TrySignedFill(fillValue, out fill);
        if (BitConverter.IsLittleEndian)
        {
            var src = MemoryMarshal.Cast<byte, long>(bytes);
            if (hasFill)
            {
                for (var i = 0; i < destination.Length; i++)
                {
                    var v = src[i];
                    destination[i] = v == fill ? double.NaN : v;
                }
            }
            else
            {
                for (var i = 0; i < destination.Length; i++)
                {
                    destination[i] = src[i];
                }
            }
            return;
        }

        for (var i = 0; i < destination.Length; i++)
        {
            var v = BinaryPrimitives.ReadInt64LittleEndian(bytes.Slice(i * 8, 8));
            destination[i] = hasFill && v == fill ? double.NaN : v;
        }
    }

    private static void DecodeUInt64Buffer(ReadOnlySpan<byte> bytes, object? fillValue, Span<double> destination)
    {
        ulong fill = 0;
        var hasFill = fillValue is not null && TryUnsignedFill(fillValue, out fill);
        if (BitConverter.IsLittleEndian)
        {
            var src = MemoryMarshal.Cast<byte, ulong>(bytes);
            if (hasFill)
            {
                for (var i = 0; i < destination.Length; i++)
                {
                    var v = src[i];
                    destination[i] = v == fill ? double.NaN : v;
                }
            }
            else
            {
                for (var i = 0; i < destination.Length; i++)
                {
                    destination[i] = src[i];
                }
            }
            return;
        }

        for (var i = 0; i < destination.Length; i++)
        {
            var v = BinaryPrimitives.ReadUInt64LittleEndian(bytes.Slice(i * 8, 8));
            destination[i] = hasFill && v == fill ? double.NaN : v;
        }
    }

    private static void DecodeFloat32Buffer(ReadOnlySpan<byte> bytes, object? fillValue, Span<double> destination)
    {
        double fill = 0;
        var hasFill = fillValue is not null && TryGetFiniteFillValue(fillValue, out fill);
        if (BitConverter.IsLittleEndian)
        {
            var src = MemoryMarshal.Cast<byte, float>(bytes);
            if (hasFill)
            {
                for (var i = 0; i < destination.Length; i++)
                {
                    double v = src[i];
                    destination[i] = v.CompareTo(fill) == 0 || !double.IsFinite(v) ? double.NaN : v;
                }
            }
            else
            {
                for (var i = 0; i < destination.Length; i++)
                {
                    double v = src[i];
                    destination[i] = double.IsFinite(v) ? v : double.NaN;
                }
            }
            return;
        }

        for (var i = 0; i < destination.Length; i++)
        {
            double v = BinaryPrimitives.ReadSingleLittleEndian(bytes.Slice(i * 4, 4));
            destination[i] = (hasFill && v.CompareTo(fill) == 0) || !double.IsFinite(v) ? double.NaN : v;
        }
    }

    private static void DecodeFloat64Buffer(ReadOnlySpan<byte> bytes, object? fillValue, Span<double> destination)
    {
        double fill = 0;
        var hasFill = fillValue is not null && TryGetFiniteFillValue(fillValue, out fill);
        if (BitConverter.IsLittleEndian)
        {
            var src = MemoryMarshal.Cast<byte, double>(bytes);
            if (hasFill)
            {
                for (var i = 0; i < destination.Length; i++)
                {
                    var v = src[i];
                    destination[i] = v.CompareTo(fill) == 0 || !double.IsFinite(v) ? double.NaN : v;
                }
            }
            else
            {
                for (var i = 0; i < destination.Length; i++)
                {
                    var v = src[i];
                    destination[i] = double.IsFinite(v) ? v : double.NaN;
                }
            }
            return;
        }

        for (var i = 0; i < destination.Length; i++)
        {
            var v = BinaryPrimitives.ReadDoubleLittleEndian(bytes.Slice(i * 8, 8));
            destination[i] = (hasFill && v.CompareTo(fill) == 0) || !double.IsFinite(v) ? double.NaN : v;
        }
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
