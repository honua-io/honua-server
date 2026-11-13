// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace Honua.Infrastructure.Rendering;

internal ref struct WkbReader(ReadOnlySpan<byte> data)
{
    private ReadOnlySpan<byte> _remaining = data;

    public bool IsLittleEndian { get; set; }

    public readonly int RemainingBytes => _remaining.Length;

    public byte ReadByte()
    {
        EnsureCanRead(1);
        var value = _remaining[0];
        _remaining = _remaining[1..];
        return value;
    }

    public int ReadInt32()
    {
        EnsureCanRead(sizeof(int));
        var value = IsLittleEndian
            ? BinaryPrimitives.ReadInt32LittleEndian(_remaining)
            : BinaryPrimitives.ReadInt32BigEndian(_remaining);
        _remaining = _remaining[sizeof(int)..];
        return value;
    }

    public double ReadDouble()
    {
        EnsureCanRead(sizeof(double));
        var value = IsLittleEndian
            ? BinaryPrimitives.ReadDoubleLittleEndian(_remaining)
            : BinaryPrimitives.ReadDoubleBigEndian(_remaining);
        _remaining = _remaining[sizeof(double)..];
        return value;
    }

    public (double X, double Y) ReadCoordinate()
    {
        const int coordinateBytes = 2 * sizeof(double);
        EnsureCanRead(coordinateBytes);
        if (!Vector128.IsHardwareAccelerated)
        {
            return (ReadDouble(), ReadDouble());
        }

        var bytes = MemoryMarshal.Read<Vector128<byte>>(_remaining);
        if (IsLittleEndian != BitConverter.IsLittleEndian)
        {
            bytes = Vector128.Shuffle(bytes,
                Vector128.Create((byte)7, 6, 5, 4, 3, 2, 1, 0, 15, 14, 13, 12, 11, 10, 9, 8));
        }

        _remaining = _remaining[coordinateBytes..];
        var values = bytes.AsDouble();
        return (values.GetElement(0), values.GetElement(1));
    }

    private readonly void EnsureCanRead(int byteCount)
    {
        if (_remaining.Length < byteCount)
        {
            throw new InvalidDataException("WKB payload ended unexpectedly.");
        }
    }
}
