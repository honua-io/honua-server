// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Buffers.Binary;
using FluentAssertions;

namespace Honua.Core.Tests.Raster.CogParser;

// Reads emitted classic little-endian TIFF tag bytes directly, independently of Honua's TIFF parser.
internal static class TiffTagReader
{
    public static Dictionary<ushort, byte[]> ReadFields(byte[] tiff)
    {
        var fields = new Dictionary<ushort, byte[]>();
        var ifd = (int)U32(tiff.AsSpan(4));
        var count = U16(tiff.AsSpan(ifd));
        for (var i = 0; i < count; i++)
        {
            var entry = tiff.AsSpan(ifd + 2 + i * 12, 12);
            var type = U16(entry[2..]);
            var size = type switch { 2 => 1, 3 => 2, 4 => 4, 12 => 8, _ => throw new InvalidDataException() };
            var length = checked((int)U32(entry[4..]) * size);
            var value = length <= 4 ? entry.Slice(8, length).ToArray()
                : tiff.AsSpan((int)U32(entry[8..]), length).ToArray();
            fields.Add(U16(entry), value);
        }
        U32(tiff.AsSpan(ifd + 2 + count * 12)).Should().Be(0);
        return fields;
    }

    public static ushort U16(ReadOnlySpan<byte> bytes) => BinaryPrimitives.ReadUInt16LittleEndian(bytes);

    public static uint U32(ReadOnlySpan<byte> bytes) => BinaryPrimitives.ReadUInt32LittleEndian(bytes);
}
