// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Buffers.Binary;

namespace Honua.Core.Tests.Features.Scene.PointCloud;

/// <summary>
/// Synthesizes minimal, valid uncompressed LAS 1.2 buffers in memory for the
/// point-cloud ingest tests (#1201). Avoids committing a binary fixture while
/// exercising the real header/point-record byte layout.
/// </summary>
internal static class LasFixtureBuilder
{
    /// <summary>A single point to encode into a LAS 1.2 point data record format 3 file.</summary>
    internal readonly record struct Point(
        double X,
        double Y,
        double Z,
        ushort Intensity,
        byte Classification,
        ushort Red,
        ushort Green,
        ushort Blue);

    private const int HeaderSize = 227;       // LAS 1.2 public header block.
    private const int RecordLengthFormat3 = 34; // base 28 + RGB 6.

    /// <summary>
    /// Builds a LAS 1.2, point data record format 3 (XYZ + GPS time + RGB)
    /// buffer from the supplied points with the given scale/offset.
    /// </summary>
    internal static byte[] BuildFormat3(
        IReadOnlyList<Point> points,
        double scale = 0.01,
        double offsetX = 0.0,
        double offsetY = 0.0,
        double offsetZ = 0.0)
    {
        var pointDataOffset = HeaderSize;
        var total = pointDataOffset + points.Count * RecordLengthFormat3;
        var buffer = new byte[total];
        var span = buffer.AsSpan();

        span[0] = (byte)'L';
        span[1] = (byte)'A';
        span[2] = (byte)'S';
        span[3] = (byte)'F';
        span[24] = 1; // version major
        span[25] = 2; // version minor

        BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(94, 2), HeaderSize);
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(96, 4), (uint)pointDataOffset);
        span[104] = 3; // point data record format 3
        BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(105, 2), RecordLengthFormat3);
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(107, 4), (uint)points.Count);

        BinaryPrimitives.WriteDoubleLittleEndian(span.Slice(131, 8), scale);
        BinaryPrimitives.WriteDoubleLittleEndian(span.Slice(139, 8), scale);
        BinaryPrimitives.WriteDoubleLittleEndian(span.Slice(147, 8), scale);
        BinaryPrimitives.WriteDoubleLittleEndian(span.Slice(155, 8), offsetX);
        BinaryPrimitives.WriteDoubleLittleEndian(span.Slice(163, 8), offsetY);
        BinaryPrimitives.WriteDoubleLittleEndian(span.Slice(171, 8), offsetZ);

        double minX = double.PositiveInfinity, minY = double.PositiveInfinity, minZ = double.PositiveInfinity;
        double maxX = double.NegativeInfinity, maxY = double.NegativeInfinity, maxZ = double.NegativeInfinity;

        var offset = pointDataOffset;
        foreach (var p in points)
        {
            var xi = (int)Math.Round((p.X - offsetX) / scale);
            var yi = (int)Math.Round((p.Y - offsetY) / scale);
            var zi = (int)Math.Round((p.Z - offsetZ) / scale);

            BinaryPrimitives.WriteInt32LittleEndian(span.Slice(offset, 4), xi);
            BinaryPrimitives.WriteInt32LittleEndian(span.Slice(offset + 4, 4), yi);
            BinaryPrimitives.WriteInt32LittleEndian(span.Slice(offset + 8, 4), zi);
            BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(offset + 12, 2), p.Intensity);
            // bytes 14 (return/flags), 15 (classification), 16 (scan angle),
            // 17 (user data), 18-19 (point source id), 20-27 (GPS time).
            span[offset + 15] = p.Classification;
            // RGB starts at base record length (28) for format 3.
            BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(offset + 28, 2), p.Red);
            BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(offset + 30, 2), p.Green);
            BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(offset + 32, 2), p.Blue);

            if (p.X < minX) minX = p.X;
            if (p.Y < minY) minY = p.Y;
            if (p.Z < minZ) minZ = p.Z;
            if (p.X > maxX) maxX = p.X;
            if (p.Y > maxY) maxY = p.Y;
            if (p.Z > maxZ) maxZ = p.Z;

            offset += RecordLengthFormat3;
        }

        BinaryPrimitives.WriteDoubleLittleEndian(span.Slice(179, 8), maxX);
        BinaryPrimitives.WriteDoubleLittleEndian(span.Slice(187, 8), minX);
        BinaryPrimitives.WriteDoubleLittleEndian(span.Slice(195, 8), maxY);
        BinaryPrimitives.WriteDoubleLittleEndian(span.Slice(203, 8), minY);
        BinaryPrimitives.WriteDoubleLittleEndian(span.Slice(211, 8), maxZ);
        BinaryPrimitives.WriteDoubleLittleEndian(span.Slice(219, 8), minZ);

        return buffer;
    }

    /// <summary>Marks a format-3 buffer as LAZ-compressed (sets bit 7 of the format byte).</summary>
    internal static byte[] MarkCompressed(byte[] lasBuffer)
    {
        var copy = (byte[])lasBuffer.Clone();
        copy[104] = (byte)(copy[104] | 0x80);
        return copy;
    }
}
