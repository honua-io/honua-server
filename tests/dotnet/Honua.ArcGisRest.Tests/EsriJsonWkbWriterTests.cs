// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Buffers.Binary;
using System.Text.Json;
using Honua.ArcGisRest.Features.FeatureStore.Services;
using Honua.Core.Features.Metadata.Domain.V2;

namespace Honua.ArcGisRest.Tests;

public class EsriJsonWkbWriterTests
{
    [Fact]
    public void Write_NullElement_ReturnsNull()
    {
        Assert.Null(EsriJsonWkbWriter.Write(geometry: null, MetadataV2GeometryType.Point));
    }

    [Fact]
    public void Write_Point_EmitsLittleEndianPointWkb()
    {
        var element = Parse("""{"x": -122.4, "y": 47.6}""");

        var wkb = EsriJsonWkbWriter.Write(element, MetadataV2GeometryType.Point);

        Assert.NotNull(wkb);
        Assert.Equal(0x01, wkb![0]); // little endian
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(wkb.AsSpan(1, 4)));
        Assert.Equal(-122.4, BinaryPrimitives.ReadDoubleLittleEndian(wkb.AsSpan(5, 8)));
        Assert.Equal(47.6, BinaryPrimitives.ReadDoubleLittleEndian(wkb.AsSpan(13, 8)));
    }

    [Fact]
    public void Write_MultiPoint_EmitsMultiPointWkbWithChildCount()
    {
        var element = Parse("""{"points": [[1, 2], [3, 4], [5, 6]]}""");

        var wkb = EsriJsonWkbWriter.Write(element, MetadataV2GeometryType.MultiPoint);

        Assert.NotNull(wkb);
        Assert.Equal(0x01, wkb![0]);
        Assert.Equal(4u, BinaryPrimitives.ReadUInt32LittleEndian(wkb.AsSpan(1, 4)));
        Assert.Equal(3u, BinaryPrimitives.ReadUInt32LittleEndian(wkb.AsSpan(5, 4)));
    }

    [Fact]
    public void Write_PolylineSinglePath_EmitsLineStringWkb()
    {
        var element = Parse("""{"paths": [[[0, 0], [1, 1], [2, 0]]]}""");

        var wkb = EsriJsonWkbWriter.Write(element, MetadataV2GeometryType.LineString);

        Assert.NotNull(wkb);
        Assert.Equal(0x01, wkb![0]);
        Assert.Equal(2u, BinaryPrimitives.ReadUInt32LittleEndian(wkb.AsSpan(1, 4))); // LineString
        Assert.Equal(3u, BinaryPrimitives.ReadUInt32LittleEndian(wkb.AsSpan(5, 4))); // point count
    }

    [Fact]
    public void Write_PolylineMultiplePaths_EmitsMultiLineStringWkb()
    {
        var element = Parse("""{"paths": [[[0,0],[1,1]],[[2,2],[3,3]]]}""");

        var wkb = EsriJsonWkbWriter.Write(element, MetadataV2GeometryType.MultiLineString);

        Assert.NotNull(wkb);
        Assert.Equal(0x01, wkb![0]);
        Assert.Equal(5u, BinaryPrimitives.ReadUInt32LittleEndian(wkb.AsSpan(1, 4))); // MultiLineString
        Assert.Equal(2u, BinaryPrimitives.ReadUInt32LittleEndian(wkb.AsSpan(5, 4))); // line count
    }

    [Fact]
    public void Write_Polygon_EmitsPolygonWkb()
    {
        var element = Parse("""{"rings": [[[0,0],[1,0],[1,1],[0,1],[0,0]]]}""");

        var wkb = EsriJsonWkbWriter.Write(element, MetadataV2GeometryType.Polygon);

        Assert.NotNull(wkb);
        Assert.Equal(0x01, wkb![0]);
        Assert.Equal(3u, BinaryPrimitives.ReadUInt32LittleEndian(wkb.AsSpan(1, 4))); // Polygon
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(wkb.AsSpan(5, 4))); // ring count
        Assert.Equal(5u, BinaryPrimitives.ReadUInt32LittleEndian(wkb.AsSpan(9, 4))); // points in first ring
    }

    [Fact]
    public void Write_PolygonTwoShellRings_EmitsMultiPolygonWkb()
    {
        // Regression for #2003: an Esri rings[] payload carrying two clockwise
        // (negative signed-area) exterior shells must classify as two distinct
        // polygons and serialize as a WKB MultiPolygon (type 6), not collapse
        // into a single Polygon (type 3) with the second shell treated as a hole.
        // The two rings are spatially disjoint and both wound clockwise so the
        // signed-area orientation check assigns each to its own shell.
        var element = Parse(
            """{"rings": [[[0,0],[0,1],[1,1],[1,0],[0,0]],[[10,10],[10,11],[11,11],[11,10],[10,10]]]}""");

        var wkb = EsriJsonWkbWriter.Write(element, MetadataV2GeometryType.Polygon);

        Assert.NotNull(wkb);
        Assert.Equal(0x01, wkb![0]); // little endian
        Assert.Equal(6u, BinaryPrimitives.ReadUInt32LittleEndian(wkb.AsSpan(1, 4))); // MultiPolygon
        Assert.Equal(2u, BinaryPrimitives.ReadUInt32LittleEndian(wkb.AsSpan(5, 4))); // two member polygons

        // First member polygon begins at offset 9: byte order + type (3) + ring count (1).
        Assert.Equal(0x01, wkb[9]);
        Assert.Equal(3u, BinaryPrimitives.ReadUInt32LittleEndian(wkb.AsSpan(10, 4))); // Polygon
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(wkb.AsSpan(14, 4))); // single shell ring
    }

    [Fact]
    public void CheckedBufferSize_WithCountThatOverflowsInt32_Throws()
    {
        // Regression for #2003: a hostile/oversized coordinate count must be
        // multiplied out in 64-bit arithmetic and rejected against the 256 MiB
        // WKB cap, rather than overflowing Int32 into a wrong-sized (or negative)
        // allocation. int.MaxValue points at 16 bytes each is ~32 GiB, far above
        // the cap, so the guard throws before any buffer is allocated.
        Assert.Throws<InvalidOperationException>(
            () => EsriJsonWkbWriter.CheckedBufferSize(headerBytes: 4, count: int.MaxValue, elementSize: 16));
    }

    [Fact]
    public void CheckedBufferSize_WithReasonableCount_ReturnsComputedSize()
    {
        // A modest count stays under the cap and returns header + count * stride.
        var size = EsriJsonWkbWriter.CheckedBufferSize(headerBytes: 4, count: 10, elementSize: 16);

        Assert.Equal(4 + (10 * 16), size);
    }

    [Fact]
    public void CheckedTotalSize_WithRunningSumAboveCap_Throws()
    {
        // The accumulated multi-part total is validated in 64-bit before the cast
        // back to int, so summing many near-cap sub-buffers cannot overflow Int32.
        Assert.Throws<InvalidOperationException>(
            () => EsriJsonWkbWriter.CheckedTotalSize((256L * 1024 * 1024) + 1));
    }

    [Fact]
    public void Write_PolygonMissingRings_Throws()
    {
        var element = Parse("""{"rings": []}""");

        Assert.Throws<InvalidOperationException>(() => EsriJsonWkbWriter.Write(element, MetadataV2GeometryType.Polygon));
    }

    [Fact]
    public void Write_UnsupportedShape_Throws()
    {
        var element = Parse("""{"hello": "world"}""");

        Assert.Throws<InvalidOperationException>(() => EsriJsonWkbWriter.Write(element, MetadataV2GeometryType.Point));
    }

    private static JsonElement Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }
}
