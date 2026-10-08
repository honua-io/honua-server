// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Protocols.GeoServices.FeatureServer.Models;
using Honua.TestKit.Attributes;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;

namespace Honua.Protocols.GeoServices.Tests.Source.FeatureServer;

public sealed class GeoServicesGeometryConverterAuditTests
{
    [UnitTest]
    public void SRV_GS_019_ConvertPolygon_WithNestedLake_AssignsHoleToSmallestContainingShell()
    {
        var geometry = new GeoServicesGeometry
        {
            Rings =
            [
                ClockwiseSquare(0, 0, 20, 20),
                CounterClockwiseSquare(2, 2, 18, 18),
                ClockwiseSquare(5, 5, 15, 15),
                CounterClockwiseSquare(7, 7, 13, 13)
            ]
        };

        var converted = new WKBReader().Read(GeoServicesGeometryConverter.ConvertGeoServicesGeometryToWkb(geometry));

        var polygons = ((MultiPolygon)converted).Geometries.Cast<Polygon>().OrderByDescending(polygon => polygon.Area).ToArray();
        polygons.Should().HaveCount(2);
        polygons[0].NumInteriorRings.Should().Be(1);
        polygons[1].NumInteriorRings.Should().Be(1);
        polygons[1].GetInteriorRingN(0).EnvelopeInternal.Should().BeEquivalentTo(new Envelope(7, 13, 7, 13));
        converted.IsValid.Should().BeTrue();
    }

    [UnitTest]
    public void SRV_GS_019_ConvertPolygon_WithConcentricRings_AssignsEachHoleToShellContainingWholeRing()
    {
        // The interior point of the 2..18 hole lies at the center, which the nested 4..16 island also
        // covers; the hole must still belong to the 0..20 shell because only that shell contains the
        // whole hole ring.
        var geometry = new GeoServicesGeometry
        {
            Rings =
            [
                ClockwiseSquare(0, 0, 20, 20),
                CounterClockwiseSquare(2, 2, 18, 18),
                ClockwiseSquare(4, 4, 16, 16),
                CounterClockwiseSquare(6, 6, 14, 14)
            ]
        };

        var converted = new WKBReader().Read(GeoServicesGeometryConverter.ConvertGeoServicesGeometryToWkb(geometry));

        converted.IsValid.Should().BeTrue();
        var polygons = ((MultiPolygon)converted).Geometries.Cast<Polygon>().OrderByDescending(polygon => polygon.Area).ToArray();
        polygons.Should().HaveCount(2);
        polygons[0].Shell.EnvelopeInternal.Should().BeEquivalentTo(new Envelope(0, 20, 0, 20));
        polygons[0].NumInteriorRings.Should().Be(1);
        polygons[0].GetInteriorRingN(0).EnvelopeInternal.Should().BeEquivalentTo(new Envelope(2, 18, 2, 18));
        polygons[1].Shell.EnvelopeInternal.Should().BeEquivalentTo(new Envelope(4, 16, 4, 16));
        polygons[1].NumInteriorRings.Should().Be(1);
        polygons[1].GetInteriorRingN(0).EnvelopeInternal.Should().BeEquivalentTo(new Envelope(6, 14, 6, 14));
        converted.Area.Should().Be((20 * 20) - (16 * 16) + (12 * 12) - (8 * 8));
    }

    private static double[][] ClockwiseSquare(double minX, double minY, double maxX, double maxY) =>
    [
        [minX, minY], [minX, maxY], [maxX, maxY], [maxX, minY], [minX, minY]
    ];

    private static double[][] CounterClockwiseSquare(double minX, double minY, double maxX, double maxY) =>
    [
        [minX, minY], [maxX, minY], [maxX, maxY], [minX, maxY], [minX, minY]
    ];
}
