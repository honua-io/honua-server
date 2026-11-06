// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Buffers;

namespace Honua.Core.Features.Infrastructure.Memory;

public static class MemoryPool
{
    public static readonly ArrayPool<byte> ByteArray = ArrayPool<byte>.Shared;

    public static readonly ArrayPool<double> DoubleArray = ArrayPool<double>.Shared;

    public static readonly ArrayPool<char> CharArray = ArrayPool<char>.Shared;

    public static byte[] RentByteArray(int minimumLength)
    {
        return ByteArray.Rent(minimumLength);
    }

    // Clears by default so a later renter cannot read the previous caller's bytes.
    public static void ReturnByteArray(byte[] array, bool clearArray = true)
    {
        ByteArray.Return(array, clearArray);
    }

    public static double[] RentDoubleArray(int minimumLength)
    {
        return DoubleArray.Rent(minimumLength);
    }

    public static void ReturnDoubleArray(double[] array, bool clearArray = false)
    {
        DoubleArray.Return(array, clearArray);
    }

    public static char[] RentCharArray(int minimumLength)
    {
        return CharArray.Rent(minimumLength);
    }

    // Clears by default so a later renter cannot read the previous caller's text.
    public static void ReturnCharArray(char[] array, bool clearArray = true)
    {
        CharArray.Return(array, clearArray);
    }
}
