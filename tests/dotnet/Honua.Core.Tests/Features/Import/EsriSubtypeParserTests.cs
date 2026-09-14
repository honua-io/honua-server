// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Metadata.Domain.V2;
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

    [Theory]
    [InlineData("HazardType", "Flooding")]
    [InlineData("fullclose", "Yes")]
    [InlineData("status", "Closed")]
    public void Parse_FeatureTypes_PreservesStringCodesAndTemplateDefaults(string field, string code)
    {
        var layer = ParseLayer($$"""
            {
              "typeIdField": "{{field}}",
              "types": [{
                "id": "{{code}}", "name": "Template label",
                "domains": { "priority": { "type": "inherited" } },
                "templates": [{ "prototype": { "attributes": {
                  "{{field}}": "{{code}}", "priority": null, "description": "Default text"
                } } }]
              }]
            }
            """);

        var parsed = EsriSubtypeParser.Parse(layer).Subtypes!;
        parsed.Should().NotBeNull();
        parsed.SubtypeField.Should().Be(field);
        parsed.DefaultSubtypeCode.Should().BeNull("feature types do not imply a layer default");
        var type = parsed.Subtypes.Single();
        type.Code.GetString().Should().Be(code);
        type.Name.Should().Be("Template label");
        type.FieldOverrides[field].DefaultValue!.Value.GetString().Should().Be(code);
        type.FieldOverrides["description"].DefaultValue!.Value.GetString().Should().Be("Default text");
        type.FieldOverrides["priority"].DefaultValue!.Value.ValueKind.Should().Be(JsonValueKind.Null);
        type.FieldOverrides["priority"].Domain.Should().BeNull("inherited means keep the field domain");
    }

    [Fact]
    public void Parse_FeatureTypesWithNumericCodeAndDomain_PreservesBoth()
    {
        var layer = ParseLayer("""
            { "typeIdField": "kind", "types": [{ "id": 7, "name": "Seven",
              "domains": { "status": { "type": "codedValue", "name": "Status",
                "codedValues": [{ "code": "new", "name": "New" }] } }
            }] }
            """);
        var type = EsriSubtypeParser.Parse(layer).Subtypes!.Subtypes.Single();
        type.Code.GetInt32().Should().Be(7);
        type.FieldOverrides["status"].Domain!.CodedValues.Single().Code.GetString().Should().Be("new");
    }

    [Fact]
    public void Parse_MultipleFeatureTemplates_RejectsAmbiguousReduction()
    {
        var layer = ParseLayer("""
            { "typeIdField": "kind", "types": [{ "id": 1, "name": "One",
              "templates": [{ "name": "A" }, { "name": "B" }] }] }
            """);
        var act = () => EsriSubtypeParser.Parse(layer);
        act.Should().Throw<InvalidOperationException>().WithMessage("*multiple editing templates*");
    }

    [Fact]
    public void Parse_ExplicitNullDefault_SurvivesCanonicalJsonRoundTripWithoutChangingLegacyNull()
    {
        var layer = ParseLayer("""
            { "typeIdField": "kind", "types": [{ "id": 1, "name": "One",
              "templates": [{ "prototype": { "attributes": { "description": null } } }] }] }
            """);
        var original = EsriSubtypeParser.Parse(layer).Subtypes!.Subtypes.Single().FieldOverrides["description"];
        var json = JsonSerializer.Serialize(original, MetadataV2JsonContext.Default.MetadataV2SubtypeFieldOverride);
        var restored = JsonSerializer.Deserialize(json, MetadataV2JsonContext.Default.MetadataV2SubtypeFieldOverride)!;
        restored.DefaultValue.Should().NotBeNull();
        restored.DefaultValue!.Value.ValueKind.Should().Be(JsonValueKind.Null);

        var legacy = JsonSerializer.Deserialize("""{"defaultValue":null,"domain":null}""",
            MetadataV2JsonContext.Default.MetadataV2SubtypeFieldOverride)!;
        legacy.DefaultValue.Should().BeNull();
    }

    [Fact]
    public void Parse_ExplicitDomainClearing_RejectsUnsupportedReduction()
    {
        var layer = ParseLayer("""
            { "typeIdField": "kind", "types": [{ "id": 1, "name": "One",
              "domains": { "status": null } }] }
            """);
        var act = () => EsriSubtypeParser.Parse(layer);
        act.Should().Throw<InvalidOperationException>().WithMessage("*clears a domain*");
    }

    private static JsonElement ParseLayer(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
