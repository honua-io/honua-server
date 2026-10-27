// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Buffers.Binary;
using System.Globalization;
using System.Text.Json;

namespace Honua.Core.Features.Raster.ZarrParser;

/// <summary>Preserves the scalar dtype and bits of Zarr fill values.</summary>
internal static class ZarrFillValueCodec
{
    internal static object? Parse(JsonElement value, string dtype, string name, bool v3)
    {
        if (value.ValueKind == JsonValueKind.Null && !v3) return null;
        var kind = Normalize(dtype);
        if (kind == "b1" && value.ValueKind is JsonValueKind.True or JsonValueKind.False)
            return value.GetBoolean();
        if (kind is "i1" or "i2" or "i4" or "i8" or "u1" or "u2" or "u4" or "u8")
        {
            if (value.ValueKind != JsonValueKind.Number ||
                v3 && value.GetRawText().IndexOfAny(['.', 'e', 'E']) >= 0 ||
                !value.TryGetDecimal(out var number) || decimal.Truncate(number) != number)
                throw Invalid(name, dtype);
            try
            {
                return kind switch
                {
                    "i1" when number is >= sbyte.MinValue and <= sbyte.MaxValue => (long)number,
                    "i2" when number is >= short.MinValue and <= short.MaxValue => (long)number,
                    "i4" when number is >= int.MinValue and <= int.MaxValue => (long)number,
                    "i8" when number is >= long.MinValue and <= long.MaxValue => (long)number,
                    "u1" when number is >= byte.MinValue and <= byte.MaxValue => (ulong)number,
                    "u2" when number is >= ushort.MinValue and <= ushort.MaxValue => (ulong)number,
                    "u4" when number is >= uint.MinValue and <= uint.MaxValue => (ulong)number,
                    "u8" when number is >= ulong.MinValue and <= ulong.MaxValue => (ulong)number,
                    _ => throw Invalid(name, dtype)
                };
            }
            catch (OverflowException) { throw Invalid(name, dtype); }
        }
        if (kind is "f4" or "f8")
        {
            if (value.ValueKind == JsonValueKind.Number)
            {
                // Zarr v3 permitted-fill-values requires IEEE round-half-to-even.
                // Parse directly at the target precision; numeric overflow may become infinity.
                if (kind == "f4" && value.TryGetSingle(out var single)) return single;
                if (kind == "f8" && value.TryGetDouble(out var number)) return number;
            }
            if (value.ValueKind == JsonValueKind.String)
            {
                var text = value.GetString();
                if (text == "NaN")
                {
                    // Zarr defines the positive quiet NaN, not the runtime's signed NaN constant.
                    if (kind == "f4") return BitConverter.Int32BitsToSingle(0x7fc00000);
                    return BitConverter.Int64BitsToDouble(0x7ff8000000000000);
                }
                if (text == "Infinity") return kind == "f4" ? (object)float.PositiveInfinity : double.PositiveInfinity;
                if (text == "-Infinity") return kind == "f4" ? (object)float.NegativeInfinity : double.NegativeInfinity;
                if (v3 && text is not null && text.StartsWith("0x", StringComparison.Ordinal))
                {
                    if (kind == "f4" && text.Length == 10 && uint.TryParse(text.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var bits32))
                        return BitConverter.Int32BitsToSingle(unchecked((int)bits32));
                    if (kind == "f8" && text.Length == 18 && ulong.TryParse(text.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var bits64))
                        return BitConverter.Int64BitsToDouble(unchecked((long)bits64));
                }
            }
        }
        throw Invalid(name, dtype);
    }

    internal static void Fill(byte[] buffer, string dtype, int elementSize, object? value, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (value is null) return; // v2 null fill denotes undefined content; zero is permitted.
        Span<byte> scalar = stackalloc byte[8];
        var kind = Normalize(dtype);
        try
        {
            switch (kind)
            {
                case "b1":
                    if (value is not bool boolean) throw Invalid("fill", dtype);
                    scalar[0] = boolean ? (byte)1 : (byte)0;
                    break;
                case "f4":
                    BinaryPrimitives.WriteSingleLittleEndian(scalar, value is float single ? single : Convert.ToSingle(value, CultureInfo.InvariantCulture));
                    break;
                case "f8":
                    BinaryPrimitives.WriteDoubleLittleEndian(scalar, Convert.ToDouble(value, CultureInfo.InvariantCulture));
                    break;
                default:
                    var number = Convert.ToDecimal(value, CultureInfo.InvariantCulture);
                    if (decimal.Truncate(number) != number) throw Invalid("fill", dtype);
                    switch (kind)
                    {
                        case "i1": scalar[0] = unchecked((byte)checked((sbyte)number)); break;
                        case "i2": BinaryPrimitives.WriteInt16LittleEndian(scalar, checked((short)number)); break;
                        case "i4": BinaryPrimitives.WriteInt32LittleEndian(scalar, checked((int)number)); break;
                        case "i8": BinaryPrimitives.WriteInt64LittleEndian(scalar, checked((long)number)); break;
                        case "u1": scalar[0] = checked((byte)number); break;
                        case "u2": BinaryPrimitives.WriteUInt16LittleEndian(scalar, checked((ushort)number)); break;
                        case "u4": BinaryPrimitives.WriteUInt32LittleEndian(scalar, checked((uint)number)); break;
                        case "u8": BinaryPrimitives.WriteUInt64LittleEndian(scalar, checked((ulong)number)); break;
                        default: throw Invalid("fill", dtype);
                    }
                    break;
            }
        }
        catch (Exception exception) when (exception is OverflowException or InvalidCastException or FormatException)
        {
            throw new InvalidDataException($"Zarr fill value cannot be represented by dtype '{dtype}'.", exception);
        }
        if (buffer.Length == 0) return;
        scalar[..elementSize].CopyTo(buffer);
        var initialized = elementSize;
        while (initialized < buffer.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var length = Math.Min(Math.Min(initialized, 64 * 1024), buffer.Length - initialized);
            buffer.AsSpan(0, length).CopyTo(buffer.AsSpan(initialized, length));
            initialized += length;
        }
    }

    private static string Normalize(string dtype) => dtype.Length > 1 && dtype[0] is '<' or '>' or '|' or '=' ? dtype[1..] : dtype;

    private static InvalidDataException Invalid(string name, string dtype) =>
        new($"Zarr array '{name}' has an invalid or unsupported fill_value for dtype '{dtype}'.");
}
