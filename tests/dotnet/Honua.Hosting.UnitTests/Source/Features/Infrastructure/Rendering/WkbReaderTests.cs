// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Buffers.Binary;
using FluentAssertions;
using Honua.Infrastructure.Rendering;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Infrastructure.Rendering;

[Trait("Component", "MapServer")]
public class WkbReaderTests
{
    [UnitTest]
    public void ReadCoordinate_ByteOrdersAndUnalignedInput_PreservesEveryBit()
    {
        long[] bits = [0, long.MinValue, 1, -1, 0x7ff0000000000000, 0x7ff8000000000042,
            0x7fefffffffffffff, 0x0010000000000000, 0x3ff0000000000000];
        foreach (var littleEndian in new[] { false, true })
        {
            foreach (var x in bits)
            {
                foreach (var y in bits)
                {
                    var bytes = new byte[17];
                    WriteBits(bytes.AsSpan(1), x, littleEndian);
                    WriteBits(bytes.AsSpan(9), y, littleEndian);
                    var reader = new WkbReader(bytes.AsSpan(1)) { IsLittleEndian = littleEndian };

                    var point = reader.ReadCoordinate();

                    BitConverter.DoubleToInt64Bits(point.X).Should().Be(x);
                    BitConverter.DoubleToInt64Bits(point.Y).Should().Be(y);
                    reader.RemainingBytes.Should().Be(0);
                }
            }
        }
    }

    [UnitTest]
    public void TryConvertPoint_RepeatedReads_AllocatesNoManagedMemory()
    {
        var bytes = new byte[21];
        bytes[0] = 1;
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(1), 1);
        Func<double, double, SkiaSharp.SKPoint> transform = static (x, y) => new((float)x, (float)y);
        for (var i = 0; i < 100; i++)
        {
            WkbToSkiaConverter.TryConvertPoint(bytes, transform, out _);
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1000; i++)
        {
            WkbToSkiaConverter.TryConvertPoint(bytes, transform, out _);
        }

        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        allocated.Should().Be(0);
    }

    private static void WriteBits(Span<byte> bytes, long bits, bool littleEndian)
    {
        if (littleEndian)
        {
            BinaryPrimitives.WriteInt64LittleEndian(bytes, bits);
        }
        else
        {
            BinaryPrimitives.WriteInt64BigEndian(bytes, bits);
        }
    }
}
