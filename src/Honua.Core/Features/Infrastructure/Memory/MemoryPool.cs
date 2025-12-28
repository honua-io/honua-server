// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Buffers;

namespace Honua.Core.Features.Infrastructure.Memory;

/// <summary>
/// Provides pooled memory management for high-frequency allocations
/// </summary>
public static class MemoryPool
{
    /// <summary>
    /// Shared ArrayPool for byte arrays used in geometry processing and serialization
    /// </summary>
    public static readonly ArrayPool<byte> ByteArray = ArrayPool<byte>.Shared;

    /// <summary>
    /// Shared ArrayPool for double arrays used in coordinate processing
    /// </summary>
    public static readonly ArrayPool<double> DoubleArray = ArrayPool<double>.Shared;

    /// <summary>
    /// Shared ArrayPool for character arrays used in string processing
    /// </summary>
    public static readonly ArrayPool<char> CharArray = ArrayPool<char>.Shared;

    /// <summary>
    /// Rents a byte array from the pool with at least the specified minimum length
    /// </summary>
    /// <param name="minimumLength">Minimum required length</param>
    /// <returns>Pooled byte array that must be returned to the pool</returns>
    public static byte[] RentByteArray(int minimumLength)
    {
        return ByteArray.Rent(minimumLength);
    }

    /// <summary>
    /// Returns a byte array to the pool for reuse
    /// </summary>
    /// <param name="array">Array to return (can be larger than originally requested)</param>
    /// <param name="clearArray">Whether to clear the array contents (default: true for security)</param>
    public static void ReturnByteArray(byte[] array, bool clearArray = true)
    {
        ByteArray.Return(array, clearArray);
    }

    /// <summary>
    /// Rents a double array from the pool with at least the specified minimum length
    /// </summary>
    /// <param name="minimumLength">Minimum required length</param>
    /// <returns>Pooled double array that must be returned to the pool</returns>
    public static double[] RentDoubleArray(int minimumLength)
    {
        return DoubleArray.Rent(minimumLength);
    }

    /// <summary>
    /// Returns a double array to the pool for reuse
    /// </summary>
    /// <param name="array">Array to return (can be larger than originally requested)</param>
    /// <param name="clearArray">Whether to clear the array contents (default: false for performance)</param>
    public static void ReturnDoubleArray(double[] array, bool clearArray = false)
    {
        DoubleArray.Return(array, clearArray);
    }

    /// <summary>
    /// Rents a character array from the pool with at least the specified minimum length
    /// </summary>
    /// <param name="minimumLength">Minimum required length</param>
    /// <returns>Pooled character array that must be returned to the pool</returns>
    public static char[] RentCharArray(int minimumLength)
    {
        return CharArray.Rent(minimumLength);
    }

    /// <summary>
    /// Returns a character array to the pool for reuse
    /// </summary>
    /// <param name="array">Array to return (can be larger than originally requested)</param>
    /// <param name="clearArray">Whether to clear the array contents (default: true for security)</param>
    public static void ReturnCharArray(char[] array, bool clearArray = true)
    {
        CharArray.Return(array, clearArray);
    }
}
