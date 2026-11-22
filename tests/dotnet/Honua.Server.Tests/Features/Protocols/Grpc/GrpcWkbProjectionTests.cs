// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Buffers.Binary;
using System.Collections.Immutable;
using FluentAssertions;
using Honua.Core.Configuration;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Server.Features.Protocols.Grpc;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using NetTopologySuite.IO;
using Proto = Geospatial.V1;

namespace Honua.Server.Tests.Features.Protocols.Grpc;

[Protocol(TestProtocols.Grpc)]
[Operation(Operations.Query)]
public sealed class GrpcWkbProjectionTests
{
    private static readonly WKTReader _wktReader = new();

    public static TheoryData<string, int> SimpleGeometries => new()
    {
        { "POINT (1.123456789 2.987654321)", -1 },
        { "POINT Z (1.123456789 2.5 3.333333333)", 3 },
        { "POINT M (1 2 7.123456789)", 8 },
        { "POINT ZM (1 2 3 4)", 0 },
        { "LINESTRING (0 0, 1.55555 1, 2 0.123456789)", 2 },
        { "LINESTRING Z (0 0 1, 1 1 2, 2 0 3)", -1 },
        { "POLYGON ((0 0, 10 0, 10 10, 0 10, 0 0), (2 2, 2 3, 3 3, 2 2))", 8 },
        { "POLYGON ZM ((0 0 1 5, 10 0 1 5, 10 10 1 5, 0 0 1 5))", 1 },
        { "MULTIPOINT ((1 2), (3.000000004 4))", 8 },
        { "MULTILINESTRING ((0 0, 1 1), (2 2, 3 3, 4 4))", -1 },
        { "LINESTRING M (0 0 1.25, 1 1 2.5)", 1 },
        { "MULTILINESTRING ZM ((0 0 1 2, 1 1 3 4), (5 5 6 7, 6 6 8 9))", 8 },
        { "MULTIPOLYGON (((0 0, 1 0, 1 1, 0 0)), ((5 5, 6 5, 6 6, 5 5), (5.2 5.1, 5.8 5.1, 5.8 5.7, 5.2 5.1)))", 8 },
    };

    [UnitTheory]
    [MemberData(nameof(SimpleGeometries))]
    public void Project_SimpleGeometry_MatchesTheNetTopologySuitePath(string wkt, int precision)
    {
        foreach (var wkb in Encodings(wkt))
        {
            GrpcWkbProjection.Project(wkb, precision).Should().Be(ThroughNts(wkb, precision), wkt);
        }
    }

    [UnitTest]
    public void Project_IsoZPoint_MatchesTheNetTopologySuitePath()
    {
        var wkb = new byte[1 + 4 + 24];
        wkb[0] = 1;
        BinaryPrimitives.WriteUInt32LittleEndian(wkb.AsSpan(1), 1001);
        BinaryPrimitives.WriteDoubleLittleEndian(wkb.AsSpan(5), 1.5);
        BinaryPrimitives.WriteDoubleLittleEndian(wkb.AsSpan(13), 2.5);
        BinaryPrimitives.WriteDoubleLittleEndian(wkb.AsSpan(21), 3.5);

        var projected = GrpcWkbProjection.Project(wkb, -1);

        projected.Should().Be(ThroughNts(wkb, -1));
        projected!.Point.Z.Should().Be(3.5);
    }

    [UnitTest]
    public void Project_EwkbFlagsWithIsoCode_ReadsTheSameDimensions()
    {
        var wkb = new byte[1 + 4 + 24];
        wkb[0] = 1;
        BinaryPrimitives.WriteUInt32LittleEndian(wkb.AsSpan(1), 0x80000000 | 1001);
        BinaryPrimitives.WriteDoubleLittleEndian(wkb.AsSpan(5), 1);
        BinaryPrimitives.WriteDoubleLittleEndian(wkb.AsSpan(13), 2);
        BinaryPrimitives.WriteDoubleLittleEndian(wkb.AsSpan(21), 3);

        GrpcWkbProjection.Project(wkb, -1)!.Point.Z.Should().Be(3);
    }

    public static TheoryData<string> DeclinedGeometries => new()
    {
        "POINT EMPTY",
        "LINESTRING EMPTY",
        "POLYGON EMPTY",
        "MULTIPOINT EMPTY",
        "GEOMETRYCOLLECTION (POINT (1 2))",
    };

    [UnitTheory]
    [MemberData(nameof(DeclinedGeometries))]
    public void Project_EmptyOrCollection_IsLeftToNetTopologySuite(string wkt)
    {
        var wkb = new WKBWriter().Write(_wktReader.Read(wkt));

        GrpcWkbProjection.Project(wkb, -1).Should().BeNull(wkt);
    }

    [UnitTest]
    public void Project_MalformedInput_IsLeftToNetTopologySuite()
    {
        var point = new WKBWriter().Write(_wktReader.Read("POINT (1 2)"));
        var unclosed = LittleEndianPolygon((0, 0), (1, 0), (1, 1), (0, 1));
        var shortRing = LittleEndianPolygon((0, 0), (1, 0), (0, 0));

        GrpcWkbProjection.Project(point.AsSpan(..^1), -1).Should().BeNull("truncated");
        GrpcWkbProjection.Project([.. point, 0], -1).Should().BeNull("trailing bytes");
        GrpcWkbProjection.Project(unclosed, -1).Should().BeNull("unclosed ring");
        GrpcWkbProjection.Project(shortRing, -1).Should().BeNull("ring under four points");
        GrpcWkbProjection.Project([], -1).Should().BeNull("empty input");
    }

    [UnitTest]
    public void Project_NaNRingOrHugeCount_IsLeftToNetTopologySuite()
    {
        var nanRing = LittleEndianPolygon((double.NaN, 0), (1, 0), (1, 1), (double.NaN, 0));
        var hugeCount = new byte[1 + 4 + 4];
        hugeCount[0] = 1;
        BinaryPrimitives.WriteUInt32LittleEndian(hugeCount.AsSpan(1), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(hugeCount.AsSpan(5), uint.MaxValue);

        GrpcWkbProjection.Project(nanRing, -1).Should().BeNull("NaN never closes a ring");
        GrpcWkbProjection.Project(hugeCount, -1).Should().BeNull("count larger than the payload");
    }

    [UnitTest]
    public void Project_PartsInEitherByteOrder_MatchTheNetTopologySuitePath()
    {
        var bigEndianPoint = new WKBWriter(ByteOrder.BigEndian).Write(_wktReader.Read("POINT (3 4)"));
        var littleEndianPoint = new WKBWriter(ByteOrder.LittleEndian).Write(_wktReader.Read("POINT (1 2)"));
        byte[] header = [1, 4, 0, 0, 0, 2, 0, 0, 0];
        byte[] wkb = [.. header, .. littleEndianPoint, .. bigEndianPoint];

        GrpcWkbProjection.Project(wkb, -1).Should().Be(ThroughNts(wkb, -1));
        GrpcWkbProjection.Project(wkb, -1)!.MultiPoint.Points.Should().HaveCount(2);
    }

    [UnitTest]
    public void ToProtoFeature_WithDefaultLimits_MatchesTheNetTopologySuitePath()
    {
        var wkb = new WKBWriter().Write(_wktReader.Read("POLYGON ((0 0, 1.123456789 0, 1 1, 0 0))"));
        var limits = new GeometryLimits();

        var proto = GrpcConversionHelpers.ToProtoFeature(Feature.Create(1, wkb, ImmutableDictionary<string, object?>.Empty), true, limits);

        proto.Geometry.Should().Be(GrpcConversionHelpers.ToProtoGeometryThroughNts(wkb, limits));
        proto.Geometry.Polygon.Rings[0].Coords[1].X.Should().Be(1.12345679);
    }

    [UnitTest]
    public void Project_RingClosedOnlyAfterRounding_IsLeftToNetTopologySuite()
    {
        var almostClosed = LittleEndianPolygon((0, 0), (1, 0), (1, 1), (0.0000001, 0));

        GrpcWkbProjection.Project(almostClosed, 3).Should().BeNull();
    }

    [UnitTest]
    public void ToProtoFeature_WithSimplificationTolerance_TakesTheGeneralPath()
    {
        var line = _wktReader.Read("LINESTRING (0 0, 0.5 0.0000001, 1 0)");
        line.SRID = 4326;
        var wkb = new WKBWriter(ByteOrder.LittleEndian, true).Write(line);
        var limits = new GeometryLimits { SimplifyTolerance = 1000, MaxVerticesPerGeometry = 1, MaxCoordinatePrecision = 8 };

        var proto = GrpcConversionHelpers.ToProtoFeature(Feature.Create(1, wkb, ImmutableDictionary<string, object?>.Empty), true, limits);

        proto.Geometry.Polyline.Paths[0].Coords.Should().HaveCount(2);
    }

    [UnitTest]
    public void ToUnixMilliseconds_LocalTime_ConvertsInsteadOfThrowing()
    {
        var utc = new DateTime(2026, 11, 21, 12, 0, 0, DateTimeKind.Utc);
        var local = new DateTimeOffset(2026, 11, 21, 17, 0, 0, TimeSpan.FromHours(5)).LocalDateTime;

        GrpcConversionHelpers.ToUnixMilliseconds(local).Should().Be(1_795_262_400_000);
        GrpcConversionHelpers.ToUnixMilliseconds(utc).Should().Be(1_795_262_400_000);
        GrpcConversionHelpers.ToUnixMilliseconds(DateTime.SpecifyKind(utc, DateTimeKind.Unspecified))
            .Should().Be(1_795_262_400_000);
    }

    [UnitTest]
    public void SelectResponseFields_DeclaresTheRequestedFieldsAndTheObjectId()
    {
        MetadataV2Field[] schema =
        [
            new() { Name = "objectid", Type = MetadataV2FieldType.Integer },
            new() { Name = "Name", Type = MetadataV2FieldType.String },
            new() { Name = "height", Type = MetadataV2FieldType.Double },
        ];

        GrpcConversionHelpers.SelectResponseFields(schema, ["name"], "objectid")
            .Select(field => field.Name).Should().Equal("objectid", "Name");
        GrpcConversionHelpers.SelectResponseFields(schema, null, "objectid").Should().BeSameAs(schema);
        GrpcConversionHelpers.SelectResponseFields(schema, ["name"], objectIdFieldName: null)
            .Select(field => field.Name).Should().Equal("Name");
    }

    private static IEnumerable<byte[]> Encodings(string wkt)
    {
        var geometry = _wktReader.Read(wkt);
        geometry.SRID = 4326;
        var (hasZ, hasM) = DimensionsOf(wkt);
        yield return new WKBWriter(ByteOrder.LittleEndian, false, hasZ, hasM).Write(geometry);
        yield return new WKBWriter(ByteOrder.BigEndian, true, hasZ, hasM).Write(geometry);
    }

    private static (bool HasZ, bool HasM) DimensionsOf(string wkt)
        => wkt.Contains(" ZM ", StringComparison.Ordinal) ? (true, true)
            : wkt.Contains(" Z ", StringComparison.Ordinal) ? (true, false)
            : (false, wkt.Contains(" M ", StringComparison.Ordinal));

    private static Proto.Geometry ThroughNts(byte[] wkb, int precision)
        => GrpcConversionHelpers.ToProtoGeometryThroughNts(
            wkb, precision < 0 ? null : new GeometryLimits { MaxCoordinatePrecision = precision });

    private static byte[] LittleEndianPolygon(params (double X, double Y)[] ring)
    {
        var wkb = new byte[1 + 4 + 4 + 4 + (ring.Length * 16)];
        wkb[0] = 1;
        BinaryPrimitives.WriteUInt32LittleEndian(wkb.AsSpan(1), 3);
        BinaryPrimitives.WriteUInt32LittleEndian(wkb.AsSpan(5), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(wkb.AsSpan(9), (uint)ring.Length);
        for (var i = 0; i < ring.Length; i++)
        {
            BinaryPrimitives.WriteDoubleLittleEndian(wkb.AsSpan(13 + (i * 16)), ring[i].X);
            BinaryPrimitives.WriteDoubleLittleEndian(wkb.AsSpan(21 + (i * 16)), ring[i].Y);
        }

        return wkb;
    }
}
