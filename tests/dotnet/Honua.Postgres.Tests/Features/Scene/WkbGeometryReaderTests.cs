// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Buffers.Binary;
using Honua.Core.Features.Scene.Domain;
using Honua.Postgres.Features.Scene;

namespace Honua.Postgres.Tests.Features.Scene;

/// <summary>
/// Verifies the minimal WKB reader used by <see cref="PostgresSceneFeatureSource"/>
/// correctly consumes (and ignores) the M ordinate so vertex streams from
/// PostGIS LineStringM/PolygonZM/etc. are not corrupted.
/// </summary>
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
        span[0] = 1; // little-endian
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
