// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Migration.Services;

namespace Honua.Core.Features.Admin.Domain;

public static class LayerPublishSourceMetadataBounds
{
    public const int MaxCodedValuesPerDomain = GeoServicesFieldDomainParser.CodedValueDomainCap;

    public const int MaxSubtypes = GeoServicesSubtypeParser.SubtypeCap;

    public const int MaxAttributeRules = GeoServicesAttributeRuleParser.AttributeRuleCap;

    public static bool TryValidate(LayerPublishRequest request, out string? error)
    {
        ArgumentNullException.ThrowIfNull(request);
        return TryValidate(request.FieldDomains, request.Subtypes, request.AttributeRules, out error);
    }

    public static bool TryValidate(
        IReadOnlyDictionary<string, MetadataV2FieldDomain>? fieldDomains,
        MetadataV2Subtypes? subtypes,
        IReadOnlyList<MetadataV2AttributeRule>? attributeRules,
        out string? error)
    {
        error = ValidateFieldDomains(fieldDomains)
            ?? ValidateSubtypes(subtypes)
            ?? ValidateAttributeRules(attributeRules);
        return error is null;
    }

    private static string? ValidateFieldDomains(IReadOnlyDictionary<string, MetadataV2FieldDomain>? fieldDomains)
    {
        if (fieldDomains is null)
        {
            return null;
        }

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (fieldName, domain) in fieldDomains)
        {
            if (string.IsNullOrWhiteSpace(fieldName))
            {
                return "fieldDomains keys must be field names.";
            }

            if (!names.Add(fieldName))
            {
                return "fieldDomains keys must be unique ignoring case.";
            }

            if (ValidateDomain(domain, "fieldDomains") is { } domainError)
            {
                return domainError;
            }
        }

        return null;
    }

    private static string? ValidateDomain(MetadataV2FieldDomain? domain, string slot)
    {
        if (domain is null || string.IsNullOrWhiteSpace(domain.Type))
        {
            return $"{slot} entries must declare a domain type.";
        }

        // Bound stored arrays independently of the client-supplied domain kind. The
        // publication projection retains these properties even for unknown kinds.
        var codedValues = domain.CodedValues;
        if (codedValues is { Count: > MaxCodedValuesPerDomain })
        {
            return $"{slot} coded-value domains are limited to {MaxCodedValuesPerDomain} coded values.";
        }

        if (domain.Range is { Count: not 2 })
        {
            return $"{slot} range domains must declare exactly [min, max].";
        }

        if (codedValues is null
            && string.Equals(domain.Type, GeoServicesFieldDomainParser.CodedValueDomainType, StringComparison.OrdinalIgnoreCase))
        {
            return $"{slot} coded-value domains must list their coded values.";
        }

        if (codedValues is not null)
        {
            foreach (var codedValue in codedValues)
            {
                if (codedValue is null || !IsSupportedCode(codedValue.Code) || string.IsNullOrWhiteSpace(codedValue.Name))
                {
                    return $"{slot} coded values need a string, number or boolean code and a name.";
                }
            }
        }

        return null;
    }

    private static string? ValidateSubtypes(MetadataV2Subtypes? subtypes)
    {
        if (subtypes is null)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(subtypes.SubtypeField))
        {
            return "subtypes.subtypeField is required.";
        }

        var entries = subtypes.Subtypes;
        if (entries is null || entries.Count == 0)
        {
            return "subtypes must declare at least one subtype.";
        }

        if (entries.Count > MaxSubtypes)
        {
            return $"subtypes are limited to {MaxSubtypes} per layer.";
        }

        if (subtypes.DefaultSubtypeCode is { } defaultCode && !IsSupportedCode(defaultCode))
        {
            return "subtypes.defaultSubtypeCode must be a string, number or boolean.";
        }

        foreach (var subtype in entries)
        {
            if (subtype is null || !IsSupportedCode(subtype.Code) || string.IsNullOrWhiteSpace(subtype.Name))
            {
                return "subtypes entries need a string, number or boolean code and a name.";
            }

            if (subtype.FieldOverrides is null)
            {
                return "subtypes fieldOverrides must not be null.";
            }

            foreach (var (fieldName, fieldOverride) in subtype.FieldOverrides)
            {
                if (string.IsNullOrWhiteSpace(fieldName) || fieldOverride is null)
                {
                    return "subtypes fieldOverrides must be keyed by field name.";
                }

                if (fieldOverride.Domain is not null
                    && ValidateDomain(fieldOverride.Domain, "subtypes fieldOverrides") is { } domainError)
                {
                    return domainError;
                }
            }
        }

        return null;
    }

    private static string? ValidateAttributeRules(IReadOnlyList<MetadataV2AttributeRule>? attributeRules)
    {
        if (attributeRules is null)
        {
            return null;
        }

        if (attributeRules.Count > MaxAttributeRules)
        {
            return $"attributeRules are limited to {MaxAttributeRules} per layer.";
        }

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rule in attributeRules)
        {
            if (rule is null || string.IsNullOrWhiteSpace(rule.Name))
            {
                return "attributeRules entries need a name.";
            }

            if (!names.Add(rule.Name))
            {
                return "attributeRules names must be unique.";
            }

            if (rule.Type == MetadataV2AttributeRuleType.Unknown || !Enum.IsDefined(rule.Type))
            {
                return "attributeRules type is not supported.";
            }

            if (string.IsNullOrWhiteSpace(rule.ScriptExpression))
            {
                return "attributeRules entries need a scriptExpression.";
            }

            if (rule.Type == MetadataV2AttributeRuleType.Calculation && string.IsNullOrWhiteSpace(rule.FieldName))
            {
                return "attributeRules calculation rules need a fieldName.";
            }

            if (rule.TriggeringEvents is null)
            {
                return "attributeRules triggeringEvents must not be null.";
            }

            if (rule.TriggeringEvents.Any(string.IsNullOrWhiteSpace))
            {
                return "attributeRules triggeringEvents must not be blank.";
            }
        }

        return null;
    }

    private static bool IsSupportedCode(JsonElement code)
        => code.ValueKind is JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False;
}
