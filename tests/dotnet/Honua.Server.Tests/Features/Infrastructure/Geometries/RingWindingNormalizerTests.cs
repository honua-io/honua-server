// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Infrastructure.Geometries;
using NetTopologySuite.Algorithm;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;

namespace Honua.Server.Tests.Features.Infrastructure.Geometries;

/// <summary>
/// Verifies that the shared secondary-emitter winding helper rewrites polygon rings to the
/// RFC 7946 / ISO 19107 right-hand rule (exterior CCW, holes CW) so clockwise-exterior stored
/// geometry — common via Esri applyEdits and shapefile imports — is emitted correctly (#2745).
/// </summary>
public sealed class RingWindingNormalizerTests
{
    private static readonly GeometryFactory _factory =
        NetTopologySuite.NtsGeometryServices.Instance.CreateGeometryFactory(srid: 4326);

    // Unit square listed counter-clockwise (right-hand rule / CCW exterior).
    private static readonly Coordinate[] _ccwShell =
    [
        new(0, 0),
        new(1, 0),
        new(1, 1),
        new(0, 1),
        new(0, 0),
    ];

    // Inner hole listed counter-clockwise (which is the WRONG orientation for a hole).
    private static readonly Coordinate[] _ccwHole =
    [
        new(0.25, 0.25),
        new(0.75, 0.25),
        new(0.75, 0.75),
        new(0.25, 0.75),
        new(0.25, 0.25),
    ];

    [Fact]
    public void NormalizeToRightHandRule_ClockwiseExterior_ProducesCcwExterior()
    {
        var cw = _factory.CreatePolygon(Reverse(_ccwShell));

        var normalized = (Polygon)RingWindingNormalizer.NormalizeToRightHandRule(cw);

        Orientation.IsCCW(normalized.ExteriorRing.CoordinateSequence).Should().BeTrue();
    }

    [Fact]
    public void NormalizeToRightHandRule_CounterClockwiseHole_ProducesClockwiseHole()
    {
        var shell = _factory.CreateLinearRing(_ccwShell);
        var hole = _factory.CreateLinearRing(_ccwHole);
        var polygon = _factory.CreatePolygon(shell, [hole]);

        var normalized = (Polygon)RingWindingNormalizer.NormalizeToRightHandRule(polygon);

        Orientation.IsCCW(normalized.ExteriorRing.CoordinateSequence).Should().BeTrue();
        Orientation.IsCCW(normalized.GetInteriorRingN(0).CoordinateSequence).Should().BeFalse();
    }

    [Fact]
    public void NormalizeToRightHandRule_AlreadyRightHandRule_ReturnsSameReference()
    {
        var shell = _factory.CreateLinearRing(_ccwShell);
        var hole = _factory.CreateLinearRing(Reverse(_ccwHole)); // CW hole = already correct
        var polygon = _factory.CreatePolygon(shell, [hole]);

        var normalized = RingWindingNormalizer.NormalizeToRightHandRule(polygon);

        normalized.Should().BeSameAs(polygon);
    }

    [Fact]
    public void NormalizeToRightHandRule_Point_ReturnsSameReference()
    {
        var point = _factory.CreatePoint(new Coordinate(3, 4));

        var normalized = RingWindingNormalizer.NormalizeToRightHandRule(point);

        normalized.Should().BeSameAs(point);
    }

    [Fact]
    public void NormalizeToRightHandRule_MultiPolygon_NormalizesEachMember()
    {
        var cwA = _factory.CreatePolygon(Reverse(_ccwShell));
        var cwB = _factory.CreatePolygon(Reverse(Translate(_ccwShell, 10, 0)));
        var multi = _factory.CreateMultiPolygon([cwA, cwB]);

        var normalized = (MultiPolygon)RingWindingNormalizer.NormalizeToRightHandRule(multi);

        for (var i = 0; i < normalized.NumGeometries; i++)
        {
            var polygon = (Polygon)normalized.GetGeometryN(i);
            Orientation.IsCCW(polygon.ExteriorRing.CoordinateSequence).Should().BeTrue();
        }
    }

    [Fact]
    public void NormalizeToRightHandRule_GeometryCollectionWithClockwisePolygon_NormalizesPolygon()
    {
        var point = _factory.CreatePoint(new Coordinate(3, 4));
        var cwPolygon = _factory.CreatePolygon(Reverse(_ccwShell));
        var collection = _factory.CreateGeometryCollection([point, cwPolygon]);

        var normalized = (GeometryCollection)RingWindingNormalizer.NormalizeToRightHandRule(collection);

        var reorientedPolygon = (Polygon)normalized.GetGeometryN(1);
        Orientation.IsCCW(reorientedPolygon.ExteriorRing.CoordinateSequence).Should().BeTrue();
    }

    [Fact]
    public void NormalizeToRightHandRule_ReorientedCopy_PreservesSourceSrid()
    {
        // A factory whose SRID is 0 does not stamp the geometry's SRID onto the reoriented copy;
        // the normalizer must carry the source geometry's SRID (3857) across the reversal (#2745).
        var sridlessFactory = NetTopologySuite.NtsGeometryServices.Instance.CreateGeometryFactory(srid: 0);
        var cw = sridlessFactory.CreatePolygon(Reverse(_ccwShell));
        cw.SRID = 3857;

        var normalized = RingWindingNormalizer.NormalizeToRightHandRule(cw);

        normalized.Should().NotBeSameAs(cw, "a clockwise exterior must be reoriented to a new geometry");
        normalized.SRID.Should().Be(3857);
    }

    [Fact]
    public void WriteGeoJson_ClockwiseExterior_EmitsCounterClockwiseExterior()
    {
        var cw = _factory.CreatePolygon(Reverse(_ccwShell));

        var geoJson = RingWindingNormalizer.WriteGeoJson(new GeoJsonWriter(), cw);

        var readBack = (Polygon)new GeoJsonReader().Read<Geometry>(geoJson);
        Orientation.IsCCW(readBack.ExteriorRing.CoordinateSequence).Should().BeTrue();
    }

    private static Coordinate[] Reverse(Coordinate[] ring)
    {
        var reversed = new Coordinate[ring.Length];
        for (var i = 0; i < ring.Length; i++)
        {
            reversed[i] = ring[ring.Length - 1 - i];
        }

        return reversed;
    }

    private static Coordinate[] Translate(Coordinate[] ring, double dx, double dy)
    {
        var moved = new Coordinate[ring.Length];
        for (var i = 0; i < ring.Length; i++)
        {
            moved[i] = new Coordinate(ring[i].X + dx, ring[i].Y + dy);
        }

        return moved;
    }
}
