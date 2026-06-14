// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Migration.Services;

namespace Honua.Core.Tests.Features.Import;

/// <summary>
/// Unit coverage for the shared Esri attribute-rule parser (honua-server#1271). The
/// parser is the single source of truth for attribute-rule capture: it maps Esri
/// calculation/constraint/validation rules into the canonical model, normalizes
/// triggering events, drops un-appliable calculation rules (no target field), and bounds
/// the rule set by the cap.
/// </summary>
public sealed class EsriAttributeRuleParserTests
{
    [Fact]
    public void Parse_CalculationAndConstraintRules_ProducesCanonicalRules()
    {
        var layer = ParseLayer("""
            {
              "attributeRules": [
                {
                  "name": "SetStatus",
                  "type": "calculation",
                  "fieldName": "status",
                  "scriptExpression": "return 'active';",
                  "triggeringEvents": ["Insert", "Update"],
                  "isEnabled": true
                },
                {
                  "name": "PositiveQty",
                  "type": "constraint",
                  "scriptExpression": "$feature.qty > 0",
                  "errorMessage": "Quantity must be positive",
                  "triggeringEvents": ["Insert"]
                }
              ]
            }
            """);

        var result = EsriAttributeRuleParser.Parse(layer);

        result.Truncated.Should().BeFalse();
        result.Rules.Should().NotBeNull();
        result.Rules!.Should().HaveCount(2);

        // Deterministically ordered by name.
        var calc = result.Rules.Single(r => r.Name == "SetStatus");
        calc.Type.Should().Be(MetadataV2AttributeRuleType.Calculation);
        calc.FieldName.Should().Be("status");
        calc.TriggeringEvents.Should().ContainInOrder("insert", "update");

        var constraint = result.Rules.Single(r => r.Name == "PositiveQty");
        constraint.Type.Should().Be(MetadataV2AttributeRuleType.Constraint);
        constraint.ErrorMessage.Should().Be("Quantity must be positive");
        constraint.TriggeringEvents.Should().ContainInOrder("insert");
    }

    [Fact]
    public void Parse_CalculationWithoutFieldName_IsDropped()
    {
        var layer = ParseLayer("""
            {
              "attributeRules": [
                { "name": "NoTarget", "type": "calculation", "scriptExpression": "return 1;" }
              ]
            }
            """);

        var result = EsriAttributeRuleParser.Parse(layer);

        // A calculation rule with no target field can never be applied; the parser drops it.
        result.Rules.Should().BeNull();
    }

    [Fact]
    public void Parse_NoAttributeRules_ReturnsNone()
    {
        var layer = ParseLayer("""{ "name": "Layer" }""");

        var result = EsriAttributeRuleParser.Parse(layer);

        result.Should().Be(EsriAttributeRuleParseResult.None);
    }

    [Fact]
    public void Parse_OverCap_ReportsTruncatedAndOmits()
    {
        var builder = new StringBuilder("{ \"attributeRules\": [");
        for (var i = 0; i <= EsriAttributeRuleParser.AttributeRuleCap; i++)
        {
            if (i > 0)
            {
                builder.Append(',');
            }

            builder.Append("{ \"name\": \"r")
                .Append(i.ToString(System.Globalization.CultureInfo.InvariantCulture))
                .Append("\", \"type\": \"constraint\", \"scriptExpression\": \"$feature.x > 0\" }");
        }

        builder.Append("] }");
        var layer = ParseLayer(builder.ToString());

        var result = EsriAttributeRuleParser.Parse(layer);

        result.Truncated.Should().BeTrue();
        result.Rules.Should().BeNull();
    }

    private static JsonElement ParseLayer(string json)
        => JsonDocument.Parse(json).RootElement.Clone();
}
