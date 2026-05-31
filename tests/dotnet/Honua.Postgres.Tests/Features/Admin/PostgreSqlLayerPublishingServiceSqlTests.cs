// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Reflection;
using System.Text.Json;
using Honua.Core.Features.Admin.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Postgres.Features.Admin;

namespace Honua.Postgres.Tests.Features.Admin;

public sealed class PostgreSqlLayerPublishingServiceSqlTests
{
    [Fact]
    public void BuildAttributesExpression_WithWideTables_ChunksJsonbBuildObjectCalls()
    {
        var columns = Enumerable.Range(1, 51)
            .Select(index => new ColumnInfo
            {
                Name = $"field_{index}",
                DataType = "text"
            })
            .ToArray();

        var method = typeof(PostgreSqlLayerPublishingService).GetMethod(
            "BuildAttributesExpression",
            BindingFlags.NonPublic | BindingFlags.Static);

        method.Should().NotBeNull();

        var expression = (string)method!.Invoke(null, [columns])!;

        expression.Split("jsonb_build_object", StringSplitOptions.None).Length.Should().Be(3);
        expression.Should().Contain(" || ");
        expression.Should().Contain("'field_1', src.\"field_1\"");
        expression.Should().Contain("'field_51', src.\"field_51\"");
    }

    [Fact]
    public void BuildLayerFields_WithFieldDomains_AttachesDomainsAndMetadataV2FieldRoundTripsThem()
    {
        var primaryKey = new ColumnInfo
        {
            Name = "OBJECTID",
            DataType = "integer",
            IsPrimaryKey = true,
            IsNullable = false
        };

        var selectedColumns = new List<ColumnInfo>
        {
            primaryKey,
            new()
            {
                Name = "ZONING",
                DataType = "character varying",
                IsNullable = true,
                MaxLength = 16
            },
            new()
            {
                Name = "ELEVATION",
                DataType = "integer",
                IsNullable = true
            },
            new()
            {
                Name = "geom",
                DataType = "geometry",
                IsNullable = false
            }
        };

        var zoningDomain = new MetadataV2FieldDomain
        {
            Type = "codedValue",
            Name = "ZoningCode",
            CodedValues =
            [
                new() { Code = JsonSerializer.SerializeToElement("R1"), Name = "Residential 1" },
                new() { Code = JsonSerializer.SerializeToElement("C1"), Name = "Commercial 1" }
            ]
        };

        var elevationDomain = new MetadataV2FieldDomain
        {
            Type = "range",
            Name = "ElevationRange",
            Range =
            [
                JsonSerializer.SerializeToElement(0),
                JsonSerializer.SerializeToElement(8848)
            ]
        };

        var fieldDomains = new Dictionary<string, MetadataV2FieldDomain>(StringComparer.OrdinalIgnoreCase)
        {
            ["ZONING"] = zoningDomain,
            // Mixed casing to prove case-insensitive resolution.
            ["elevation"] = elevationDomain
        };

        var buildMethod = typeof(PostgreSqlLayerPublishingService).GetMethod(
            "BuildLayerFields",
            BindingFlags.NonPublic | BindingFlags.Static);
        buildMethod.Should().NotBeNull();

        var inserts = buildMethod!.Invoke(null, [selectedColumns, primaryKey, "geom", fieldDomains])!;
        var insertList = (System.Collections.IEnumerable)inserts;
        var insertsByName = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var entry in insertList)
        {
            var name = (string)entry.GetType().GetProperty("Name")!.GetValue(entry)!;
            insertsByName[name] = entry;
        }

        var domainProperty = insertsByName["ZONING"].GetType().GetProperty("Domain")!;
        ((MetadataV2FieldDomain?)domainProperty.GetValue(insertsByName["ZONING"]))
            .Should().BeSameAs(zoningDomain);
        ((MetadataV2FieldDomain?)domainProperty.GetValue(insertsByName["ELEVATION"]))
            .Should().BeSameAs(elevationDomain, "case-insensitive lookup must find the lowercase key");
        ((MetadataV2FieldDomain?)domainProperty.GetValue(insertsByName["OBJECTID"]))
            .Should().BeNull("fields not in the domain map should remain undomained");

        var mapMethod = typeof(PostgreSqlLayerPublishingService).GetMethod(
            "MapLayerFieldToMetadataV2",
            BindingFlags.NonPublic | BindingFlags.Static);
        mapMethod.Should().NotBeNull();

        var mappedZoning = (MetadataV2Field)mapMethod!.Invoke(null, [insertsByName["ZONING"], "OBJECTID", "geom"])!;
        mappedZoning.Domain.Should().BeSameAs(zoningDomain);

        var mappedElevation = (MetadataV2Field)mapMethod.Invoke(null, [insertsByName["ELEVATION"], "OBJECTID", "geom"])!;
        mappedElevation.Domain.Should().BeSameAs(elevationDomain);

        var mappedObjectId = (MetadataV2Field)mapMethod.Invoke(null, [insertsByName["OBJECTID"], "OBJECTID", "geom"])!;
        mappedObjectId.Domain.Should().BeNull();
    }
}
