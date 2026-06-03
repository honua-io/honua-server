// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Migration.Services;

namespace Honua.Core.Tests.Features.Import;

/// <summary>
/// Unit coverage for the shared Esri subtype parser (honua-server#1378, #1254). The
/// parser is the single source of truth for subtype capture, the subtype cap, and the
/// per-subtype domain/default overrides (which reuse the shared field-domain parser so
/// the same coded-value cap applies).
/// </summary>
public sealed class EsriSubtypeParserTests
{
    [Fact]
    public void Parse_SubtypeFieldWithSubtypes_ProducesCanonicalSubtypes()
    {
        var layer = ParseLayer("""
            {
              "subtypeField": "buildingtype",
              "defaultSubtypeCode": 1,
              "subtypes": [
                { "code": 2, "name": "Residential", "defaultValues": { "status": "occupied" } },
                { "code": 1, "name": "Commercial" }
              ]
            }
            """);

        var result = EsriSubtypeParser.Parse(layer);

        result.Truncated.Should().BeFalse();
        result.Subtypes.Should().NotBeNull();
        result.Subtypes!.SubtypeField.Should().Be("buildingtype");
        result.Subtypes.DefaultSubtypeCode.Should().NotBeNull();
        result.Subtypes.DefaultSubtypeCode!.Value.GetInt32().Should().Be(1);

        // Deterministically ordered by code.
        result.Subtypes.Subtypes.Select(s => s.Code.GetInt32()).Should().ContainInOrder(1, 2);
        result.Subtypes.Subtypes.Select(s => s.Name).Should().ContainInOrder("Commercial", "Residential");

        var residential = result.Subtypes.Subtypes.Single(s => s.Name == "Residential");
        residential.FieldOverrides.Should().ContainKey("status");
        residential.FieldOverrides["status"].DefaultValue.Should().NotBeNull();
        residential.FieldOverrides["status"].DefaultValue!.Value.GetString().Should().Be("occupied");
    }

    [Fact]
    public void Parse_SubtypeWithPerSubtypeDomain_CapturesDomainOverride()
    {
        var layer = ParseLayer("""
            {
              "subtypeField": "buildingtype",
              "subtypes": [
                {
                  "code": 1,
                  "name": "Residential",
                  "domains": {
                    "status": {
                      "type": "codedValue",
                      "name": "OccupancyDomain",
                      "codedValues": [
                        { "name": "Occupied", "code": "occupied" },
                        { "name": "Vacant", "code": "vacant" }
                      ]
                    }
                  }
                }
              ]
            }
            """);

        var result = EsriSubtypeParser.Parse(layer);

        var residential = result.Subtypes!.Subtypes.Single();
        var statusOverride = residential.FieldOverrides["status"];
        statusOverride.Domain.Should().NotBeNull();
        statusOverride.Domain!.Type.Should().Be(EsriFieldDomainParser.CodedValueDomainType);
        statusOverride.Domain.CodedValues.Select(v => v.Name)
            .Should().BeEquivalentTo(["Occupied", "Vacant"]);
    }

    [Fact]
    public void Parse_NoSubtypeField_ReturnsNone()
    {
        var layer = ParseLayer("""{ "subtypes": [ { "code": 1, "name": "X" } ] }""");

        var result = EsriSubtypeParser.Parse(layer);

        result.Should().Be(EsriSubtypeParseResult.None);
    }

    [Fact]
    public void Parse_SubtypeFieldWithoutSubtypes_ReturnsNone()
    {
        var layer = ParseLayer("""{ "subtypeField": "buildingtype", "subtypes": [] }""");

        var result = EsriSubtypeParser.Parse(layer);

        result.Should().Be(EsriSubtypeParseResult.None);
    }

    [Fact]
    public void Parse_OverCapSubtypeSet_ReportsTruncatedAndOmits()
    {
        var builder = new StringBuilder();
        builder.Append("""{ "subtypeField": "buildingtype", "subtypes": [""");
        for (var i = 0; i <= EsriSubtypeParser.SubtypeCap; i++)
        {
            if (i > 0)
            {
                builder.Append(',');
            }

            builder.Append(System.Globalization.CultureInfo.InvariantCulture, $$"""{ "code": {{i}}, "name": "Subtype {{i}}" }""");
        }
        builder.Append("] }");

        var layer = ParseLayer(builder.ToString());

        var result = EsriSubtypeParser.Parse(layer);

        result.Truncated.Should().BeTrue("an over-cap subtype set is omitted rather than persisted partial");
        result.Subtypes.Should().BeNull();
    }

    private static JsonElement ParseLayer(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
