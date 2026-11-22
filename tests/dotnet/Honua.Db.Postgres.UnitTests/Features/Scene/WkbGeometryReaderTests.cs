// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Buffers.Binary;
using Honua.Core.Exceptions;
using Honua.Core.Features.Scene.Domain;
using Honua.Db.Postgres.Features.Scene;

namespace Honua.Db.Postgres.Tests.Features.Scene;

[global::Xunit.Trait("Tier", "Fast")]
public sealed class WkbGeometryReaderTests
{
    [Fact]
    public void Parse_IsoLineStringM_DiscardsMOrdinateAndKeepsXY()
    {
        // ISO type 2002 = LineStringM. Three vertices with X/Y/M.
        var vertices = new[]
        {
            (X: -122.5, Y: 37.7, M: 0.0),
            (X: -122.4, Y: 37.8, M: 12.5),
            (X: -122.3, Y: 37.9, M: 25.0)
        };

        var wkb = BuildLineString(vertices, isoType: 2002, hasZ: false, hasM: true);

        var geometry = WkbGeometryReader.Parse(wkb);

        geometry.Should().NotBeNull();
        geometry!.Kind.Should().Be(SceneGeometryKind.LineString);
        geometry.Vertices.Should().HaveCount(3);
        // Each vertex's X/Y must come from the source — without consuming
        // the M ordinate, the second vertex's X would slide into M (12.5).
        geometry.Vertices[0].Longitude.Should().BeApproximately(-122.5, 1e-9);
        geometry.Vertices[0].Latitude.Should().BeApproximately(37.7, 1e-9);
        geometry.Vertices[0].Height.Should().BeNull("M-only sources have no Z");
        geometry.Vertices[1].Longitude.Should().BeApproximately(-122.4, 1e-9);
        geometry.Vertices[1].Latitude.Should().BeApproximately(37.8, 1e-9);
        geometry.Vertices[2].Longitude.Should().BeApproximately(-122.3, 1e-9);
        geometry.Vertices[2].Latitude.Should().BeApproximately(37.9, 1e-9);
    }

    [Fact]
    public void Parse_IsoLineStringZM_KeepsZAndDiscardsM()
    {
        // ISO type 3002 = LineStringZM. Each vertex is X/Y/Z/M.
        var vertices = new[]
        {
            (X: -122.5, Y: 37.7, Z: 10.0, M: 0.0),
            (X: -122.4, Y: 37.8, Z: 20.0, M: 12.5)
        };

        var wkb = BuildLineStringZM(vertices, isoType: 3002);

        var geometry = WkbGeometryReader.Parse(wkb);

        geometry.Should().NotBeNull();
        geometry!.Vertices.Should().HaveCount(2);
        geometry.Vertices[0].Longitude.Should().BeApproximately(-122.5, 1e-9);
        geometry.Vertices[0].Latitude.Should().BeApproximately(37.7, 1e-9);
        geometry.Vertices[0].Height.Should().Be(10.0);
        geometry.Vertices[1].Longitude.Should().BeApproximately(-122.4, 1e-9);
        geometry.Vertices[1].Latitude.Should().BeApproximately(37.8, 1e-9);
        geometry.Vertices[1].Height.Should().Be(20.0);
    }

    [Fact]
    public void Parse_EwkbLineStringM_DiscardsMOrdinate()
    {
        // EWKB encoding: high bit 0x40000000 marks M dimension.
        // Type word = 2 | 0x40000000 = 0x40000002.
        var vertices = new[]
        {
            (X: 1.0, Y: 2.0, M: 999.0),
            (X: 3.0, Y: 4.0, M: 999.0)
        };

        var wkb = BuildLineString(vertices, ewkbType: 0x40000002, hasZ: false, hasM: true);

        var geometry = WkbGeometryReader.Parse(wkb);

        geometry.Should().NotBeNull();
        geometry!.Vertices[0].Longitude.Should().Be(1.0);
        geometry.Vertices[0].Latitude.Should().Be(2.0);
        geometry.Vertices[1].Longitude.Should().Be(3.0);
        geometry.Vertices[1].Latitude.Should().Be(4.0);
    }

    [Fact]
    public void Parse_IsoPolygonM_DiscardsMOrdinate()
    {
        // ISO type 2003 = PolygonM. Single ring, 4 closed vertices.
        var ring = new[]
        {
            (X: 0.0, Y: 0.0, M: 0.0),
            (X: 1.0, Y: 0.0, M: 0.0),
            (X: 1.0, Y: 1.0, M: 0.0),
            (X: 0.0, Y: 0.0, M: 0.0)
        };
        var wkb = BuildSingleRingPolygon(ring, isoType: 2003, hasZ: false, hasM: true);

        var geometry = WkbGeometryReader.Parse(wkb);

        geometry.Should().NotBeNull();
        geometry!.Kind.Should().Be(SceneGeometryKind.Polygon);
        geometry.Vertices.Should().HaveCount(4);
        geometry.Vertices[2].Longitude.Should().Be(1.0);
        geometry.Vertices[2].Latitude.Should().Be(1.0);
    }

    [Fact]
    public void Parse_GeometryCollection_ThrowsUnsupportedGeometryType()
    {
        // OGC type 7 = GeometryCollection. The v1 generator only supports
        // Point/LineString/Polygon and their Multi variants; an unsupported
        // type must surface as SCENE_UNSUPPORTED_GEOMETRY_TYPE rather than
        // be silently skipped (which would quietly produce a tileset with
        // missing data).
        var buffer = new byte[1 + 4 + 4]; // header + count, no actual contents needed
        var span = buffer.AsSpan();
        span[0] = 1;
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(1, 4), 7u); // GeometryCollection
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(5, 4), 0u);

        var act = () => WkbGeometryReader.Parse(buffer);

        var ex = act.Should().Throw<ValidationException>().Which;
        ex.Message.Should().StartWith(SceneGenerationErrorCodes.UnsupportedGeometryType);
        ex.Message.Should().Contain("7");
    }

    [Fact]
    public void Parse_CircularString_ThrowsUnsupportedGeometryType()
    {
        // OGC type 8 = CircularString. Same rule as GeometryCollection.
        var buffer = new byte[1 + 4];
        var span = buffer.AsSpan();
        span[0] = 1;
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(1, 4), 8u);

        var act = () => WkbGeometryReader.Parse(buffer);

        act.Should().Throw<ValidationException>()
            .Which.Message.Should().StartWith(SceneGenerationErrorCodes.UnsupportedGeometryType);
    }

    [Fact]
    public void Parse_IsoLineStringZ_StillReadsZ()
    {
        // Regression guard: ZM handling must not break the simple Z path.
        var vertices = new[]
        {
            (X: -122.5, Y: 37.7, Z: 10.0, M: 0.0),
            (X: -122.4, Y: 37.8, Z: 20.0, M: 0.0)
        };

        var wkb = BuildLineStringZM(vertices, isoType: 1002);

        var geometry = WkbGeometryReader.Parse(wkb);

        geometry.Should().NotBeNull();
        geometry!.Vertices[0].Height.Should().Be(10.0);
        geometry.Vertices[1].Height.Should().Be(20.0);
    }

    [Fact]
    public void Parse_EwkbWithEmbeddedSrid_SkipsSridAndReadsCoordinates()
    {
        // EWKB PointZ with SRID flag (0x80000000 | 0x20000000 | 1 = 0xA0000001), SRID = 4326.
        var buffer = new byte[1 + 4 + 4 + 24];
        var span = buffer.AsSpan();
        span[0] = 1;
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(1, 4), 0xA0000001u);
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(5, 4), 4326u);
        BinaryPrimitives.WriteDoubleLittleEndian(span.Slice(9, 8), -122.4194);
        BinaryPrimitives.WriteDoubleLittleEndian(span.Slice(17, 8), 37.7749);
        BinaryPrimitives.WriteDoubleLittleEndian(span.Slice(25, 8), 42.5);

        var geometry = WkbGeometryReader.Parse(buffer);

        geometry.Should().NotBeNull();
        geometry!.Kind.Should().Be(SceneGeometryKind.Point);
        geometry.Vertices.Should().ContainSingle();
        geometry.Vertices[0].Longitude.Should().BeApproximately(-122.4194, 1e-9);
        geometry.Vertices[0].Latitude.Should().BeApproximately(37.7749, 1e-9);
        geometry.Vertices[0].Height.Should().BeApproximately(42.5, 1e-9);
    }

    [Fact]
    public void Parse_MultiGeometries_ReadsAllChildParts()
    {
        // MultiPoint with 2 points
        var mp = new byte[1 + 4 + 4 + 2 * (1 + 4 + 16)];
        var span = mp.AsSpan();
        span[0] = 1;
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(1, 4), 4u); // MultiPoint
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(5, 4), 2u); // 2 child points
        // child 0
        span[9] = 1;
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(10, 4), 1u);
        BinaryPrimitives.WriteDoubleLittleEndian(span.Slice(14, 8), -122.5);
        BinaryPrimitives.WriteDoubleLittleEndian(span.Slice(22, 8), 37.7);
        // child 1
        span[30] = 1;
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(31, 4), 1u);
        BinaryPrimitives.WriteDoubleLittleEndian(span.Slice(35, 8), -122.4);
        BinaryPrimitives.WriteDoubleLittleEndian(span.Slice(43, 8), 37.8);

        var parsedMp = WkbGeometryReader.Parse(mp);
        parsedMp.Should().NotBeNull();
        parsedMp!.Kind.Should().Be(SceneGeometryKind.Point);
        parsedMp.Vertices.Should().HaveCount(2);
        parsedMp.Parts.Should().HaveCount(2);
        parsedMp.Vertices[1].Longitude.Should().BeApproximately(-122.4, 1e-9);
        parsedMp.Vertices[1].Latitude.Should().BeApproximately(37.8, 1e-9);
    }

    private static byte[] BuildLineString(
        (double X, double Y, double M)[] vertices,
        int? isoType = null,
        uint? ewkbType = null,
        bool hasZ = false,
        bool hasM = false)
    {
        var ordinates = (hasZ ? 1 : 0) + (hasM ? 1 : 0);
        var size = 1 + 4 + 4 + vertices.Length * (16 + ordinates * 8);
        var buffer = new byte[size];
        var span = buffer.AsSpan();
        span[0] = 1;
        var typeWord = ewkbType ?? (uint)isoType!.Value;
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(1, 4), typeWord);
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(5, 4), (uint)vertices.Length);
        var pos = 9;
        foreach (var (x, y, m) in vertices)
        {
            BinaryPrimitives.WriteDoubleLittleEndian(span.Slice(pos, 8), x); pos += 8;
            BinaryPrimitives.WriteDoubleLittleEndian(span.Slice(pos, 8), y); pos += 8;
            if (hasZ)
            {
                BinaryPrimitives.WriteDoubleLittleEndian(span.Slice(pos, 8), 0.0); pos += 8;
            }
            if (hasM)
            {
                BinaryPrimitives.WriteDoubleLittleEndian(span.Slice(pos, 8), m); pos += 8;
            }
        }
        return buffer;
    }

    private static byte[] BuildLineStringZM(
        (double X, double Y, double Z, double M)[] vertices,
        int isoType)
    {
        var hasZ = isoType / 1000 is 1 or 3;
        var hasM = isoType / 1000 is 2 or 3;
        var ordinates = (hasZ ? 1 : 0) + (hasM ? 1 : 0);
        var size = 1 + 4 + 4 + vertices.Length * (16 + ordinates * 8);
        var buffer = new byte[size];
        var span = buffer.AsSpan();
        span[0] = 1;
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(1, 4), (uint)isoType);
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(5, 4), (uint)vertices.Length);
        var pos = 9;
        foreach (var (x, y, z, m) in vertices)
        {
            BinaryPrimitives.WriteDoubleLittleEndian(span.Slice(pos, 8), x); pos += 8;
            BinaryPrimitives.WriteDoubleLittleEndian(span.Slice(pos, 8), y); pos += 8;
            if (hasZ) { BinaryPrimitives.WriteDoubleLittleEndian(span.Slice(pos, 8), z); pos += 8; }
            if (hasM) { BinaryPrimitives.WriteDoubleLittleEndian(span.Slice(pos, 8), m); pos += 8; }
        }
        return buffer;
    }

    private static byte[] BuildSingleRingPolygon(
        (double X, double Y, double M)[] ring,
        int isoType,
        bool hasZ,
        bool hasM)
    {
        var ordinates = (hasZ ? 1 : 0) + (hasM ? 1 : 0);
        var size = 1 + 4 + 4 + 4 + ring.Length * (16 + ordinates * 8);
        var buffer = new byte[size];
        var span = buffer.AsSpan();
        span[0] = 1;
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(1, 4), (uint)isoType);
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(5, 4), 1u); // ring count
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(9, 4), (uint)ring.Length);
        var pos = 13;
        foreach (var (x, y, m) in ring)
        {
            BinaryPrimitives.WriteDoubleLittleEndian(span.Slice(pos, 8), x); pos += 8;
            BinaryPrimitives.WriteDoubleLittleEndian(span.Slice(pos, 8), y); pos += 8;
            if (hasZ) { BinaryPrimitives.WriteDoubleLittleEndian(span.Slice(pos, 8), 0.0); pos += 8; }
            if (hasM) { BinaryPrimitives.WriteDoubleLittleEndian(span.Slice(pos, 8), m); pos += 8; }
        }
        return buffer;
    }
}
