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

    private static double[][] ClockwiseSquare(double minX, double minY, double maxX, double maxY) =>
    [
        [minX, minY], [minX, maxY], [maxX, maxY], [maxX, minY], [minX, minY]
    ];

    private static double[][] CounterClockwiseSquare(double minX, double minY, double maxX, double maxY) =>
    [
        [minX, minY], [maxX, minY], [maxX, maxY], [minX, maxY], [minX, minY]
    ];
}
