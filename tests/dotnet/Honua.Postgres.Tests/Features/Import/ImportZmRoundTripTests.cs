// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Reflection;
using System.Text.Json;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;
using Honua.Postgres.Features.FileImport;
using Honua.Postgres.Features.Migration;

namespace Honua.Postgres.Tests.Features.Import;

/// <summary>
/// Regression tests for #1981: Esri/file import must carry Z and M ordinates
/// instead of silently flattening geometries to 2-D (or M-less). The streaming
/// file-import path selects WKB writer dimensionality from the source geometry,
/// so XYZ, XYM, and XYZM geometries round-trip through WKB.
/// </summary>
public sealed class ImportZmRoundTripTests
{
    private static readonly GeometryFactory Factory = new();

    private static WKBWriter InvokeSelectWkbWriter(Geometry geometry)
    {
        var method = typeof(StreamingFileImportService).GetMethod(
            "SelectWkbWriter",
            BindingFlags.NonPublic | BindingFlags.Static);
        method.Should().NotBeNull();

        var plain = new WKBWriter();
        return (WKBWriter)method!.Invoke(null, new object[] { geometry, plain })!;
    }

    private static (bool HasZ, bool HasM) InvokeDetectZm(Geometry geometry)
    {
        var method = typeof(StreamingFileImportService).GetMethod(
            "DetectZm",
            BindingFlags.NonPublic | BindingFlags.Static);
        method.Should().NotBeNull();

        return ((bool, bool))method!.Invoke(null, new object[] { geometry })!;
    }

    private static Geometry RoundTrip(Geometry geometry)
    {
        var writer = InvokeSelectWkbWriter(geometry);
        var wkb = writer.Write(geometry);
        return new WKBReader().Read(wkb);
    }

    [Fact]
    public void CreateWkb_2DPoint_StaysTwoDimensional()
    {
        var point = Factory.CreatePoint(new Coordinate(1.0, 2.0));

        InvokeDetectZm(point).Should().Be((false, false));

        var result = RoundTrip(point);
        var coord = result.Coordinate;
        double.IsNaN(coord.Z).Should().BeTrue("a 2-D point must not gain a Z ordinate");
        double.IsNaN(coord.M).Should().BeTrue("a 2-D point must not gain an M ordinate");
    }

    [Fact]
    public void CreateWkb_XyzPoint_RoundTripsZ()
    {
        var point = Factory.CreatePoint(new CoordinateZ(1.0, 2.0, 30.0));

        InvokeDetectZm(point).Should().Be((true, false));

        var result = (Point)RoundTrip(point);
        result.Coordinate.Z.Should().Be(30.0, "Z must survive import (#1981)");
        double.IsNaN(result.Coordinate.M).Should().BeTrue();
    }

    [Fact]
    public void CreateWkb_XymPoint_RoundTripsM_WithoutGainingZ()
    {
        var point = Factory.CreatePoint(new CoordinateM(1.0, 2.0, 42.0));

        InvokeDetectZm(point).Should().Be((false, true));

        var result = (Point)RoundTrip(point);
        result.Coordinate.M.Should().Be(42.0, "M must survive import (#1981)");
        double.IsNaN(result.Coordinate.Z).Should().BeTrue("an XYM point must not gain a Z ordinate");
    }

    [Fact]
    public void CreateWkb_XyzmPoint_RoundTripsBothZAndM()
    {
        var point = Factory.CreatePoint(new CoordinateZM(1.0, 2.0, 30.0, 42.0));

        InvokeDetectZm(point).Should().Be((true, true));

        var result = (Point)RoundTrip(point);
        result.Coordinate.Z.Should().Be(30.0);
        result.Coordinate.M.Should().Be(42.0);
    }

    [Fact]
    public void CreateWkb_XyzmLineString_RoundTripsBothZAndM()
    {
        var line = Factory.CreateLineString(new Coordinate[]
        {
            new CoordinateZM(0.0, 0.0, 10.0, 100.0),
            new CoordinateZM(1.0, 1.0, 11.0, 101.0),
            new CoordinateZM(2.0, 2.0, 12.0, 102.0),
        });

        InvokeDetectZm(line).Should().Be((true, true));

        var result = (LineString)RoundTrip(line);
        result.GetCoordinateN(0).Z.Should().Be(10.0);
        result.GetCoordinateN(0).M.Should().Be(100.0);
        result.GetCoordinateN(2).Z.Should().Be(12.0);
        result.GetCoordinateN(2).M.Should().Be(102.0);
    }

    [Fact]
    public void CreateWkb_XymPolygon_RoundTripsM()
    {
        var shell = Factory.CreateLinearRing(new Coordinate[]
        {
            new CoordinateM(0.0, 0.0, 5.0),
            new CoordinateM(0.0, 1.0, 6.0),
            new CoordinateM(1.0, 1.0, 7.0),
            new CoordinateM(1.0, 0.0, 8.0),
            new CoordinateM(0.0, 0.0, 5.0),
        });
        var polygon = Factory.CreatePolygon(shell);

        InvokeDetectZm(polygon).Should().Be((false, true));

        var result = (Polygon)RoundTrip(polygon);
        result.ExteriorRing.GetCoordinateN(0).M.Should().Be(5.0);
        result.ExteriorRing.GetCoordinateN(2).M.Should().Be(7.0);
        double.IsNaN(result.ExteriorRing.GetCoordinateN(0).Z).Should().BeTrue();
    }

    private static string? InvokeConvertEsri(string esriJson)
    {
        using var doc = JsonDocument.Parse(esriJson);
        var method = typeof(GeoservicesImportService).GetMethod(
            "ConvertEsriGeometryToGeoJson",
            BindingFlags.NonPublic | BindingFlags.Static);
        method.Should().NotBeNull();

        return (string?)method!.Invoke(null, new object[] { doc.RootElement.Clone() });
    }

    [Fact]
    public void ConvertEsriGeometryToGeoJson_PolygonZ_CarriesZ()
    {
        // Esri polygon ring with Z (single CW shell). Z values are the third ordinate.
        const string esriJson = """
        {
          "hasZ": true,
          "rings": [
            [ [0,0,10], [0,1,11], [1,1,12], [1,0,13], [0,0,10] ]
          ]
        }
        """;

        var geoJson = InvokeConvertEsri(esriJson);
        geoJson.Should().NotBeNull();

        // The polygon Z must survive: parse and confirm every coordinate has 3 ordinates.
        using var doc = JsonDocument.Parse(geoJson!);
        doc.RootElement.GetProperty("type").GetString().Should().Be("MultiPolygon");

        var firstRing = doc.RootElement
            .GetProperty("coordinates")[0][0];
        foreach (var coord in firstRing.EnumerateArray())
        {
            coord.GetArrayLength().Should().Be(3, "Esri polygon Z must round-trip (#1981)");
        }

        // Spot-check a known Z value is present somewhere in the ring.
        var anyZ = firstRing.EnumerateArray()
            .Any(c => c.GetArrayLength() == 3 && c[2].GetDouble() == 12.0);
        anyZ.Should().BeTrue();
    }

    [Fact]
    public void ConvertEsriGeometryToGeoJson_PolygonM_DoesNotMistakeMeasureForZ()
    {
        // hasZ:false, hasM:true — the third ordinate is M, which GeoJSON/ST_GeomFromGeoJSON
        // cannot represent. It must NOT be promoted to GeoJSON Z.
        const string esriJson = """
        {
          "hasZ": false,
          "hasM": true,
          "rings": [
            [ [0,0,500], [0,1,501], [1,1,502], [1,0,503], [0,0,500] ]
          ]
        }
        """;

        var geoJson = InvokeConvertEsri(esriJson);
        geoJson.Should().NotBeNull();

        using var doc = JsonDocument.Parse(geoJson!);
        var firstRing = doc.RootElement.GetProperty("coordinates")[0][0];
        foreach (var coord in firstRing.EnumerateArray())
        {
            coord.GetArrayLength().Should().Be(2, "an M-only ordinate must not be written as GeoJSON Z");
        }
    }

    [Fact]
    public void ConvertEsriGeometryToGeoJson_PolylineZ_CarriesZ()
    {
        const string esriJson = """
        {
          "hasZ": true,
          "paths": [ [ [0,0,10], [1,1,11], [2,2,12] ] ]
        }
        """;

        var geoJson = InvokeConvertEsri(esriJson);
        geoJson.Should().NotBeNull();

        using var doc = JsonDocument.Parse(geoJson!);
        doc.RootElement.GetProperty("type").GetString().Should().Be("MultiLineString");
        var firstLine = doc.RootElement.GetProperty("coordinates")[0];
        firstLine[0].GetArrayLength().Should().Be(3);
        firstLine[2][2].GetDouble().Should().Be(12.0);
    }
}
