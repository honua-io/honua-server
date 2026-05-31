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
