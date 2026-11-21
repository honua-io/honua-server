// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Reflection;
using FluentAssertions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Io.Export;
using Honua.Io.Export.Writers;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Export;

public sealed class ExactTypesExportTests
{
    [UnitTheory]
    [InlineData(MetadataV2FieldType.Decimal, nameof(ExportFieldType.Double))]
    [InlineData(MetadataV2FieldType.TimestampOffset, nameof(ExportFieldType.DateTime))]
    public void MapFieldType_ExactTypes_UseDoubleAndDateTime(MetadataV2FieldType type, string expected)
    {
        Invoke(typeof(ExportEndpoints), "MapFieldType", type)!.ToString().Should().Be(expected);
    }

    [UnitTest]
    public void GeoPackage_DecimalTextInDoubleColumn_StoresInvariantDouble()
    {
        Invoke(typeof(GeoPackageExportWriter), "NormalizeSqliteValue", "-12.50", ExportFieldType.Double)
            .Should().Be(-12.5d);
        Invoke(typeof(GeoPackageExportWriter), "NormalizeSqliteValue", 3.25d, ExportFieldType.Double)
            .Should().Be(3.25d);
    }

    [UnitTest]
    public void Shapefile_DecimalTextInDoubleColumn_StoresInvariantDouble()
    {
        Invoke(typeof(ShapefileExportWriter), "NormalizeDbfValue", "1234.5", ExportFieldType.Double)
            .Should().Be(1234.5d);
    }

    private static object? Invoke(Type type, string method, params object?[] arguments)
        => type.GetMethod(method, BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, arguments);
}
