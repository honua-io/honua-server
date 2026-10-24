// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Protocols.GeoServices.GeometryService.Services;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using NetTopologySuite.Geometries;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.GeometryService;

/// <summary>
/// In-process coverage for GeometryServer reshape and trimExtend. These operations are
/// pure NetTopologySuite edits; the component host is not required to prove the geometry.
/// </summary>
[Protocol(TestProtocols.GeometryService)]
public sealed class GeometryEditOperationsTests
{
    private static readonly GeometryFactory Factory = new();

    [UnitTest]
    [Operation(Operations.Reshape)]
    public void Reshape_OutwardPolygon_UnionsLobe()
    {
        var target = Square();
        var reshaper = Line((1, 0), (2, 0.5), (1, 1));

        var reshaped = GeometryServiceHandler.ReshapeGeometry(target, reshaper);

        reshaped.Area.Should().BeApproximately(1.5, 1e-9);
        reshaped.Covers(Factory.CreatePoint(new Coordinate(1.25, 0.5))).Should().BeTrue();
    }

    [UnitTest]
    [Operation(Operations.Reshape)]
    public void Reshape_InwardPolygon_DropsLargerFace()
    {
        // Longer boundary (the three sides, length 3) encloses 0.25. The short bottom
        // edge encloses 0.75 with the reshaper. Largest-face selection would keep 0.75.
        var target = Square();
        const double inset = 0.75 / 0.875;
        var reshaper = Line((0, 0), (0.125, inset), (0.875, inset), (1, 0));

        var reshaped = GeometryServiceHandler.ReshapeGeometry(target, reshaper);

        reshaped.Area.Should().BeApproximately(0.25, 1e-9);
        reshaped.Contains(Factory.CreatePoint(new Coordinate(0.5, 0.95))).Should().BeTrue();
        reshaped.Contains(Factory.CreatePoint(new Coordinate(0.5, 0.1))).Should().BeFalse();
    }

    [UnitTest]
    [Operation(Operations.Reshape)]
    public void Reshape_Polyline_KeepsSectionsOutsideCrossings()
    {
        var target = Line((0, 0), (10, 0));
        var reshaper = Line((3, 0), (3, 2), (7, 2), (7, 0));

        var reshaped = GeometryServiceHandler.ReshapeGeometry(target, reshaper);

        reshaped.Length.Should().BeApproximately(14, 1e-9);
        reshaped.Coordinates[0].X.Should().BeApproximately(0, 1e-9);
        reshaped.Coordinates[0].Y.Should().BeApproximately(0, 1e-9);
        reshaped.Coordinates[^1].X.Should().BeApproximately(10, 1e-9);
        reshaped.Coordinates[^1].Y.Should().BeApproximately(0, 1e-9);
        reshaped.Covers(Factory.CreatePoint(new Coordinate(3, 2))).Should().BeTrue();
    }

    [UnitTest]
    [Operation(Operations.Reshape)]
    public void Reshape_PolylineWithOneCrossing_ReturnsTarget()
    {
        var target = Line((0, 0), (10, 0));
        var reshaper = Line((3, 0), (3, 2));

        var reshaped = GeometryServiceHandler.ReshapeGeometry(target, reshaper);

        reshaped.Coordinates.Should().Equal(target.Coordinates);
    }

    [UnitTest]
    [Operation(Operations.TrimExtend)]
    public void TrimExtend_TrimLineOrientedNorth_KeepsWestPart()
    {
        var line = Line((0, 0), (10, 0));
        var trim = Line((3, -2), (3, 2));

        var trimmed = GeometryServiceHandler.TrimOrExtend(line, trim);

        trimmed.Coordinates[0].X.Should().BeApproximately(0, 1e-9);
        trimmed.Coordinates[^1].X.Should().BeApproximately(3, 1e-9);
        trimmed.Coordinates[^1].Y.Should().BeApproximately(0, 1e-9);
    }

    [UnitTest]
    [Operation(Operations.TrimExtend)]
    public void TrimExtend_TrimLineOrientedSouth_KeepsEastPart()
    {
        var line = Line((0, 0), (10, 0));
        var trim = Line((3, 2), (3, -2));

        var trimmed = GeometryServiceHandler.TrimOrExtend(line, trim);

        trimmed.Coordinates[0].X.Should().BeApproximately(3, 1e-9);
        trimmed.Coordinates[0].Y.Should().BeApproximately(0, 1e-9);
        trimmed.Coordinates[^1].X.Should().BeApproximately(10, 1e-9);
    }

    [UnitTest]
    [Operation(Operations.TrimExtend)]
    public void TrimExtend_DisjointTrimLine_ExtendsEndToLine()
    {
        var line = Line((0, 0), (2, 0));
        var trim = Line((5, -2), (5, 2));

        var extended = GeometryServiceHandler.TrimOrExtend(line, trim);

        extended.Coordinates[^1].X.Should().BeApproximately(5, 1e-9);
        extended.Coordinates[^1].Y.Should().BeApproximately(0, 1e-9);
    }

    private static Polygon Square()
        => Factory.CreatePolygon(
        [
            new Coordinate(0, 0),
            new Coordinate(1, 0),
            new Coordinate(1, 1),
            new Coordinate(0, 1),
            new Coordinate(0, 0),
        ]);

    private static LineString Line(params (double X, double Y)[] coordinates)
        => Factory.CreateLineString(coordinates.Select(coordinate => new Coordinate(coordinate.X, coordinate.Y)).ToArray());
}
