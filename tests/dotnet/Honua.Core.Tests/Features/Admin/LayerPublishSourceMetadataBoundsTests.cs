// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Admin.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Migration.Services;

namespace Honua.Core.Tests.Features.Admin;

/// <summary>
/// Captured source metadata on a publish request is held to the Esri import capture caps
/// (honua-server#4854 REQ-006): anything the import parsers produce passes, and anything
/// larger or malformed is rejected with a specific reason.
/// </summary>
public sealed class LayerPublishSourceMetadataBoundsTests
{
    [Fact]
    public void TryValidate_WithNoSourceMetadata_Accepts()
    {
        var accepted = LayerPublishSourceMetadataBounds.TryValidate(
            new LayerPublishRequest { Schema = "public", Table = "t", LayerName = "t" },
            out var error);

        accepted.Should().BeTrue();
        error.Should().BeNull();
    }

    [Fact]
    public void Bounds_MatchImportCaptureCaps()
    {
        LayerPublishSourceMetadataBounds.MaxCodedValuesPerDomain.Should().Be(EsriFieldDomainParser.CodedValueDomainCap);
        LayerPublishSourceMetadataBounds.MaxSubtypes.Should().Be(EsriSubtypeParser.SubtypeCap);
        LayerPublishSourceMetadataBounds.MaxAttributeRules.Should().Be(EsriAttributeRuleParser.AttributeRuleCap);
    }

    [Fact]
    public void TryValidate_WithImportParserOutputAtEveryCap_Accepts()
    {
        var domain = EsriFieldDomainParser.ParseDomain(Json($$"""
            { "type": "codedValue", "name": "Status", "codedValues": [{{CodedValues(EsriFieldDomainParser.CodedValueDomainCap)}}] }
            """)).Domain;
        var subtypes = EsriSubtypeParser.Parse(
            "kind",
            Json("1"),
            Json($"[{Subtypes(EsriSubtypeParser.SubtypeCap, overrideCodedValues: EsriFieldDomainParser.CodedValueDomainCap)}]")).Subtypes;
        var rules = EsriAttributeRuleParser.Parse((JsonElement?)Json($"[{Rules(EsriAttributeRuleParser.AttributeRuleCap)}]")).Rules;
        domain.Should().NotBeNull();
        subtypes.Should().NotBeNull();
        rules.Should().HaveCount(EsriAttributeRuleParser.AttributeRuleCap);

        var accepted = LayerPublishSourceMetadataBounds.TryValidate(
            new Dictionary<string, MetadataV2FieldDomain> { ["status"] = domain! },
            subtypes,
            rules,
            out var error);

        error.Should().BeNull();
        accepted.Should().BeTrue();
    }

    [Fact]
    public void TryValidate_WithCodedValueDomainOverCap_Rejects()
    {
        var domain = new MetadataV2FieldDomain
        {
            Name = "Status",
            Type = "codedValue",
            CodedValues = Enumerable.Range(1, EsriFieldDomainParser.CodedValueDomainCap + 1)
                .Select(code => new MetadataV2CodedValue { Code = JsonSerializer.SerializeToElement(code), Name = $"v{code}" })
                .ToArray()
        };

        AssertRejected(
            new Dictionary<string, MetadataV2FieldDomain> { ["status"] = domain }, null, null,
            "fieldDomains coded-value domains are limited to 100 coded values.");
    }

    [Fact]
    public void TryValidate_WithSubtypeOverrideDomainOverCap_Rejects()
    {
        var subtypes = EsriSubtypeParser.Parse("kind", null, Json($"[{Subtypes(1, overrideCodedValues: 1)}]")).Subtypes!;
        var oversized = new MetadataV2FieldDomain
        {
            Type = "codedValue",
            CodedValues = Enumerable.Range(1, EsriFieldDomainParser.CodedValueDomainCap + 1)
                .Select(code => new MetadataV2CodedValue { Code = JsonSerializer.SerializeToElement(code), Name = $"v{code}" })
                .ToArray()
        };
        var withOversizedOverride = subtypes with
        {
            Subtypes = [subtypes.Subtypes[0] with
            {
                FieldOverrides = new Dictionary<string, MetadataV2SubtypeFieldOverride>
                {
                    ["status"] = new() { Domain = oversized }
                }
            }]
        };

        AssertRejected(null, withOversizedOverride, null,
            "subtypes fieldOverrides coded-value domains are limited to 100 coded values.");
    }

    [Fact]
    public void TryValidate_WithSubtypesOverCap_Rejects()
    {
        var subtypes = new MetadataV2Subtypes
        {
            SubtypeField = "kind",
            Subtypes = Enumerable.Range(1, EsriSubtypeParser.SubtypeCap + 1)
                .Select(code => new MetadataV2Subtype { Code = JsonSerializer.SerializeToElement(code), Name = $"s{code}" })
                .ToArray()
        };

        AssertRejected(null, subtypes, null, "subtypes are limited to 100 per layer.");
    }

    [Fact]
    public void TryValidate_WithAttributeRulesOverCap_Rejects()
    {
        var rules = Enumerable.Range(1, EsriAttributeRuleParser.AttributeRuleCap + 1)
            .Select(index => new MetadataV2AttributeRule
            {
                Name = $"rule-{index}",
                Type = MetadataV2AttributeRuleType.Constraint,
                ScriptExpression = "true"
            })
            .ToArray();

        AssertRejected(null, null, rules, "attributeRules are limited to 200 per layer.");
    }

    public static TheoryData<string, string> MalformedMetadata => new()
    {
        { "domain-without-type", "fieldDomains entries must declare a domain type." },
        { "coded-value-without-name", "fieldDomains coded values need a string, number or boolean code and a name." },
        { "coded-value-object-code", "fieldDomains coded values need a string, number or boolean code and a name." },
        { "range-with-one-bound", "fieldDomains range domains must declare exactly [min, max]." },
        { "subtypes-without-field", "subtypes.subtypeField is required." },
        { "subtypes-empty", "subtypes must declare at least one subtype." },
        { "subtype-without-code", "subtypes entries need a string, number or boolean code and a name." },
        { "subtype-default-object", "subtypes.defaultSubtypeCode must be a string, number or boolean." },
        { "rule-duplicate-name", "attributeRules names must be unique." },
        { "rule-without-script", "attributeRules entries need a scriptExpression." },
        { "calculation-without-field", "attributeRules calculation rules need a fieldName." },
        { "rule-undefined-type", "attributeRules type is not supported." },
    };

    [Theory]
    [MemberData(nameof(MalformedMetadata))]
    public void TryValidate_WithMalformedMetadata_RejectsWithSpecificReason(string shape, string expected)
    {
        var codedValue = new MetadataV2CodedValue { Code = JsonSerializer.SerializeToElement(1), Name = "One" };
        var subtype = new MetadataV2Subtype { Code = JsonSerializer.SerializeToElement(1), Name = "One" };
        var rule = new MetadataV2AttributeRule
        {
            Name = "rule",
            Type = MetadataV2AttributeRuleType.Constraint,
            ScriptExpression = "true"
        };
        (Dictionary<string, MetadataV2FieldDomain>? Domains, MetadataV2Subtypes? Subtypes, MetadataV2AttributeRule[]? Rules) input = shape switch
        {
            "domain-without-type" => (Domains(new MetadataV2FieldDomain()), null, null),
            "coded-value-without-name" => (Domains(new MetadataV2FieldDomain { Type = "codedValue", CodedValues = [codedValue with { Name = " " }] }), null, null),
            "coded-value-object-code" => (Domains(new MetadataV2FieldDomain { Type = "codedValue", CodedValues = [codedValue with { Code = Json("{}") }] }), null, null),
            "range-with-one-bound" => (Domains(new MetadataV2FieldDomain { Type = "range", Range = [Json("1")] }), null, null),
            "subtypes-without-field" => (null, new MetadataV2Subtypes { Subtypes = [subtype] }, null),
            "subtypes-empty" => (null, new MetadataV2Subtypes { SubtypeField = "kind" }, null),
            "subtype-without-code" => (null, new MetadataV2Subtypes { SubtypeField = "kind", Subtypes = [subtype with { Code = default }] }, null),
            "subtype-default-object" => (null, new MetadataV2Subtypes { SubtypeField = "kind", DefaultSubtypeCode = Json("[]"), Subtypes = [subtype] }, null),
            "rule-duplicate-name" => (null, null, [rule, rule with { Name = "RULE" }]),
            "rule-without-script" => (null, null, [rule with { ScriptExpression = "" }]),
            "calculation-without-field" => (null, null, [rule with { Type = MetadataV2AttributeRuleType.Calculation }]),
            "rule-undefined-type" => (null, null, [rule with { Type = (MetadataV2AttributeRuleType)99 }]),
            _ => throw new ArgumentOutOfRangeException(nameof(shape), shape, null)
        };

        AssertRejected(input.Domains, input.Subtypes, input.Rules, expected);
    }

    private static void AssertRejected(
        IReadOnlyDictionary<string, MetadataV2FieldDomain>? domains,
        MetadataV2Subtypes? subtypes,
        IReadOnlyList<MetadataV2AttributeRule>? rules,
        string expected)
    {
        var accepted = LayerPublishSourceMetadataBounds.TryValidate(domains, subtypes, rules, out var error);

        accepted.Should().BeFalse();
        error.Should().Be(expected);
    }

    private static Dictionary<string, MetadataV2FieldDomain> Domains(MetadataV2FieldDomain domain)
        => new(StringComparer.OrdinalIgnoreCase) { ["status"] = domain };

    private static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static string CodedValues(int count)
        => string.Join(",", Enumerable.Range(1, count).Select(code => $$"""{ "code": {{code}}, "name": "v{{code}}" }"""));

    private static string Subtypes(int count, int overrideCodedValues)
        => string.Join(",", Enumerable.Range(1, count).Select(code =>
            $$"""{ "code": {{code}}, "name": "s{{code}}", "domains": { "status": { "type": "codedValue", "codedValues": [{{CodedValues(overrideCodedValues)}}] } } }"""));

    private static string Rules(int count)
        => string.Join(",", Enumerable.Range(1, count).Select(index =>
            $$"""{ "name": "rule-{{index}}", "type": "constraint", "scriptExpression": "true", "triggeringEvents": ["Insert"] }"""));
}
