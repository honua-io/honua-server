// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Reflection;
using System.Text.Json;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Import.Services;
using Honua.Postgres.Features.Import;

namespace Honua.Postgres.Tests.Features.Import;

public sealed class GeoservicesImportServiceSqlTests
{
    [Fact]
    public void BuildCreateTableSql_DoesNotLeaveTrailingComma()
    {
        var layerInfo = new GeoservicesLayerInfo
        {
            Id = 1,
            Name = "Test Layer",
            GeometryType = "esriGeometryPoint",
            Fields =
            [
                new GeoservicesFieldInfo
                {
                    Name = "OBJECTID",
                    Type = "esriFieldTypeOID",
                    Nullable = false
                },
                new GeoservicesFieldInfo
                {
                    Name = "Name",
                    Type = "esriFieldTypeString",
                    Length = 50,
                    Nullable = false
                }
            ]
        };

        var method = typeof(GeoservicesImportService).GetMethod(
            "BuildCreateTableSql",
            BindingFlags.NonPublic | BindingFlags.Static);

        method.Should().NotBeNull();

        var sql = (string)method!.Invoke(null, new object[] { "honua_data", "test_table", layerInfo, 4326 })!;

        var lines = sql.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        lines[^1].Trim().Should().Be(");");
        lines[^2].TrimEnd('\r').TrimEnd().Should().NotEndWith(",");

        sql.Should().Contain("objectid BIGSERIAL PRIMARY KEY");
        sql.Should().Contain("CREATE TABLE \"honua_data\".\"test_table\"");
        sql.Should().Contain("geom geometry(POINT, 4326)");
        sql.Should().Contain("\"name\"");
    }

    [Fact]
    public void BuildCreateTableSql_AllowsNullsForSourceFieldsWithStaleEsriNullableMetadata()
    {
        var layerInfo = new GeoservicesLayerInfo
        {
            Id = 5,
            Name = "C2 Military Operations Area",
            GeometryType = "esriGeometryPolygon",
            Fields =
            [
                new GeoservicesFieldInfo
                {
                    Name = "objectid",
                    Type = "esriFieldTypeOID",
                    Nullable = false
                },
                new GeoservicesFieldInfo
                {
                    Name = "Shape__Area",
                    Type = "esriFieldTypeDouble",
                    Nullable = false
                }
            ]
        };

        var method = typeof(GeoservicesImportService).GetMethod(
            "BuildCreateTableSql",
            BindingFlags.NonPublic | BindingFlags.Static);

        method.Should().NotBeNull();

        var sql = (string)method!.Invoke(null, new object[] { "honua_data", "military_area", layerInfo, 4326 })!;

        sql.Should().Contain("\"shape__area\" DOUBLE PRECISION");
        sql.Should().NotContain("\"shape__area\" DOUBLE PRECISION NOT NULL");
    }

    [Fact]
    public void BuildGeometryInsertExpression_ForPolygon_RepairsInvalidSourceRings()
    {
        var method = typeof(GeoservicesImportService).GetMethod(
            "BuildGeometryInsertExpression",
            BindingFlags.NonPublic | BindingFlags.Static);

        method.Should().NotBeNull();

        var expression = (string)method!.Invoke(null, ["esriGeometryPolygon", 4326])!;

        expression.Should().Contain("ST_MakeValid");
        expression.Should().Contain("ST_CollectionExtract");
        expression.Should().Contain(", 3)");
        expression.Should().StartWith("ST_Multi(");
    }

    [Fact]
    public void ConvertEsriGeometryToGeoJson_ClassifiesPolygonRingsIntoShellsAndHoles()
    {
        using var geometry = JsonDocument.Parse("""
            {
              "rings": [
                [[0,0], [0,10], [10,10], [10,0], [0,0]],
                [[2,2], [8,2], [8,8], [2,8], [2,2]],
                [[20,20], [20,30], [30,30], [30,20], [20,20]]
              ]
            }
            """);

        var method = typeof(GeoservicesImportService).GetMethod(
            "ConvertEsriGeometryToGeoJson",
            BindingFlags.NonPublic | BindingFlags.Static);

        method.Should().NotBeNull();

        var geoJson = (string?)method!.Invoke(null, [geometry.RootElement]);

        geoJson.Should().NotBeNull();
        using var converted = JsonDocument.Parse(geoJson!);
        var root = converted.RootElement;

        root.GetProperty("type").GetString().Should().Be("MultiPolygon");
        var polygons = root.GetProperty("coordinates");
        polygons.GetArrayLength().Should().Be(2);
        polygons[0].GetArrayLength().Should().Be(2, "the first shell should retain its hole");
        polygons[1].GetArrayLength().Should().Be(1, "the disjoint shell should become a second polygon");
    }
}
