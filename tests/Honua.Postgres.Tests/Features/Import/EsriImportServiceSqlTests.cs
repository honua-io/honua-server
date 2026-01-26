// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Reflection;
using Honua.Core.Features.Import.Domain;
using Honua.Postgres.Features.Import;

namespace Honua.Postgres.Tests.Features.Import;

public sealed class EsriImportServiceSqlTests
{
    [Fact]
    public void BuildCreateTableSql_DoesNotLeaveTrailingComma()
    {
        var layerInfo = new EsriLayerInfo
        {
            Id = 1,
            Name = "Test Layer",
            GeometryType = "esriGeometryPoint",
            Fields =
            [
                new EsriFieldInfo
                {
                    Name = "OBJECTID",
                    Type = "esriFieldTypeOID",
                    Nullable = false
                },
                new EsriFieldInfo
                {
                    Name = "Name",
                    Type = "esriFieldTypeString",
                    Length = 50,
                    Nullable = false
                }
            ]
        };

        var method = typeof(EsriImportService).GetMethod(
            "BuildCreateTableSql",
            BindingFlags.NonPublic | BindingFlags.Static);

        method.Should().NotBeNull();

        var sql = (string)method!.Invoke(null, new object[] { "test_table", layerInfo, 4326 })!;

        var lines = sql.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        lines[^1].Trim().Should().Be(");");
        lines[^2].TrimEnd('\r').TrimEnd().Should().NotEndWith(",");

        sql.Should().Contain("geom geometry(POINT, 4326)");
        sql.Should().Contain("\"name\"");
    }
}
