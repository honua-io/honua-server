// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Runtime.CompilerServices;

namespace Honua.Core.Features.Infrastructure.Memory;

/// <summary>
/// Optimized memory management for geometry coordinate operations
/// </summary>
public static class GeometryMemoryManager
{
    /// <summary>
    /// Creates a pooled coordinate buffer for geometry operations
    /// </summary>
    /// <param name="coordinateCount">Number of coordinates needed</param>
    /// <param name="dimensions">Number of dimensions per coordinate (2, 3, or 4)</param>
    /// <returns>Rental wrapper that manages the pooled memory lifecycle</returns>
    public static CoordinateBufferRental RentCoordinateBuffer(int coordinateCount, int dimensions = 2)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(coordinateCount);
        ArgumentOutOfRangeException.ThrowIfLessThan(dimensions, 2);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(dimensions, 4);

        var totalSize = coordinateCount * dimensions;
        var buffer = MemoryPool.RentDoubleArray(totalSize);
        return new CoordinateBufferRental(buffer, coordinateCount, dimensions);
    }

    /// <summary>
    /// Creates a pooled memory span for WKB processing operations
    /// </summary>
    /// <param name="wkbLength">Expected WKB byte length</param>
    /// <param name="bufferSize">Optional buffer size override (must be >= wkbLength)</param>
    /// <returns>Rental wrapper that manages the pooled memory lifecycle</returns>
    public static ByteBufferRental RentWkbBuffer(int wkbLength, int? bufferSize = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(wkbLength);

        var actualSize = bufferSize ?? wkbLength;
        if (actualSize < wkbLength)
        {
            throw new ArgumentException($"Buffer size {actualSize} must be >= WKB length {wkbLength}");
        }

        var buffer = MemoryPool.RentByteArray(actualSize);
        return new ByteBufferRental(buffer, wkbLength);
    }
}

/// <summary>
/// RAII wrapper for pooled coordinate buffers that ensures proper cleanup
/// </summary>
public readonly struct CoordinateBufferRental : IDisposable
{
    private readonly double[] _buffer;
    private readonly int _coordinateCount;
    private readonly int _dimensions;

    internal CoordinateBufferRental(double[] buffer, int coordinateCount, int dimensions)
    {
        _buffer = buffer;
        _coordinateCount = coordinateCount;
        _dimensions = dimensions;
    }

    /// <summary>
    /// Gets the raw pooled buffer (may be larger than requested)
    /// </summary>
    public double[] Buffer => _buffer;

    /// <summary>
    /// Gets the number of coordinates this buffer can hold
    /// </summary>
    public int CoordinateCount => _coordinateCount;

    /// <summary>
    /// Gets the number of dimensions per coordinate
    /// </summary>
    public int Dimensions => _dimensions;

    /// <summary>
    /// Gets the actual usable length (coordinateCount * dimensions)
    /// </summary>
    public int UsableLength => _coordinateCount * _dimensions;

    /// <summary>
    /// Gets a memory span covering the usable portion of the buffer
    /// </summary>
    public Memory<double> Memory => new(_buffer, 0, UsableLength);

    /// <summary>
    /// Gets a span covering the usable portion of the buffer
    /// </summary>
    public Span<double> Span => new(_buffer, 0, UsableLength);

    /// <summary>
    /// Gets coordinates for a specific point
    /// </summary>
    /// <param name="coordinateIndex">Zero-based coordinate index</param>
    /// <returns>Span covering the coordinate dimensions</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Span<double> GetCoordinate(int coordinateIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(coordinateIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(coordinateIndex, _coordinateCount);

        var startIndex = coordinateIndex * _dimensions;
        return new Span<double>(_buffer, startIndex, _dimensions);
    }

    /// <summary>
    /// Sets the X coordinate for a specific point
    /// </summary>
    /// <param name="coordinateIndex">Zero-based coordinate index</param>
    /// <param name="value">X coordinate value</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetX(int coordinateIndex, double value)
    {
        _buffer[coordinateIndex * _dimensions] = value;
    }

    /// <summary>
    /// Sets the Y coordinate for a specific point
    /// </summary>
    /// <param name="coordinateIndex">Zero-based coordinate index</param>
    /// <param name="value">Y coordinate value</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetY(int coordinateIndex, double value)
    {
        _buffer[coordinateIndex * _dimensions + 1] = value;
    }

    /// <summary>
    /// Sets the Z coordinate for a specific point (only valid for 3D/4D)
    /// </summary>
    /// <param name="coordinateIndex">Zero-based coordinate index</param>
    /// <param name="value">Z coordinate value</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetZ(int coordinateIndex, double value)
    {
        if (_dimensions < 3)
            throw new InvalidOperationException("Z coordinate not available for 2D coordinates");
        _buffer[coordinateIndex * _dimensions + 2] = value;
    }

    /// <summary>
    /// Sets the M coordinate for a specific point (only valid for 4D)
    /// </summary>
    /// <param name="coordinateIndex">Zero-based coordinate index</param>
    /// <param name="value">M coordinate value</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetM(int coordinateIndex, double value)
    {
        if (_dimensions < 4)
            throw new InvalidOperationException("M coordinate not available for non-4D coordinates");
        _buffer[coordinateIndex * _dimensions + 3] = value;
    }

    /// <summary>
    /// Returns the buffer to the pool for reuse
    /// </summary>
    public void Dispose()
    {
        MemoryPool.ReturnDoubleArray(_buffer, clearArray: false);
    }
}

/// <summary>
/// RAII wrapper for pooled byte buffers that ensures proper cleanup
/// </summary>
public readonly struct ByteBufferRental : IDisposable
{
    private readonly byte[] _buffer;
    private readonly int _usableLength;

    internal ByteBufferRental(byte[] buffer, int usableLength)
    {
        _buffer = buffer;
        _usableLength = usableLength;
    }

    /// <summary>
    /// Gets the raw pooled buffer (may be larger than requested)
    /// </summary>
    public byte[] Buffer => _buffer;

    /// <summary>
    /// Gets the usable length of the buffer
    /// </summary>
    public int UsableLength => _usableLength;

    /// <summary>
    /// Gets a memory span covering the usable portion of the buffer
    /// </summary>
    public Memory<byte> Memory => new(_buffer, 0, _usableLength);

    /// <summary>
    /// Gets a span covering the usable portion of the buffer
    /// </summary>
    public Span<byte> Span => new(_buffer, 0, _usableLength);

    /// <summary>
    /// Returns the buffer to the pool for reuse (clears for security)
    /// </summary>
    public void Dispose()
    {
        MemoryPool.ReturnByteArray(_buffer, clearArray: true);
    }
}
