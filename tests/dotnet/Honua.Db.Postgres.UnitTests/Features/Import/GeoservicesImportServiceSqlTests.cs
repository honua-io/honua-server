// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Reflection;
using System.Text.Json;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.FileImport.Domain;
using Honua.Core.Features.Migration.Services;
using Honua.Core.Features.FileImport.Services;
using Honua.Db.Postgres.Features.Migration;
using Honua.Db.Postgres.Features.FileImport;

namespace Honua.Db.Postgres.Tests.Features.Import;

[global::Xunit.Trait("Tier", "Fast")]
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
    public void BuildCreateTableSql_RenamesReservedAndCollidingAttributeNames()
    {
        var layerInfo = new GeoservicesLayerInfo
        {
            Id = 2,
            Name = "Reserved",
            GeometryType = "esriGeometryPoint",
            Fields =
            [
                new GeoservicesFieldInfo { Name = "FID", Type = "esriFieldTypeOID" },
                new GeoservicesFieldInfo { Name = "OBJECTID", Type = "esriFieldTypeInteger" },
                new GeoservicesFieldInfo { Name = "GEOM", Type = "esriFieldTypeString", Length = 20 },
                new GeoservicesFieldInfo { Name = "A-B", Type = "esriFieldTypeString", Length = 10 },
                new GeoservicesFieldInfo { Name = "a_b", Type = "esriFieldTypeString", Length = 10 }
            ]
        };

        var method = typeof(GeoservicesImportService).GetMethod(
            "BuildCreateTableSql",
            BindingFlags.NonPublic | BindingFlags.Static);
        var sql = (string)method!.Invoke(null, new object[] { "honua_data", "reserved_names", layerInfo, 4326 })!;

        sql.Should().Contain("objectid BIGSERIAL PRIMARY KEY");
        sql.Should().Contain("\"objectid_src\" INTEGER");
        sql.Should().Contain("\"geom_src\" VARCHAR(20)");
        sql.Should().Contain("geom geometry(POINT, 4326)");
        sql.Should().NotContain("\"objectid\" INTEGER");
        sql.Should().NotContain("\"geom\" ");
        var aB = sql.IndexOf("\"a_b\"", StringComparison.Ordinal);
        var aBSrc = sql.IndexOf("\"a_b_src\"", StringComparison.Ordinal);
        aB.Should().BeGreaterThan(0);
        aBSrc.Should().BeGreaterThan(aB);
    }

    [Fact]
    public void Map_NonSpatialGeomAttribute_KeepsGeomColumnName()
    {
        var map = GeoservicesImportColumns.Map(new GeoservicesLayerInfo
        {
            Id = 3,
            Name = "Table",
            Fields =
            [
                new GeoservicesFieldInfo { Name = "OBJECTID", Type = "esriFieldTypeOID" },
                new GeoservicesFieldInfo { Name = "GEOM", Type = "esriFieldTypeString" }
            ]
        });

        map.Spatial.Should().BeFalse();
        map.Columns.Should().ContainSingle().Which.ColumnName.Should().Be("geom");
        map.Columns[0].Renamed.Should().BeFalse();
    }

    [Fact]
    public void MapReference_RemapsSubtypeAndDomainNames()
    {
        var layer = new GeoservicesLayerInfo
        {
            Id = 4,
            Name = "Domains",
            GeometryType = "esriGeometryPoint",
            GlobalIdField = "OBJECTID",
            Fields =
            [
                new GeoservicesFieldInfo { Name = "FID", Type = "esriFieldTypeOID" },
                new GeoservicesFieldInfo
                {
                    Name = "OBJECTID",
                    Type = "esriFieldTypeInteger",
                    Domain = new Honua.Core.Features.Metadata.Domain.V2.MetadataV2FieldDomain { Name = "codes" }
                }
            ]
        };
        var map = GeoservicesImportColumns.Map(layer);
        var domains = GeoservicesImportColumns.BuildFieldDomainMap(layer);

        map.MapReference("OBJECTID").Should().Be("objectid_src");
        map.MapReference("FID").Should().Be("fid");
        domains.Should().ContainKey("objectid_src");
        domains.Should().NotContainKey("OBJECTID");
    }

    [Fact]
    public void RemapAttributeRules_RewritesCollisionReferencesAndKeepsQuotedText()
    {
        var map = GeoservicesImportColumns.Map(new GeoservicesLayerInfo
        {
            Id = 5,
            Name = "Rules",
            GeometryType = "esriGeometryPoint",
            Fields =
            [
                new GeoservicesFieldInfo { Name = "FID", Type = "esriFieldTypeOID" },
                new GeoservicesFieldInfo { Name = "OBJECTID", Type = "esriFieldTypeInteger" },
                new GeoservicesFieldInfo { Name = "Name", Type = "esriFieldTypeString" },
                new GeoservicesFieldInfo { Name = "Field Name", Type = "esriFieldTypeString" }
            ]
        });

        var rules = GeoservicesImportColumns.RemapAttributeRules(
        [
            new MetadataV2AttributeRule
            {
                Name = "copy",
                FieldName = "OBJECTID",
                ScriptExpression = "return $feature.OBJECTID + $feature[\"Name\"] + $feature[\"Field Name\"] + 'keep $feature.OBJECTID';"
            },
            new MetadataV2AttributeRule
            {
                Name = "check",
                Type = MetadataV2AttributeRuleType.Constraint,
                ScriptExpression = "$feature.FID > 0"
            }
        ], map);

        rules.Should().HaveCount(2);
        rules![0].FieldName.Should().Be("objectid_src");
        rules[0].ScriptExpression.Should().Be(
            "return $feature.objectid_src + $feature[\"Name\"] + $feature.field_name + 'keep $feature.OBJECTID';");
        rules[1].FieldName.Should().BeNull();
        rules[1].ScriptExpression.Should().Be("$feature.FID > 0");
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
