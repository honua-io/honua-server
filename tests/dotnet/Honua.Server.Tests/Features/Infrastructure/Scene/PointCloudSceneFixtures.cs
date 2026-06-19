// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Buffers.Binary;

namespace Honua.Server.Tests.Features.Infrastructure.Scene;

/// <summary>
/// Synthesizes minimal, valid uncompressed LAS 1.2 buffers in memory for the
/// point-cloud ingest tests (#1201). Avoids committing a binary fixture while
/// exercising the real header/point-record byte layout the reader parses. The
/// emitted coordinates are treated as geographic (lon/lat degrees) by the
/// executor's geographic-pass-through transform.
/// </summary>
internal static class PointCloudSceneFixtures
{
    private const int HeaderSize = 227;          // LAS 1.2 public header block.
    private const int RecordLengthFormat3 = 34;  // base 28 + RGB 6.

    /// <summary>
    /// Builds a LAS 1.2, point data record format 3 (XYZ + GPS time + RGB)
    /// buffer holding a small geographic grid of coloured points around the
    /// supplied origin. The grid is dense enough to subdivide into multiple
    /// quadtree tiles under a small per-tile cap.
    /// </summary>
    public static byte[] ColoredGridGeographic(
        int columns = 16,
        int rows = 16,
        double originLon = -122.5,
        double originLat = 37.5,
        double step = 0.0005)
    {
        var points = new List<(double X, double Y, double Z, ushort Intensity, byte Classification, ushort R, ushort G, ushort B)>(columns * rows);
        for (var r = 0; r < rows; r++)
        {
            for (var c = 0; c < columns; c++)
            {
                var lon = originLon + c * step;
                var lat = originLat + r * step;
                var z = 10.0 + ((c + r) % 5);
                // 8-bit-in-16-bit colour (the common producer quirk) so the
                // ingest preserves a recognisable RGB ramp.
                var red = (ushort)((c * 255) / Math.Max(1, columns - 1));
                var green = (ushort)((r * 255) / Math.Max(1, rows - 1));
                var blue = (ushort)128;
                var classification = (byte)(2 + ((c + r) % 3)); // ground / low-veg / building.
                var intensity = (ushort)(c * 17 + r * 31);
                points.Add((lon, lat, z, intensity, classification, red, green, blue));
            }
        }

        return BuildFormat3(points);
    }

    /// <summary>Builds a single-point geographic LAS buffer (smallest valid cloud).</summary>
    public static byte[] SinglePointGeographic(double lon = -122.5, double lat = 37.5, double height = 12.0)
        => BuildFormat3([(lon, lat, height, Intensity: (ushort)100, Classification: (byte)2, R: (ushort)10, G: (ushort)20, B: (ushort)30)]);

    /// <summary>Marks a format-3 buffer as LAZ-compressed (sets bit 7 of the format byte).</summary>
    public static byte[] MarkCompressed(byte[] lasBuffer)
    {
        var copy = (byte[])lasBuffer.Clone();
        copy[104] = (byte)(copy[104] | 0x80);
        return copy;
    }

    private static byte[] BuildFormat3(
        List<(double X, double Y, double Z, ushort Intensity, byte Classification, ushort R, ushort G, ushort B)> points)
    {
        // A fine scale keeps the descaled geographic coordinates within int range
        // when divided through, matching the reader's scale/offset model.
        const double scale = 1e-7;
        const double offsetX = 0.0;
        const double offsetY = 0.0;
        const double offsetZ = 0.0;

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
            span[offset + 15] = p.Classification;
            BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(offset + 28, 2), p.R);
            BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(offset + 30, 2), p.G);
            BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(offset + 32, 2), p.B);

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
}
