// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Metadata.Domain.V2;

namespace Honua.Core.Features.Migration.Services;

// A coded-value domain past the cap is reported as truncated and not persisted, so later reconciliation does not compare a halved domain.
public static class GeoServicesFieldDomainParser
{
    public const int CodedValueDomainCap = 100;

    public const string CodedValueDomainType = "codedValue";

    public const string RangeDomainType = "range";

    public static GeoServicesFieldDomainParseResult Parse(JsonElement fieldElement)
    {
        if (fieldElement.ValueKind != JsonValueKind.Object ||
            !fieldElement.TryGetProperty("domain", out var domainElement))
        {
            return GeoServicesFieldDomainParseResult.None;
        }

        return ParseDomain(domainElement);
    }

    public static GeoServicesFieldDomainParseResult ParseDomain(JsonElement? domainElement)
    {
        if (domainElement is not { ValueKind: JsonValueKind.Object } domain)
        {
            return GeoServicesFieldDomainParseResult.None;
        }

        var type = GetString(domain, "type");
        var name = GetString(domain, "name");
        var mergePolicy = GetString(domain, "mergePolicy");
        var splitPolicy = GetString(domain, "splitPolicy");

        if (string.Equals(type, CodedValueDomainType, StringComparison.Ordinal))
        {
            return ParseCodedValueDomain(domain, name, mergePolicy, splitPolicy);
        }

        if (string.Equals(type, RangeDomainType, StringComparison.Ordinal))
        {
            return ParseRangeDomain(domain, name, mergePolicy, splitPolicy);
        }

        // Unknown domain kind: capture identity only so downstream review still
        // sees that a domain existed without inventing coded values or a range.
        if (string.IsNullOrEmpty(type))
        {
            return GeoServicesFieldDomainParseResult.None;
        }

        return new GeoServicesFieldDomainParseResult(
            new MetadataV2FieldDomain
            {
                Name = name,
                Type = type!,
                MergePolicy = mergePolicy,
                SplitPolicy = splitPolicy
            },
            Truncated: false);
    }

    private static GeoServicesFieldDomainParseResult ParseCodedValueDomain(
        JsonElement domainElement,
        string? name,
        string? mergePolicy,
        string? splitPolicy)
    {
        if (!domainElement.TryGetProperty("codedValues", out var codedValuesElement) ||
            codedValuesElement.ValueKind != JsonValueKind.Array)
        {
            return new GeoServicesFieldDomainParseResult(
                new MetadataV2FieldDomain
                {
                    Name = name,
                    Type = CodedValueDomainType,
                    CodedValues = [],
                    MergePolicy = mergePolicy,
                    SplitPolicy = splitPolicy
                },
                Truncated: false);
        }

        var entries = new List<MetadataV2CodedValue>();
        var truncated = false;
        foreach (var entry in codedValuesElement.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object ||
                !entry.TryGetProperty("code", out var codeElement))
            {
                continue;
            }

            var entryName = GetString(entry, "name");
            if (string.IsNullOrWhiteSpace(entryName) || !IsSupportedCode(codeElement))
            {
                continue;
            }

            if (entries.Count >= CodedValueDomainCap)
            {
                truncated = true;
                break;
            }

            entries.Add(new MetadataV2CodedValue
            {
                Code = codeElement.Clone(),
                Name = entryName!
            });
        }

        // A truncated domain is not persisted: keeping a half-domain would let an
        // Esri client believe the lookup is complete. Report the cap instead.
        if (truncated)
        {
            return new GeoServicesFieldDomainParseResult(Domain: null, Truncated: true, DomainName: name);
        }

        var ordered = entries
            .OrderBy(static value => value.Code.GetRawText(), StringComparer.Ordinal)
            .ThenBy(static value => value.Name, StringComparer.Ordinal)
            .ToArray();

        return new GeoServicesFieldDomainParseResult(
            new MetadataV2FieldDomain
            {
                Name = name,
                Type = CodedValueDomainType,
                CodedValues = ordered,
                MergePolicy = mergePolicy,
                SplitPolicy = splitPolicy
            },
            Truncated: false);
    }

    private static GeoServicesFieldDomainParseResult ParseRangeDomain(
        JsonElement domainElement,
        string? name,
        string? mergePolicy,
        string? splitPolicy)
    {
        JsonElement[]? range = null;
        if (domainElement.TryGetProperty("range", out var rangeElement) &&
            rangeElement.ValueKind == JsonValueKind.Array)
        {
            var values = new List<JsonElement>(capacity: 2);
            foreach (var value in rangeElement.EnumerateArray())
            {
                values.Add(value.Clone());
            }

            // A well-formed Esri range domain carries exactly [min, max]. Anything
            // else is left null so serving omits an ambiguous range.
            if (values.Count == 2)
            {
                range = [.. values];
            }
        }

        return new GeoServicesFieldDomainParseResult(
            new MetadataV2FieldDomain
            {
                Name = name,
                Type = RangeDomainType,
                Range = range,
                MergePolicy = mergePolicy,
                SplitPolicy = splitPolicy
            },
            Truncated: false);
    }

    public static bool IsEnforceable(MetadataV2FieldDomain? domain)
    {
        if (domain is null)
        {
            return false;
        }

        if (string.Equals(domain.Type, CodedValueDomainType, StringComparison.OrdinalIgnoreCase))
        {
            return domain.CodedValues.Count > 0;
        }

        if (string.Equals(domain.Type, RangeDomainType, StringComparison.OrdinalIgnoreCase))
        {
            return domain.Range is { Count: 2 } range && IsNumericBound(range[0]) && IsNumericBound(range[1]);
        }

        return false;
    }

    private static bool IsNumericBound(JsonElement bound)
        => (bound.ValueKind == JsonValueKind.Number && bound.TryGetDouble(out _))
            || (bound.ValueKind == JsonValueKind.String
                && double.TryParse(bound.GetString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _));

    private static bool IsSupportedCode(JsonElement codeElement)
        => codeElement.ValueKind is JsonValueKind.String
            or JsonValueKind.Number
            or JsonValueKind.True
            or JsonValueKind.False;

    private static string? GetString(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}

public readonly record struct GeoServicesFieldDomainParseResult(
    MetadataV2FieldDomain? Domain,
    bool Truncated,
    string? DomainName = null)
{
    public static readonly GeoServicesFieldDomainParseResult None = new(Domain: null, Truncated: false);
}
