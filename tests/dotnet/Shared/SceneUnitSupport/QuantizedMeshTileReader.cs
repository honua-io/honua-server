// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Buffers.Binary;
using System.IO.Compression;

namespace Honua.Scene.Terrain;

public static class QuantizedMeshTileReader
{
    private const int Index16VertexLimit = 65536;

    public static QuantizedMeshTile Read(ReadOnlySpan<byte> payload)
    {
        var raw = IsGzipped(payload) ? DecompressGzip(payload) : payload.ToArray();
        if (raw.Length < QuantizedMeshTileWriter.HeaderByteLength + 4)
        {
            throw new InvalidDataException("Quantized-mesh payload is truncated before the vertex count.");
        }

        var span = raw.AsSpan();
        var header = ReadHeader(span);
        var offset = QuantizedMeshTileWriter.HeaderByteLength;

        var vertexCount = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(offset, 4)));
        offset += 4;

        var u = ReadDeltaZigZagArray(span, ref offset, vertexCount);
        var v = ReadDeltaZigZagArray(span, ref offset, vertexCount);
        var height = ReadDeltaZigZagArray(span, ref offset, vertexCount);

        var use32Bit = vertexCount > Index16VertexLimit;
        AlignOffset(ref offset, use32Bit ? 4 : 2);

        var indices = ReadHighWaterMarkIndices(span, ref offset, use32Bit);
        var west = ReadIndexList(span, ref offset, use32Bit);
        var south = ReadIndexList(span, ref offset, use32Bit);
        var east = ReadIndexList(span, ref offset, use32Bit);
        var north = ReadIndexList(span, ref offset, use32Bit);
        var normals = ReadExtensions(span, ref offset, vertexCount);

        return new QuantizedMeshTile
        {
            Header = header,
            U = u,
            V = v,
            Height = height,
            Indices = indices,
            WestIndices = west,
            SouthIndices = south,
            EastIndices = east,
            NorthIndices = north,
            OctEncodedNormals = normals,
            Uses32BitIndices = use32Bit
        };
    }

    private static bool IsGzipped(ReadOnlySpan<byte> payload)
        => payload.Length >= 2 && payload[0] == 0x1F && payload[1] == 0x8B;

    private static byte[] DecompressGzip(ReadOnlySpan<byte> payload)
    {
        using var input = new MemoryStream(payload.ToArray());
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        gzip.CopyTo(output);
        return output.ToArray();
    }

    private static QuantizedMeshHeader ReadHeader(ReadOnlySpan<byte> span)
        => new(
            BinaryPrimitives.ReadDoubleLittleEndian(span.Slice(0, 8)),
            BinaryPrimitives.ReadDoubleLittleEndian(span.Slice(8, 8)),
            BinaryPrimitives.ReadDoubleLittleEndian(span.Slice(16, 8)),
            BinaryPrimitives.ReadSingleLittleEndian(span.Slice(24, 4)),
            BinaryPrimitives.ReadSingleLittleEndian(span.Slice(28, 4)),
            BinaryPrimitives.ReadDoubleLittleEndian(span.Slice(32, 8)),
            BinaryPrimitives.ReadDoubleLittleEndian(span.Slice(40, 8)),
            BinaryPrimitives.ReadDoubleLittleEndian(span.Slice(48, 8)),
            BinaryPrimitives.ReadDoubleLittleEndian(span.Slice(56, 8)),
            BinaryPrimitives.ReadDoubleLittleEndian(span.Slice(64, 8)),
            BinaryPrimitives.ReadDoubleLittleEndian(span.Slice(72, 8)),
            BinaryPrimitives.ReadDoubleLittleEndian(span.Slice(80, 8)));

    private static ushort[] ReadDeltaZigZagArray(ReadOnlySpan<byte> span, ref int offset, int count)
    {
        EnsureAvailable(span, offset, checked(count * 2));
        var values = new ushort[count];
        var current = 0;

        for (var i = 0; i < count; i++)
        {
            var encoded = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(offset, 2));
            offset += 2;
            var delta = (encoded >> 1) ^ -(encoded & 1);
            current += delta;
            values[i] = checked((ushort)current);
        }

        return values;
    }

    private static uint[] ReadHighWaterMarkIndices(ReadOnlySpan<byte> span, ref int offset, bool use32Bit)
    {
        EnsureAvailable(span, offset, 4);
        var triangleCount = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(offset, 4)));
        offset += 4;

        var indexCount = checked(triangleCount * 3);
        var stride = use32Bit ? 4 : 2;
        EnsureAvailable(span, offset, checked(indexCount * stride));

        var indices = new uint[indexCount];
        uint highest = 0;
        for (var i = 0; i < indexCount; i++)
        {
            var code = use32Bit
                ? BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(offset, 4))
                : BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(offset, 2));
            offset += stride;
            indices[i] = highest - code;
            if (code == 0)
            {
                highest++;
            }
        }

        return indices;
    }

    private static uint[] ReadIndexList(ReadOnlySpan<byte> span, ref int offset, bool use32Bit)
    {
        EnsureAvailable(span, offset, 4);
        var count = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(offset, 4)));
        offset += 4;

        var stride = use32Bit ? 4 : 2;
        EnsureAvailable(span, offset, checked(count * stride));
        var indices = new uint[count];
        for (var i = 0; i < count; i++)
        {
            indices[i] = use32Bit
                ? BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(offset, 4))
                : BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(offset, 2));
            offset += stride;
        }

        return indices;
    }

    private static byte[]? ReadExtensions(ReadOnlySpan<byte> span, ref int offset, int vertexCount)
    {
        byte[]? octNormals = null;
        while (offset < span.Length)
        {
            EnsureAvailable(span, offset, 5);
            var extensionId = span[offset];
            var extensionLength = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(offset + 1, 4)));
            offset += 5;
            EnsureAvailable(span, offset, extensionLength);

            if (extensionId == QuantizedMeshTileWriter.OctVertexNormalsExtensionId)
            {
                if (extensionLength != vertexCount * 2)
                {
                    throw new InvalidDataException($"OctEncodedVertexNormals length {extensionLength} does not match 2 * vertexCount ({vertexCount * 2}).");
                }

                octNormals = span.Slice(offset, extensionLength).ToArray();
            }

            offset += extensionLength;
        }

        return octNormals;
    }

    private static void AlignOffset(ref int offset, int alignment)
    {
        var remainder = offset % alignment;
        if (remainder != 0)
        {
            offset += alignment - remainder;
        }
    }

    private static void EnsureAvailable(ReadOnlySpan<byte> span, int offset, int length)
    {
        if (length < 0 || offset + length > span.Length)
        {
            throw new InvalidDataException($"Quantized-mesh payload truncated at offset {offset} (needed {length} bytes, total {span.Length}).");
        }
    }
}
