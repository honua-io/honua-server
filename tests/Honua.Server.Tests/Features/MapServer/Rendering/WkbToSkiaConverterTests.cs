// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Server.Features.MapServer.Rendering;
using Honua.TestKit.Attributes;
using SkiaSharp;

namespace Honua.Server.Tests.Features.MapServer.Rendering;

/// <summary>
/// Tests for WKB geometry to Skia path conversion.
/// </summary>
[Trait("Component", "MapServer")]
public class WkbToSkiaConverterTests
{
    private static readonly Func<double, double, SKPoint> _identityTransform =
        (x, y) => new SKPoint((float)x, (float)y);

    [UnitTest]
    public void Convert_Point_ReturnsPointResult()
    {
        var wkb = CreateWkbPoint(10.0, 20.0);

        var result = WkbToSkiaConverter.Convert(wkb, _identityTransform);

        result.IsPoint.Should().BeTrue();
        result.Points.Should().NotBeNull();
        result.Points.Should().HaveCount(1);
        result.Points![0].X.Should().BeApproximately(10f, 0.001f);
        result.Points![0].Y.Should().BeApproximately(20f, 0.001f);
        result.Path.Should().BeNull();
    }

    [UnitTest]
    public void TryConvertPoint_Point_ReturnsPoint()
    {
        var wkb = CreateWkbPoint(10.0, 20.0);

        var success = WkbToSkiaConverter.TryConvertPoint(wkb, _identityTransform, out var point);

        success.Should().BeTrue();
        point.X.Should().BeApproximately(10f, 0.001f);
        point.Y.Should().BeApproximately(20f, 0.001f);
    }

    [UnitTest]
    public void TryConvertPoint_NonPoint_ReturnsFalse()
    {
        var wkb = CreateWkbLineString([(0, 0), (10, 10)]);

        var success = WkbToSkiaConverter.TryConvertPoint(wkb, _identityTransform, out _);

        success.Should().BeFalse();
    }

    [UnitTest]
    public void Convert_LineString_ReturnsPathResult()
    {
        var wkb = CreateWkbLineString([(0, 0), (10, 10), (20, 0)]);

        var result = WkbToSkiaConverter.Convert(wkb, _identityTransform);

        result.IsPoint.Should().BeFalse();
        result.IsPolygon.Should().BeFalse();
        result.Path.Should().NotBeNull();
        result.Path!.PointCount.Should().BeGreaterThan(0);
        result.Path.Dispose();
    }

    [UnitTest]
    public void Convert_Polygon_ReturnsPolygonResult()
    {
        var wkb = CreateWkbPolygon([(0, 0), (10, 0), (10, 10), (0, 10), (0, 0)]);

        var result = WkbToSkiaConverter.Convert(wkb, _identityTransform);

        result.IsPolygon.Should().BeTrue();
        result.Path.Should().NotBeNull();
        result.Path!.PointCount.Should().BeGreaterThan(0);
        result.Path.Dispose();
    }

    [UnitTest]
    public void Convert_MultiPoint_ReturnsMultiplePoints()
    {
        var wkb = CreateWkbMultiPoint([(1, 2), (3, 4), (5, 6)]);

        var result = WkbToSkiaConverter.Convert(wkb, _identityTransform);

        result.IsPoint.Should().BeTrue();
        result.Points.Should().NotBeNull();
        result.Points.Should().HaveCount(3);
    }

    [UnitTest]
    public void Convert_NullGeometry_ReturnsEmptyResult()
    {
        var result = WkbToSkiaConverter.Convert(null!, _identityTransform);

        result.Path.Should().BeNull();
        result.Points.Should().BeNull();
    }

    [UnitTest]
    public void Convert_EmptyGeometry_ReturnsEmptyResult()
    {
        var result = WkbToSkiaConverter.Convert([], _identityTransform);

        result.Path.Should().BeNull();
        result.Points.Should().BeNull();
    }

    [UnitTest]
    public void Convert_TooShortGeometry_ReturnsEmptyResult()
    {
        var result = WkbToSkiaConverter.Convert([1, 0, 0], _identityTransform);

        result.Path.Should().BeNull();
        result.Points.Should().BeNull();
    }

    [UnitTest]
    public void Convert_WithTransform_AppliesTransformation()
    {
        var wkb = CreateWkbPoint(100.0, 200.0);
        // Scale by 0.5
        Func<double, double, SKPoint> scaledTransform =
            (x, y) => new SKPoint((float)(x * 0.5), (float)(y * 0.5));

        var result = WkbToSkiaConverter.Convert(wkb, scaledTransform);

        result.Points.Should().NotBeNull();
        result.Points![0].X.Should().BeApproximately(50f, 0.001f);
        result.Points![0].Y.Should().BeApproximately(100f, 0.001f);
    }

    [UnitTest]
    public void Convert_BigEndianPoint_ParsesCorrectly()
    {
        var wkb = CreateWkbPointBigEndian(10.0, 20.0);

        var result = WkbToSkiaConverter.Convert(wkb, _identityTransform);

        result.IsPoint.Should().BeTrue();
        result.Points.Should().NotBeNull();
        result.Points![0].X.Should().BeApproximately(10f, 0.001f);
        result.Points![0].Y.Should().BeApproximately(20f, 0.001f);
    }

    // --- WKB Creation Helpers ---

    private static byte[] CreateWkbPoint(double x, double y)
    {
        var wkb = new byte[21];
        wkb[0] = 1; // little-endian
        BitConverter.TryWriteBytes(wkb.AsSpan(1), 1); // Point type
        BitConverter.TryWriteBytes(wkb.AsSpan(5), x);
        BitConverter.TryWriteBytes(wkb.AsSpan(13), y);
        return wkb;
    }

    private static byte[] CreateWkbPointBigEndian(double x, double y)
    {
        var wkb = new byte[21];
        wkb[0] = 0; // big-endian
        WriteInt32BigEndian(wkb, 1, 1); // Point type
        WriteDoubleBigEndian(wkb, 5, x);
        WriteDoubleBigEndian(wkb, 13, y);
        return wkb;
    }

    private static byte[] CreateWkbLineString((double X, double Y)[] points)
    {
        var wkb = new byte[9 + points.Length * 16];
        wkb[0] = 1; // little-endian
        BitConverter.TryWriteBytes(wkb.AsSpan(1), 2); // LineString type
        BitConverter.TryWriteBytes(wkb.AsSpan(5), points.Length);
        var offset = 9;
        foreach (var (x, y) in points)
        {
            BitConverter.TryWriteBytes(wkb.AsSpan(offset), x);
            offset += 8;
            BitConverter.TryWriteBytes(wkb.AsSpan(offset), y);
            offset += 8;
        }

        return wkb;
    }

    private static byte[] CreateWkbPolygon((double X, double Y)[] ring)
    {
        var wkb = new byte[13 + ring.Length * 16];
        wkb[0] = 1; // little-endian
        BitConverter.TryWriteBytes(wkb.AsSpan(1), 3); // Polygon type
        BitConverter.TryWriteBytes(wkb.AsSpan(5), 1); // 1 ring
        BitConverter.TryWriteBytes(wkb.AsSpan(9), ring.Length);
        var offset = 13;
        foreach (var (x, y) in ring)
        {
            BitConverter.TryWriteBytes(wkb.AsSpan(offset), x);
            offset += 8;
            BitConverter.TryWriteBytes(wkb.AsSpan(offset), y);
            offset += 8;
        }

        return wkb;
    }

    private static byte[] CreateWkbMultiPoint((double X, double Y)[] points)
    {
        // MultiPoint = 1 (byte order) + 4 (type=4) + 4 (num points) + N * (1+4+16)
        var wkb = new byte[9 + points.Length * 21];
        wkb[0] = 1; // little-endian
        BitConverter.TryWriteBytes(wkb.AsSpan(1), 4); // MultiPoint type
        BitConverter.TryWriteBytes(wkb.AsSpan(5), points.Length);
        var offset = 9;
        foreach (var (x, y) in points)
        {
            wkb[offset] = 1; // sub-geometry: little-endian
            offset++;
            BitConverter.TryWriteBytes(wkb.AsSpan(offset), 1); // Point type
            offset += 4;
            BitConverter.TryWriteBytes(wkb.AsSpan(offset), x);
            offset += 8;
            BitConverter.TryWriteBytes(wkb.AsSpan(offset), y);
            offset += 8;
        }

        return wkb;
    }

    private static void WriteInt32BigEndian(byte[] buffer, int offset, int value)
    {
        buffer[offset] = (byte)(value >> 24);
        buffer[offset + 1] = (byte)(value >> 16);
        buffer[offset + 2] = (byte)(value >> 8);
        buffer[offset + 3] = (byte)value;
    }

    private static void WriteDoubleBigEndian(byte[] buffer, int offset, double value)
    {
        var bytes = BitConverter.GetBytes(value);
        Array.Reverse(bytes);
        Buffer.BlockCopy(bytes, 0, buffer, offset, 8);
    }
}
