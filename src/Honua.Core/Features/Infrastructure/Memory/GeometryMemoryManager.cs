// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Runtime.CompilerServices;

namespace Honua.Core.Features.Infrastructure.Memory;

public static class GeometryMemoryManager
{
    public static CoordinateBufferRental RentCoordinateBuffer(int coordinateCount, int dimensions = 2)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(coordinateCount);
        ArgumentOutOfRangeException.ThrowIfLessThan(dimensions, 2);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(dimensions, 4);

        var totalSize = coordinateCount * dimensions;
        var buffer = MemoryPool.RentDoubleArray(totalSize);
        return new CoordinateBufferRental(buffer, coordinateCount, dimensions);
    }

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

    public double[] Buffer => _buffer;

    public int CoordinateCount => _coordinateCount;

    public int Dimensions => _dimensions;

    public int UsableLength => _coordinateCount * _dimensions;

    public Memory<double> Memory => new(_buffer, 0, UsableLength);

    public Span<double> Span => new(_buffer, 0, UsableLength);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Span<double> GetCoordinate(int coordinateIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(coordinateIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(coordinateIndex, _coordinateCount);

        var startIndex = coordinateIndex * _dimensions;
        return new Span<double>(_buffer, startIndex, _dimensions);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetX(int coordinateIndex, double value)
    {
        _buffer[coordinateIndex * _dimensions] = value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetY(int coordinateIndex, double value)
    {
        _buffer[coordinateIndex * _dimensions + 1] = value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetZ(int coordinateIndex, double value)
    {
        if (_dimensions < 3)
            throw new InvalidOperationException("Z coordinate not available for 2D coordinates");
        _buffer[coordinateIndex * _dimensions + 2] = value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetM(int coordinateIndex, double value)
    {
        if (_dimensions < 4)
            throw new InvalidOperationException("M coordinate not available for non-4D coordinates");
        _buffer[coordinateIndex * _dimensions + 3] = value;
    }

    public void Dispose()
    {
        MemoryPool.ReturnDoubleArray(_buffer, clearArray: false);
    }
}

public readonly struct ByteBufferRental : IDisposable
{
    private readonly byte[] _buffer;
    private readonly int _usableLength;

    internal ByteBufferRental(byte[] buffer, int usableLength)
    {
        _buffer = buffer;
        _usableLength = usableLength;
    }

    public byte[] Buffer => _buffer;

    public int UsableLength => _usableLength;

    public Memory<byte> Memory => new(_buffer, 0, _usableLength);

    public Span<byte> Span => new(_buffer, 0, _usableLength);

    // Clears the buffer before reuse so a later renter cannot read the previous caller's bytes.
    public void Dispose()
    {
        MemoryPool.ReturnByteArray(_buffer, clearArray: true);
    }
}
