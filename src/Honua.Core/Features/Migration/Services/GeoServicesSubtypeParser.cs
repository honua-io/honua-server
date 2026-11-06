// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Metadata.Domain.V2;

namespace Honua.Core.Features.Migration.Services;

public static class GeoServicesSubtypeParser
{
    // Subtype sets over this cap are omitted entirely, because a partial subtype set would false-fail later reconciliation.
    public const int SubtypeCap = 100;

    public static GeoServicesSubtypeParseResult Parse(JsonElement layerElement)
    {
        if (layerElement.ValueKind != JsonValueKind.Object)
        {
            return GeoServicesSubtypeParseResult.None;
        }

        var subtypeField = GetString(layerElement, "subtypeField") ?? GetString(layerElement, "subtypeFieldName");
        var subtypesElement = layerElement.TryGetProperty("subtypes", out var subtypes) ? subtypes : (JsonElement?)null;
        var defaultSubtypeCode = layerElement.TryGetProperty("defaultSubtypeCode", out var defaultCode)
            ? defaultCode
            : (JsonElement?)null;
        var parsed = Parse(subtypeField, defaultSubtypeCode, subtypesElement);
        return parsed.Subtypes is not null || parsed.Truncated
            ? parsed
            : ParseFeatureTypes(GetString(layerElement, "typeIdField"),
                layerElement.TryGetProperty("types", out var types) ? types : null);
    }

    public static GeoServicesSubtypeParseResult ParseFeatureTypes(string? typeIdField, JsonElement? typesElement)
    {
        if (string.IsNullOrWhiteSpace(typeIdField) ||
            typesElement is not { ValueKind: JsonValueKind.Array } types || types.GetArrayLength() == 0)
        {
            return GeoServicesSubtypeParseResult.None;
        }
        if (types.GetArrayLength() > SubtypeCap)
        {
            return GeoServicesSubtypeParseResult.OverCap;
        }

        var normalized = new List<Dictionary<string, JsonElement>>();
        foreach (var type in types.EnumerateArray())
        {
            if (type.ValueKind != JsonValueKind.Object ||
                !type.TryGetProperty("id", out var id) || !IsSupportedCode(id) ||
                string.IsNullOrWhiteSpace(GetString(type, "name")))
            {
                return GeoServicesSubtypeParseResult.Unsupported(
                    ImportCompatibilityCodes.GeoServicesFeatureTypeIdentityUnsupported,
                    DescribeType(type, normalized.Count));
            }
            var entry = new Dictionary<string, JsonElement>
            {
                ["code"] = id.Clone(),
                ["name"] = type.GetProperty("name").Clone()
            };
            if (ReadObject(type, "domains") is { } domains)
            {
                var clearedField = domains
                    .EnumerateObject()
                    .FirstOrDefault(property => property.Value.ValueKind == JsonValueKind.Null);
                if (clearedField.Value.ValueKind == JsonValueKind.Null)
                {
                    return GeoServicesSubtypeParseResult.Unsupported(
                        ImportCompatibilityCodes.GeoServicesFeatureTypeDomainClearingUnsupported,
                        $"{DescribeType(type, normalized.Count)} clears the domain on field '{clearedField.Name}'");
                }
                entry["domains"] = domains.Clone();
            }
            if (type.TryGetProperty("templates", out var templates) && templates.ValueKind == JsonValueKind.Array)
            {
                if (templates.GetArrayLength() > 1)
                {
                    return GeoServicesSubtypeParseResult.Unsupported(
                        ImportCompatibilityCodes.GeoServicesFeatureTypeTemplatesUnsupported,
                        $"{DescribeType(type, normalized.Count)} declares {templates.GetArrayLength()} editing templates");
                }
                if (templates.GetArrayLength() == 1 &&
                    ReadObject(templates[0], "prototype") is { } prototype &&
                    ReadObject(prototype, "attributes") is { } attributes)
                {
                    entry["defaultValues"] = attributes.Clone();
                }
            }
            normalized.Add(entry);
        }
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartArray();
            foreach (var entry in normalized)
            {
                writer.WriteStartObject();
                foreach (var (key, value) in entry)
                {
                    writer.WritePropertyName(key);
                    value.WriteTo(writer);
                }
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }
        using var document = JsonDocument.Parse(buffer.ToArray());
        return Parse(typeIdField, null, document.RootElement);
    }

    public static GeoServicesSubtypeParseResult Parse(
        string? subtypeField,
        JsonElement? defaultSubtypeCode,
        JsonElement? subtypesElementOrNull)
    {
        if (string.IsNullOrWhiteSpace(subtypeField))
        {
            return GeoServicesSubtypeParseResult.None;
        }

        if (subtypesElementOrNull is not { ValueKind: JsonValueKind.Array } subtypesElement ||
            subtypesElement.GetArrayLength() == 0)
        {
            // A subtypeField with no subtype definitions carries no usable lookup; the
            // field is still served as an ordinary attribute, so capture nothing.
            return GeoServicesSubtypeParseResult.None;
        }

        if (subtypesElement.GetArrayLength() > SubtypeCap)
        {
            return GeoServicesSubtypeParseResult.OverCap;
        }

        var subtypes = new List<MetadataV2Subtype>();
        foreach (var entry in subtypesElement.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object ||
                !entry.TryGetProperty("code", out var codeElement) ||
                !IsSupportedCode(codeElement))
            {
                continue;
            }

            var name = GetString(entry, "name");
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            subtypes.Add(new MetadataV2Subtype
            {
                Code = codeElement.Clone(),
                Name = name!,
                FieldOverrides = ParseFieldOverrides(entry)
            });
        }

        if (subtypes.Count == 0)
        {
            return GeoServicesSubtypeParseResult.None;
        }

        var ordered = subtypes
            .OrderBy(static subtype => subtype.Code.GetRawText(), StringComparer.Ordinal)
            .ThenBy(static subtype => subtype.Name, StringComparer.Ordinal)
            .ToArray();

        JsonElement? capturedDefault = defaultSubtypeCode is { } code && IsSupportedCode(code)
            ? code.Clone()
            : null;

        return new GeoServicesSubtypeParseResult(
            new MetadataV2Subtypes
            {
                SubtypeField = subtypeField!,
                DefaultSubtypeCode = capturedDefault,
                Subtypes = ordered
            },
            Truncated: false);
    }

    // Esri encodes per-subtype overrides as parallel 'domains' and 'defaultValues'
    // objects keyed by field name. Merge both into the canonical per-field override map.
    private static IReadOnlyDictionary<string, MetadataV2SubtypeFieldOverride> ParseFieldOverrides(
        JsonElement subtypeElement)
    {
        var domains = ReadObject(subtypeElement, "domains");
        var defaults = ReadObject(subtypeElement, "defaultValues");
        if (domains is null && defaults is null)
        {
            return EmptyOverrides;
        }

        var overrides = new Dictionary<string, MetadataV2SubtypeFieldOverride>(StringComparer.OrdinalIgnoreCase);

        if (domains is { } domainsObject)
        {
            foreach (var property in domainsObject.EnumerateObject())
            {
                if (string.IsNullOrWhiteSpace(property.Name))
                {
                    continue;
                }

                // Inheritance is the absence of a subtype override, not a new
                // domain kind. Keep the published field's own domain in force.
                if (property.Value.ValueKind == JsonValueKind.Object &&
                    string.Equals(GetString(property.Value, "type"), "inherited", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                // Reuse the shared domain parser so the same cap/consistency rules apply.
                // An over-cap per-subtype domain is dropped rather than persisted partial.
                var domain = GeoServicesFieldDomainParser.ParseDomain(property.Value).Domain;
                if (domain is null)
                {
                    continue;
                }

                overrides[property.Name] = MergeOverride(overrides, property.Name, domain: domain);
            }
        }

        if (defaults is { } defaultsObject)
        {
            foreach (var property in defaultsObject.EnumerateObject())
            {
                if (string.IsNullOrWhiteSpace(property.Name))
                {
                    continue;
                }

                overrides[property.Name] = MergeOverride(
                    overrides,
                    property.Name,
                    defaultValue: property.Value.Clone());
            }
        }

        return overrides.Count == 0 ? EmptyOverrides : overrides;
    }

    private static MetadataV2SubtypeFieldOverride MergeOverride(
        Dictionary<string, MetadataV2SubtypeFieldOverride> existing,
        string fieldName,
        MetadataV2FieldDomain? domain = null,
        JsonElement? defaultValue = null)
    {
        var current = existing.TryGetValue(fieldName, out var found)
            ? found
            : new MetadataV2SubtypeFieldOverride();

        return current with
        {
            Domain = domain ?? current.Domain,
            DefaultValueIsNull = defaultValue is { ValueKind: JsonValueKind.Null } ||
                (!defaultValue.HasValue && current.DefaultValueIsNull),
            DefaultValue = defaultValue ?? current.DefaultValue
        };
    }

    // Identifies the offending source type in a finding without echoing the whole
    // type document into an artifact an operator has to read.
    private static string DescribeType(JsonElement type, int ordinal)
    {
        var name = type.ValueKind == JsonValueKind.Object ? GetString(type, "name") : null;
        return string.IsNullOrWhiteSpace(name)
            ? $"Feature type at index {ordinal}"
            : $"Feature type '{name}'";
    }

    private static JsonElement? ReadObject(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.Object
            ? value
            : null;

    private static bool IsSupportedCode(JsonElement codeElement)
        => codeElement.ValueKind is JsonValueKind.String
            or JsonValueKind.Number
            or JsonValueKind.True
            or JsonValueKind.False;

    private static string? GetString(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static readonly IReadOnlyDictionary<string, MetadataV2SubtypeFieldOverride> EmptyOverrides =
        new Dictionary<string, MetadataV2SubtypeFieldOverride>(StringComparer.OrdinalIgnoreCase);
}

public readonly record struct GeoServicesSubtypeParseResult(
    MetadataV2Subtypes? Subtypes,
    bool Truncated,
    string? UnsupportedCode = null,
    string? UnsupportedDetail = null)
{
    public static readonly GeoServicesSubtypeParseResult None = new(Subtypes: null, Truncated: false);

    public static readonly GeoServicesSubtypeParseResult OverCap = new(Subtypes: null, Truncated: true);

    // An unrepresentable editing construct is named in a fidelity finding and nothing is captured, instead of failing without a reason.
    public static GeoServicesSubtypeParseResult Unsupported(string code, string detail)
        => new(Subtypes: null, Truncated: false, UnsupportedCode: code, UnsupportedDetail: detail);
}
