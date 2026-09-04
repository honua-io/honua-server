// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Protocols.GeoServices;
using Honua.Protocols.GeoServices.FeatureServer.Models;
using Honua.TestKit.Attributes;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.FeatureServer.Services;

/// <summary>
/// Unit tests for <see cref="CurveGeometryConverter"/> true-curve densification and JSON
/// round-trip (#1877 Parts A and B). Circular-arc, elliptic-arc, and cubic-Bézier segments densify
/// to linear vertices within tolerance, preserve Z/M ordinates, and retain their JSON definition.
/// </summary>
public sealed class CurveGeometryConverterTests
{
    private static Geometry ReadGeometry(byte[] wkb) => new WKBReader().Read(wkb);

    private static GeoServicesGeometry ParseCurve(string json) => CurveGeometryConverter.Parse(json);

    #region Circular-arc densification

    [UnitTest]
    public void Densify_ThreePointArc_ProducesVerticesOnCircumcircle()
    {
        // Esri's c[1] is an interior point, not the center. These three points define a circle
        // centered at (0.5, 0.5) with radius sqrt(0.5), sweeping through (0,0).
        var json = """
        {
            "curvePaths": [
                [
                    [1.0, 0.0],
                    { "c": [[0.0, 1.0], [0.0, 0.0]] }
                ]
            ]
        }
        """;

        var densified = CurveGeometryConverter.Densify(ParseCurve(json));

        densified.Paths.Should().NotBeNull();
        var path = densified.Paths![0];
        path.Length.Should().BeGreaterThan(2, "the arc must be densified into multiple chords");

        // Independent Shapely 2.1.2 reference: a LineString sampled on the circumcircle has
        // distance 0 from the declared interior Point(0,0), center (0.5,0.5), radius sqrt(0.5).
        var expectedRadius = Math.Sqrt(0.5);
        foreach (var vertex in path)
        {
            var radius = Math.Sqrt(
                ((vertex[0] - 0.5) * (vertex[0] - 0.5)) +
                ((vertex[1] - 0.5) * (vertex[1] - 0.5)));
            radius.Should().BeApproximately(expectedRadius, 1e-6);
        }

        path.Should().Contain(v => Math.Abs(v[0]) < 1e-9 && Math.Abs(v[1]) < 1e-9,
            "the circular arc must pass through Esri's declared interior point");

        // Endpoints are exact.
        path[0][0].Should().BeApproximately(1.0, 1e-9);
        path[0][1].Should().BeApproximately(0.0, 1e-9);
        path[^1][0].Should().BeApproximately(0.0, 1e-9);
        path[^1][1].Should().BeApproximately(1.0, 1e-9);
    }

    [UnitTest]
    public void Densify_CircularArc_MidpointLiesOnArc()
    {
        // The halfway point of this semicircle is Esri's declared interior point (0,0).
        var json = """
        {
            "curvePaths": [
                [
                    [1.0, 0.0],
                    { "c": [[0.0, 1.0], [0.0, 0.0]] }
                ]
            ]
        }
        """;

        var path = CurveGeometryConverter.Densify(ParseCurve(json)).Paths![0];

        path.Should().Contain(
            v => Math.Abs(v[0]) < 1e-9 && Math.Abs(v[1]) < 1e-9,
            "the declared interior point must select the correct semicircle");
    }

    [UnitTest]
    public void Densify_CurveRings_ConvertsToWkbPolygon()
    {
        // A curved ring: a near-circular boundary built from one arc closing back to the start.
        var json = """
        {
            "curveRings": [
                [
                    [1.0, 0.0],
                    { "c": [[-1.0, 0.0], [0.0, 1.0]] },
                    { "c": [[1.0, 0.0], [0.0, -1.0]] }
                ]
            ]
        }
        """;

        var wkb = GeoServicesGeometryConverter.ConvertGeoServicesGeometryToWkb(ParseCurve(json));

        wkb.Should().NotBeNull();
        var geometry = ReadGeometry(wkb);
        geometry.Should().BeOfType<Polygon>();
        // Densified full circle radius 1 ~ area pi.
        geometry.Area.Should().BeApproximately(Math.PI, 0.05);
    }

    #endregion

    #region Elliptic-arc densification

    [UnitTest]
    public void Densify_EllipticArc_ProducesVerticesOnDeclaredEllipse()
    {
        // Axis-aligned quarter ellipse: center (0,0), semi-major 10, ratio .5 => semi-minor 5.
        // Independent Shapely 2.1.2 reference samples the same parametric ellipse; its midpoint
        // at theta=pi/4 is (7.071067811865476, 3.5355339059327373).
        var json = """
        {
            "curvePaths": [
                [
                    [10.0, 0.0],
                    { "a": [[0.0, 5.0], [0.0, 0.0], 1, 0, 0.0, 10.0, 0.5] }
                ]
            ]
        }
        """;

        var path = CurveGeometryConverter.Densify(ParseCurve(json)).Paths![0];

        path.Length.Should().BeGreaterThan(2);
        path.Should().Contain(v =>
            Math.Abs(v[0] - 7.071067811865476) < 1e-9 &&
            Math.Abs(v[1] - 3.5355339059327373) < 1e-9);
        foreach (var vertex in path)
        {
            var ellipseEquation = (vertex[0] * vertex[0] / 100.0) + (vertex[1] * vertex[1] / 25.0);
            ellipseEquation.Should().BeApproximately(1.0, 1e-9);
        }
    }

    [UnitTest]
    public void Densify_FullCircleAForm_ProducesClosedCircle()
    {
        var json = """
        {
            "curvePaths": [
                [
                    [3.5, 1.0],
                    { "a": [[3.5, 1.0], [3.0, 2.0], 0, 1] }
                ]
            ]
        }
        """;

        var path = CurveGeometryConverter.Densify(ParseCurve(json)).Paths![0];

        path.Length.Should().BeGreaterThan(100);
        path[0].Should().Equal(path[^1]);
        var expectedRadius = Math.Sqrt(1.25);
        foreach (var vertex in path)
        {
            var radius = Math.Sqrt(
                ((vertex[0] - 3.0) * (vertex[0] - 3.0)) +
                ((vertex[1] - 2.0) * (vertex[1] - 2.0)));
            radius.Should().BeApproximately(expectedRadius, 1e-6);
        }
    }

    #endregion

    #region Cubic-Bézier densification

    [UnitTest]
    public void Densify_CubicBezier_EndpointsExactAndMonotonicSampling()
    {
        // Bézier from (0,0) to (10,0) bulging upward via control points (3,5) and (7,5).
        var json = """
        {
            "curvePaths": [
                [
                    [0.0, 0.0],
                    { "b": [[10.0, 0.0], [3.0, 5.0], [7.0, 5.0]] }
                ]
            ]
        }
        """;

        var path = CurveGeometryConverter.Densify(ParseCurve(json)).Paths![0];

        // First vertex is the start; last is the declared endpoint.
        path[0][0].Should().BeApproximately(0.0, 1e-9);
        path[0][1].Should().BeApproximately(0.0, 1e-9);
        path[^1][0].Should().BeApproximately(10.0, 1e-9);
        path[^1][1].Should().BeApproximately(0.0, 1e-9);

        // Sampled count is bounded and stable (BezierSegments chords => start + BezierSegments points).
        path.Length.Should().Be(CurveGeometryConverter.BezierSegments + 1);

        // The midpoint t=0.5 of a symmetric cubic with control y=5 sits at y = 3/8*5 + 3/8*5 = 3.75.
        var mid = path[CurveGeometryConverter.BezierSegments / 2];
        mid[0].Should().BeApproximately(5.0, 1e-6);
        mid[1].Should().BeApproximately(3.75, 1e-6);
    }

    [UnitTest]
    public void Densify_CubicBezier_ConvertsToWkbLineString()
    {
        var json = """
        {
            "curvePaths": [
                [
                    [0.0, 0.0],
                    { "b": [[10.0, 0.0], [3.0, 5.0], [7.0, 5.0]] }
                ]
            ]
        }
        """;

        var wkb = GeoServicesGeometryConverter.ConvertGeoServicesGeometryToWkb(ParseCurve(json));
        var geometry = ReadGeometry(wkb);
        geometry.Should().BeOfType<LineString>();
        ((LineString)geometry).NumPoints.Should().Be(CurveGeometryConverter.BezierSegments + 1);
    }

    #endregion

    #region JSON round-trip

    [UnitTest]
    public void ParseThenSerialize_PreservesCurveDefinition()
    {
        var json = """
        {"curvePaths":[[[1,0],{"c":[[0,1],[0,0]]},{"b":[[5,5],[2,3],[4,4]]}]]}
        """;

        var model = CurveGeometryConverter.Parse(json);
        var roundTripped = CurveGeometryConverter.Serialize(model);

        // Compare structurally (whitespace/number-format independent).
        using var original = JsonDocument.Parse(json);
        using var actual = JsonDocument.Parse(roundTripped);

        var originalPaths = original.RootElement.GetProperty("curvePaths");
        var actualPaths = actual.RootElement.GetProperty("curvePaths");

        actualPaths.GetArrayLength().Should().Be(originalPaths.GetArrayLength());
        actualPaths[0].GetArrayLength().Should().Be(originalPaths[0].GetArrayLength());

        // The circular-arc segment "c" survives.
        actualPaths[0][1].TryGetProperty("c", out var c).Should().BeTrue();
        c[0][1].GetDouble().Should().Be(1.0);

        // The Bézier segment "b" survives.
        actualPaths[0][2].TryGetProperty("b", out var b).Should().BeTrue();
        b.GetArrayLength().Should().Be(3);
    }

    [UnitTest]
    public void HasTrueCurves_DetectsCurvePathsAndCurveRings()
    {
        CurveGeometryConverter.HasTrueCurves(
            ParseCurve("""{"curvePaths":[[[0,0],{"c":[[1,1],[0,1]]}]]}""")).Should().BeTrue();
        CurveGeometryConverter.HasTrueCurves(
            ParseCurve("""{"curveRings":[[[0,0],{"c":[[1,1],[0,1]]}]]}""")).Should().BeTrue();
        CurveGeometryConverter.HasTrueCurves(
            ParseCurve("""{"paths":[[[0,0],[1,1]]]}""")).Should().BeFalse();
    }

    #endregion

    #region Dimension preservation / malformed segments

    [UnitTest]
    public void Densify_CircularArcWithZAndM_InterpolatesEveryOrdinate()
    {
        var json = """
        {
            "hasZ": true,
            "hasM": true,
            "curvePaths": [
                [ [1.0, 0.0, 3.0, 4.0], { "c": [[0.0, 1.0, 5.0, 6.0], [0.0, 0.0]] } ]
            ]
        }
        """;

        var path = CurveGeometryConverter.Densify(ParseCurve(json)).Paths![0];

        path.Should().OnlyContain(vertex => vertex.Length == 4);
        var interior = path.Single(vertex => Math.Abs(vertex[0]) < 1e-9 && Math.Abs(vertex[1]) < 1e-9);
        interior[2].Should().BeApproximately(4.0, 1e-9);
        interior[3].Should().BeApproximately(5.0, 1e-9);
    }

    [UnitTest]
    public void Densify_CollinearCircularArc_EmitsFiniteLinearVertices()
    {
        var json = """
        {
            "curvePaths": [
                [ [0.0, 0.0], { "c": [[2.0, 0.0], [1.0, 0.0]] } ]
            ]
        }
        """;

        var path = CurveGeometryConverter.Densify(ParseCurve(json)).Paths![0];

        path.Should().HaveCount(3);
        path.Should().OnlyContain(vertex => vertex.All(double.IsFinite));
        path[1].Should().Equal(1.0, 0.0);
    }

    [UnitTest]
    public void Densify_UnknownSegmentKey_Throws()
    {
        var json = """
        {
            "curvePaths": [ [ [0.0, 0.0], { "z": [[1.0, 1.0]] } ] ]
        }
        """;

        var action = () => CurveGeometryConverter.Densify(ParseCurve(json));
        action.Should().Throw<ArgumentException>()
            .WithMessage("*Unknown true-curve segment*");
    }

    [UnitTest]
    public void Densify_SegmentBeforeStartVertex_Throws()
    {
        var json = """
        {
            "curvePaths": [ [ { "c": [[1.0, 1.0], [0.0, 0.0]] } ] ]
        }
        """;

        var action = () => CurveGeometryConverter.Densify(ParseCurve(json));
        action.Should().Throw<ArgumentException>()
            .WithMessage("*must follow a start vertex*");
    }

    #endregion
}
