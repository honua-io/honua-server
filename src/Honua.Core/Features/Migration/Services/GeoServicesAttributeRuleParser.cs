// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Metadata.Domain.V2;

namespace Honua.Core.Features.Migration.Services;

public static class GeoServicesAttributeRuleParser
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
    public static GeoServicesAttributeRuleParseResult Parse(JsonElement layerElement)
    {
        if (layerElement.ValueKind != JsonValueKind.Object)
        {
            return GeoServicesAttributeRuleParseResult.None;
        }

        var rulesElement = layerElement.TryGetProperty("attributeRules", out var rules)
            ? rules
            : (JsonElement?)null;
        return Parse(rulesElement);
    }

    public static GeoServicesAttributeRuleParseResult Parse(JsonElement? attributeRulesElementOrNull)
    {
        if (attributeRulesElementOrNull is not { ValueKind: JsonValueKind.Array } rulesElement ||
            rulesElement.GetArrayLength() == 0)
        {
            return GeoServicesAttributeRuleParseResult.None;
        }

        if (rulesElement.GetArrayLength() > AttributeRuleCap)
        {
            return GeoServicesAttributeRuleParseResult.OverCap;
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
            return GeoServicesAttributeRuleParseResult.None;
        }

        var ordered = parsed
            .OrderBy(static rule => rule.Name, StringComparer.Ordinal)
            .ToArray();

        return new GeoServicesAttributeRuleParseResult(ordered, Truncated: false);
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

public readonly record struct GeoServicesAttributeRuleParseResult(
    IReadOnlyList<MetadataV2AttributeRule>? Rules,
    bool Truncated)
{
    /// <summary>Shared result for a layer that carries no attribute rules.</summary>
    public static readonly GeoServicesAttributeRuleParseResult None = new(Rules: null, Truncated: false);

    /// <summary>Shared result for a layer whose rule set exceeded the cap and was omitted.</summary>
    public static readonly GeoServicesAttributeRuleParseResult OverCap = new(Rules: null, Truncated: true);
}
