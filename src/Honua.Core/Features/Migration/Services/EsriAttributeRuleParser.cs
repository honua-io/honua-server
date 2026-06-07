// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Metadata.Domain.V2;

namespace Honua.Core.Features.Migration.Services;

/// <summary>
/// Shared, deterministic parser that converts an Esri layer's <c>attributeRules</c> JSON
/// into the canonical <see cref="MetadataV2AttributeRule"/> list. Mirrors
/// <see cref="EsriSubtypeParser"/>: it is the single source of truth for attribute-rule
/// capture so the migration inventory artifact and the import/publish projection agree.
/// <para>
/// Attribute rules are imported for fidelity regardless of whether their Arcade
/// expression is one Honua can evaluate on the edit path — unsupported expressions are
/// routed out of scope at edit time, not dropped at import. The number of captured rules
/// is bounded by <see cref="AttributeRuleCap"/> for determinism.
/// </para>
/// </summary>
public static class EsriAttributeRuleParser
{
    /// <summary>
    /// Maximum number of attribute rules captured for a single layer. Layers exceeding
    /// this cap are reported as truncated and not persisted, keeping the artifact bounded.
    /// </summary>
    public const int AttributeRuleCap = 200;

    /// <summary>
    /// Parses the Esri attribute-rule metadata on a layer element into the canonical
    /// <see cref="MetadataV2AttributeRule"/> list.
    /// </summary>
    /// <param name="layerElement">The Esri layer element carrying <c>attributeRules</c>.</param>
    /// <returns>The parse result describing the captured rules and whether they were truncated.</returns>
    public static EsriAttributeRuleParseResult Parse(JsonElement layerElement)
    {
        if (layerElement.ValueKind != JsonValueKind.Object)
        {
            return EsriAttributeRuleParseResult.None;
        }

        var rulesElement = layerElement.TryGetProperty("attributeRules", out var rules)
            ? rules
            : (JsonElement?)null;
        return Parse(rulesElement);
    }

    /// <summary>
    /// Parses the Esri <c>attributeRules</c> array element into the canonical model.
    /// </summary>
    /// <param name="attributeRulesElementOrNull">The Esri <c>attributeRules</c> array element, or <c>null</c>.</param>
    /// <returns>The parse result; <see cref="EsriAttributeRuleParseResult.None"/> when there are no usable rules.</returns>
    public static EsriAttributeRuleParseResult Parse(JsonElement? attributeRulesElementOrNull)
    {
        if (attributeRulesElementOrNull is not { ValueKind: JsonValueKind.Array } rulesElement ||
            rulesElement.GetArrayLength() == 0)
        {
            return EsriAttributeRuleParseResult.None;
        }

        if (rulesElement.GetArrayLength() > AttributeRuleCap)
        {
            return EsriAttributeRuleParseResult.OverCap;
        }

        var parsed = new List<MetadataV2AttributeRule>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in rulesElement.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var name = GetString(entry, "name");
            if (string.IsNullOrWhiteSpace(name) || !seen.Add(name!))
            {
                continue;
            }

            if (!TryMapType(GetString(entry, "type"), out var type))
            {
                continue;
            }

            var script = GetString(entry, "scriptExpression");
            if (string.IsNullOrWhiteSpace(script))
            {
                continue;
            }

            var fieldName = GetString(entry, "fieldName");
            if (type == MetadataV2AttributeRuleType.Calculation && string.IsNullOrWhiteSpace(fieldName))
            {
                // A calculation rule with no target field can never be applied; skip it
                // rather than persist an un-appliable rule.
                continue;
            }

            parsed.Add(new MetadataV2AttributeRule
            {
                Name = name!,
                Type = type,
                FieldName = fieldName,
                ScriptExpression = script!,
                TriggeringEvents = ParseTriggeringEvents(entry),
                ErrorMessage = GetString(entry, "errorMessage"),
                IsEnabled = GetBool(entry, "isEnabled") ?? true
            });
        }

        if (parsed.Count == 0)
        {
            return EsriAttributeRuleParseResult.None;
        }

        var ordered = parsed
            .OrderBy(static rule => rule.Name, StringComparer.Ordinal)
            .ToArray();

        return new EsriAttributeRuleParseResult(ordered, Truncated: false);
    }

    private static IReadOnlyList<string> ParseTriggeringEvents(JsonElement ruleElement)
    {
        if (!ruleElement.TryGetProperty("triggeringEvents", out var events) ||
            events.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<string>();
        }

        var result = new List<string>();
        foreach (var item in events.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var value = item.GetString();
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            // Esri uses 'Insert'/'Update'/'Delete'; normalize to lowercase canonical tokens.
            result.Add(value.Trim().ToLowerInvariant());
        }

        return result.Count == 0 ? Array.Empty<string>() : result;
    }

    private static bool TryMapType(string? esriType, out MetadataV2AttributeRuleType type)
    {
        switch (esriType?.Trim().ToLowerInvariant())
        {
            case "calculation":
            case "esriarcalculation":
                type = MetadataV2AttributeRuleType.Calculation;
                return true;
            case "constraint":
            case "esriarconstraint":
                type = MetadataV2AttributeRuleType.Constraint;
                return true;
            case "validation":
            case "esriarvalidation":
                type = MetadataV2AttributeRuleType.Validation;
                return true;
            default:
                type = MetadataV2AttributeRuleType.Calculation;
                return false;
        }
    }

    private static string? GetString(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool? GetBool(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null
        };
    }
}

/// <summary>
/// Outcome of parsing an Esri layer's attribute-rule metadata into the canonical model.
/// </summary>
/// <param name="Rules">The parsed canonical rules, or <c>null</c> when there are none or they were truncated.</param>
/// <param name="Truncated">True when the rule set exceeded <see cref="EsriAttributeRuleParser.AttributeRuleCap"/> and was omitted.</param>
public readonly record struct EsriAttributeRuleParseResult(
    IReadOnlyList<MetadataV2AttributeRule>? Rules,
    bool Truncated)
{
    /// <summary>Shared result for a layer that carries no attribute rules.</summary>
    public static readonly EsriAttributeRuleParseResult None = new(Rules: null, Truncated: false);

    /// <summary>Shared result for a layer whose rule set exceeded the cap and was omitted.</summary>
    public static readonly EsriAttributeRuleParseResult OverCap = new(Rules: null, Truncated: true);
}
