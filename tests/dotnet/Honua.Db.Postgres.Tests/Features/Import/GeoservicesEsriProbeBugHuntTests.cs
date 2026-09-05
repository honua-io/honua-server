// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Reflection;
using System.Text.Json;
using Honua.Core.Features.Migration.Domain;
using Honua.Db.Postgres.Features.Migration;

namespace Honua.Db.Postgres.Tests.Features.Import;

/// <summary>
/// Regression probes for ArcGIS geometry and field fidelity in the migration importer.
/// </summary>
public sealed class GeoservicesEsriProbeBugHuntTests
{
    [Fact]
    public void CurvePolyline_IsNotSilentlyConvertedToNullGeometry()
    {
        var geoJson = ConvertEsriGeometry("""
            {
              "curvePaths": [
                [
                  [0,0], {"c":[[10,0],[5,5],[0,10]]}
                ]
              ]
            }
            """);

        Assert.NotNull(geoJson);
    }

    [Fact]
    public void CurvePolyline_CircularArc_PreservesCurvatureAndEndpoints()
    {
        var geoJson = ConvertEsriGeometry("""
            {"curvePaths": [[[1,0], {"c": [[0,1], [0,0]]}]]}
            """);

        Assert.NotNull(geoJson);
        using var document = JsonDocument.Parse(geoJson);
        var path = document.RootElement.GetProperty("coordinates")[0];
        Assert.True(path.GetArrayLength() > 2);
        Assert.Equal(1, path[0][0].GetDouble());
        Assert.Equal(0, path[0][1].GetDouble());
        Assert.Equal(0, path[path.GetArrayLength() - 1][0].GetDouble());
        Assert.Equal(1, path[path.GetArrayLength() - 1][1].GetDouble());
        foreach (var vertex in path.EnumerateArray())
        {
            var x = vertex[0].GetDouble();
            var y = vertex[1].GetDouble();
            Assert.Equal(1, (x * x) + (y * y), precision: 6);
        }
    }

    [Fact]
    public void CurvePolyline_CubicBezier_SamplesCurveAndPreservesEndpointOrdinates()
    {
        var geoJson = ConvertEsriGeometry("""
            {"hasZ": true, "hasM": true,
             "curvePaths": [[[0,0,30,40], {"b": [[10,0,31,41], [3,5], [7,5]]}]]}
            """);

        Assert.NotNull(geoJson);
        using var document = JsonDocument.Parse(geoJson);
        var path = document.RootElement.GetProperty("coordinates")[0];
        Assert.Equal(33, path.GetArrayLength());
        Assert.Equal(5, path[16][0].GetDouble(), precision: 6);
        Assert.Equal(3.75, path[16][1].GetDouble(), precision: 6);
        Assert.Equal(30, path[0][2].GetDouble());
        Assert.Equal(40, path[0][3].GetDouble());
        Assert.Equal(31, path[32][2].GetDouble());
        Assert.Equal(41, path[32][3].GetDouble());
    }

    [Fact]
    public void EmptyPolyline_IsRetainedAsAnEmptyGeometry()
    {
        var geoJson = ConvertEsriGeometry("{\"paths\":[]}");

        Assert.NotNull(geoJson);
        using var document = JsonDocument.Parse(geoJson!);
        Assert.Equal("MultiLineString", document.RootElement.GetProperty("type").GetString());
        Assert.Empty(document.RootElement.GetProperty("coordinates").EnumerateArray());
    }

    [Fact]
    public void EmptyPolyline_IsInsertedAsEmptyWktInsteadOfNull()
    {
        var wkt = ConvertWkt("{\"paths\":[]}", hasZ: false, hasM: false);

        Assert.Equal("MULTILINESTRING EMPTY", wkt);
    }

    [Fact]
    public void MOnlyPoint_DoesNotLoseTheSourceMeasure()
    {
        var geoJson = ConvertEsriGeometry("""
            { "hasZ": false, "hasM": true, "x": 10, "y": 20, "m": 7 }
            """);

        Assert.NotNull(geoJson);
        using var document = JsonDocument.Parse(geoJson!);
        Assert.Equal(3, document.RootElement.GetProperty("coordinates").GetArrayLength());
        Assert.Equal(7, document.RootElement.GetProperty("coordinates")[2].GetDouble());
    }

    [Fact]
    public void MOnlyPoint_UsesThePostgisMDimension()
    {
        var wkt = ConvertWkt("""
            { "hasZ": false, "hasM": true, "x": 10, "y": 20, "m": 7 }
            """, hasZ: false, hasM: true);

        Assert.Equal("POINT M (10 20 7)", wkt);
    }

    [Fact]
    public void LayerDimensions_AreUsedWhenGeometryOmitsDimensionFlags()
    {
        var wkt = ConvertWkt("""
            { "paths": [[[10, 20, 30, 40], [11, 21, 31, 41]]] }
            """, hasZ: true, hasM: true);

        Assert.StartsWith("MULTILINESTRING ZM ", wkt, StringComparison.Ordinal);
        Assert.Contains("30 40", wkt, StringComparison.Ordinal);
        Assert.Contains("31 41", wkt, StringComparison.Ordinal);
    }

    [Fact]
    public void BlobAttribute_IsDecodedToByteaValueInsteadOfString()
    {
        using var document = JsonDocument.Parse("\"AQID\"");
        var value = InvokePrivate(
            "ConvertJsonValue",
            document.RootElement,
            "esriFieldTypeBlob");

        Assert.IsType<byte[]>(value);
        Assert.Equal(new byte[] { 1, 2, 3 }, (byte[])value!);
    }

    [Fact]
    public void RasterField_IsNotMappedToText()
    {
        var value = InvokePrivate("MapEsriTypeToPgType", "esriFieldTypeRaster", null);

        Assert.NotEqual("TEXT", value);
    }

    [Fact]
    public void ZGeometry_UsesADimensionalPostgisColumn()
    {
        using var geometry = JsonDocument.Parse("""
            {
              "hasZ": true,
              "x": 10,
              "y": 20,
              "z": 30
            }
            """);
        var geoJson = ConvertEsriGeometry(geometry.RootElement.GetRawText());
        Assert.NotNull(geoJson);

        var layerInfo = new GeoservicesLayerInfo
        {
            Id = 1,
            Name = "Z points",
            GeometryType = "esriGeometryPoint",
            HasZ = true,
            Fields = []
        };
        var sql = (string)InvokePrivate(
            "BuildCreateTableSql",
            "honua_data",
            "z_points",
            layerInfo,
            4326)!;

        Assert.Contains("geometry(POINTZ, 4326)", sql, StringComparison.Ordinal);
    }

    private static string? ConvertEsriGeometry(string json)
    {
        using var document = JsonDocument.Parse(json);
        return (string?)InvokePrivate("ConvertEsriGeometryToGeoJson", document.RootElement);
    }

    private static string? ConvertWkt(string json, bool hasZ, bool hasM)
    {
        using var document = JsonDocument.Parse(json);
        return (string?)InvokePrivate(
            "ConvertEsriGeometryToWkt",
            document.RootElement,
            hasZ,
            hasM);
    }

    private static object? InvokePrivate(string methodName, params object?[] arguments)
    {
        var method = typeof(GeoservicesImportService).GetMethod(
            methodName,
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return method!.Invoke(null, arguments);
    }
}
