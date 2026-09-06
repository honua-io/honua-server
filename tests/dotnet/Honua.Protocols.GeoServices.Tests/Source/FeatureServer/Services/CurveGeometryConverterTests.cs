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
/// Unit tests for <see cref="GeoServicesGeometryConverter"/> true-curve densification and JSON
/// round-trip (#1877 Parts A and B). Circular-arc, elliptic-arc, and cubic-Bézier geometries convert to WKB
/// through the shared densifier; the curve definition survives a parse+serialize cycle.
/// </summary>
public sealed class CurveGeometryConverterTests
{
    private static Geometry ReadGeometry(byte[] wkb) => new WKBReader().Read(wkb);

    private static GeoServicesGeometry ParseCurve(string json) => GeoServicesGeometryConverter.ParseCurveGeometry(json);

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
        ((LineString)geometry).NumPoints.Should().Be(32 + 1);
    }

    [UnitTest]
    public void ParseThenSerialize_PreservesCurveDefinition()
    {
        var json = """
        {"curvePaths":[[[1,0],{"c":[[0,1],[0,0]]},{"b":[[5,5],[2,3],[4,4]]}]]}
        """;

        var model = GeoServicesGeometryConverter.ParseCurveGeometry(json);
        var roundTripped = GeoServicesGeometryConverter.SerializeCurveGeometry(model);

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
        GeoServicesGeometryConverter.HasTrueCurves(
            ParseCurve("""{"curvePaths":[[[0,0],{"c":[[1,1],[0,1]]}]]}""")).Should().BeTrue();
        GeoServicesGeometryConverter.HasTrueCurves(
            ParseCurve("""{"curveRings":[[[0,0],{"c":[[1,1],[0,1]]}]]}""")).Should().BeTrue();
        GeoServicesGeometryConverter.HasTrueCurves(
            ParseCurve("""{"paths":[[[0,0],[1,1]]]}""")).Should().BeFalse();
    }

}
