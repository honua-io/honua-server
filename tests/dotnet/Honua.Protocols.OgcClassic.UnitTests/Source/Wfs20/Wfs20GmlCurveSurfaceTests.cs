// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Xml.Linq;
using FluentAssertions;
using Honua.TestKit.Attributes;
using NetTopologySuite.Geometries;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Classic.Wfs20;

[Protocol(TestProtocols.Wfs20)]
public sealed class Wfs20GmlCurveSurfaceTests
{
    [UnitTest]
    public void ParseTransactionGeometry_Curve_KeepsEveryLineStringSegment()
    {
        var geometry = Wfs20Handler.ParseTransactionGeometry(XElement.Parse("""
            <gml:Curve srsName="EPSG:3857" xmlns:gml="http://www.opengis.net/gml/3.2">
              <gml:segments>
                <gml:LineStringSegment>
                  <gml:posList>0 0 1 0</gml:posList>
                </gml:LineStringSegment>
                <gml:LineStringSegment>
                  <gml:posList>1 0 1 1</gml:posList>
                </gml:LineStringSegment>
              </gml:segments>
            </gml:Curve>
            """), 3857);

        geometry.Should().BeOfType<LineString>();
        geometry.Coordinates.Select(static coordinate => (coordinate.X, coordinate.Y))
            .Should().Equal((0d, 0d), (1d, 0d), (1d, 1d));
    }

    [UnitTest]
    public void ParseTransactionGeometry_Arc_SamplesTheCircularArc()
    {
        var geometry = Wfs20Handler.ParseTransactionGeometry(XElement.Parse("""
            <gml:Curve srsName="EPSG:3857" xmlns:gml="http://www.opengis.net/gml/3.2">
              <gml:segments>
                <gml:Arc>
                  <gml:posList>0 0 1 1 2 0</gml:posList>
                </gml:Arc>
              </gml:segments>
            </gml:Curve>
            """), 3857);

        geometry.NumPoints.Should().BeGreaterThan(3);
        geometry.Coordinates[0].X.Should().Be(0);
        geometry.Coordinates[0].Y.Should().Be(0);
        geometry.Coordinates[^1].X.Should().Be(2);
        geometry.Coordinates[^1].Y.Should().Be(0);
        geometry.Coordinates.Max(static coordinate => coordinate.Y).Should().BeGreaterThan(0.9);
        geometry.Coordinates.Should().OnlyContain(static coordinate =>
            Math.Abs((((coordinate.X - 1d) * (coordinate.X - 1d)) + (coordinate.Y * coordinate.Y)) - 1d) < 1e-6);
    }

    [UnitTest]
    public void ParseTransactionGeometry_UnsupportedCurveSegment_IsRejected()
    {
        var act = () => Wfs20Handler.ParseTransactionGeometry(XElement.Parse("""
            <gml:Curve srsName="EPSG:3857" xmlns:gml="http://www.opengis.net/gml/3.2">
              <gml:segments>
                <gml:ArcByCenterPoint>
                  <gml:pos>0 0</gml:pos>
                </gml:ArcByCenterPoint>
              </gml:segments>
            </gml:Curve>
            """), 3857);

        act.Should().Throw<ArgumentException>().WithMessage("*ArcByCenterPoint*");
    }

    [UnitTest]
    public void ParseTransactionGeometry_Surface_UnionsEveryPolygonPatch()
    {
        var geometry = Wfs20Handler.ParseTransactionGeometry(XElement.Parse("""
            <gml:Surface srsName="EPSG:3857" xmlns:gml="http://www.opengis.net/gml/3.2">
              <gml:patches>
                <gml:PolygonPatch>
                  <gml:exterior>
                    <gml:LinearRing>
                      <gml:posList>0 0 1 0 1 1 0 1 0 0</gml:posList>
                    </gml:LinearRing>
                  </gml:exterior>
                </gml:PolygonPatch>
                <gml:PolygonPatch>
                  <gml:exterior>
                    <gml:LinearRing>
                      <gml:posList>3 0 4 0 4 1 3 1 3 0</gml:posList>
                    </gml:LinearRing>
                  </gml:exterior>
                </gml:PolygonPatch>
              </gml:patches>
            </gml:Surface>
            """), 3857);

        geometry.Should().BeOfType<MultiPolygon>();
        geometry.NumGeometries.Should().Be(2);
        geometry.Area.Should().BeApproximately(2d, 1e-6);
    }

    [UnitTest]
    public void ParseTransactionGeometry_AdjacentSurfacePatches_UnionToOnePolygon()
    {
        var geometry = Wfs20Handler.ParseTransactionGeometry(XElement.Parse("""
            <gml:Surface srsName="EPSG:3857" xmlns:gml="http://www.opengis.net/gml/3.2">
              <gml:patches>
                <gml:PolygonPatch>
                  <gml:exterior>
                    <gml:LinearRing>
                      <gml:posList>0 0 1 0 1 1 0 1 0 0</gml:posList>
                    </gml:LinearRing>
                  </gml:exterior>
                </gml:PolygonPatch>
                <gml:PolygonPatch>
                  <gml:exterior>
                    <gml:LinearRing>
                      <gml:posList>1 0 2 0 2 1 1 1 1 0</gml:posList>
                    </gml:LinearRing>
                  </gml:exterior>
                </gml:PolygonPatch>
              </gml:patches>
            </gml:Surface>
            """), 3857);

        geometry.Should().BeOfType<Polygon>();
        geometry.Area.Should().BeApproximately(2d, 1e-6);
    }
}
