// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Runtime.CompilerServices;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.Migration.Services;
using Honua.Core.Features.FileImport.Abstractions;
using Honua.Core.Features.FileImport.Domain;
using Honua.Core.Features.FileImport.Services;

namespace Honua.Core.Features.FileImport.Services.FileGdb;

internal static class GdbVarInt
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong ReadVarUInt(BinaryReader reader)
    {
        ulong result = 0;
        var shift = 0;
        while (true)
        {
            var b = reader.ReadByte();
            result |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
            {
                break;
            }

            shift += 7;
            if (shift > 63)
            {
                throw new InvalidDataException("VarUInt exceeds 64-bit range.");
            }
        }

        return result;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long ReadVarInt(BinaryReader reader)
    {
        var unsigned = ReadVarUInt(reader);
        return ZigzagDecode(unsigned);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong ReadVarUInt(ReadOnlySpan<byte> data, ref int offset)
    {
        ulong result = 0;
        var shift = 0;
        while (offset < data.Length)
        {
            var b = data[offset++];
            result |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
            {
                return result;
            }

            shift += 7;
            if (shift > 63)
            {
                throw new InvalidDataException("VarUInt exceeds 64-bit range.");
            }
        }

        throw new InvalidDataException("Unexpected end of data reading VarUInt.");
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long ReadVarInt(ReadOnlySpan<byte> data, ref int offset)
    {
        var unsigned = ReadVarUInt(data, ref offset);
        return ZigzagDecode(unsigned);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long ZigzagDecode(ulong value)
    {
        return (long)(value >> 1) ^ -((long)(value & 1));
    }
}
