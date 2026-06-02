// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Metadata.Domain.V2;

namespace Honua.Core.Features.Migration.Services;

/// <summary>
/// Shared, deterministic parser that converts an Esri layer's <c>subtypeField</c> /
/// <c>subtypes</c> / <c>defaultSubtypeCode</c> JSON into the canonical
/// <see cref="MetadataV2Subtypes"/> model.
/// <para>
/// This is the single source of truth for Esri subtype capture so the migration
/// inventory artifact and the import/publish projection agree. Per-subtype field
/// domains reuse <see cref="EsriFieldDomainParser"/> so the same
/// <see cref="EsriFieldDomainParser.CodedValueDomainCap"/> applies — an over-cap
/// per-subtype coded-value domain is dropped (not persisted as a partial lookup) so
/// it does not later false-fail reconciliation against the capped inventory artifact.
/// The number of captured subtypes is bounded by <see cref="SubtypeCap"/> for the
/// same determinism reason.
/// </para>
/// </summary>
public static class EsriSubtypeParser
{
    /// <summary>
    /// Maximum number of subtypes captured for a single layer. Layers that exceed this
    /// cap are reported as truncated and not persisted, keeping the artifact bounded and
    /// avoiding a partial subtype set that would false-fail later reconciliation.
    /// </summary>
    public const int SubtypeCap = 100;

    /// <summary>
    /// Parses the Esri subtype metadata on a layer element into the canonical
    /// <see cref="MetadataV2Subtypes"/> model.
    /// </summary>
    /// <param name="layerElement">The Esri layer element carrying <c>subtypeField</c> / <c>subtypes</c>.</param>
    /// <returns>
    /// A <see cref="EsriSubtypeParseResult"/> describing the parsed subtype set (or
    /// <c>null</c> when the layer declares no usable subtypes) and whether the subtype
    /// set was truncated by the cap.
    /// </returns>
    public static EsriSubtypeParseResult Parse(JsonElement layerElement)
    {
        if (layerElement.ValueKind != JsonValueKind.Object)
        {
            return EsriSubtypeParseResult.None;
        }

        var subtypeField = GetString(layerElement, "subtypeField") ?? GetString(layerElement, "subtypeFieldName");
        var subtypesElement = layerElement.TryGetProperty("subtypes", out var subtypes) ? subtypes : (JsonElement?)null;
        var defaultSubtypeCode = layerElement.TryGetProperty("defaultSubtypeCode", out var defaultCode)
            ? defaultCode
            : (JsonElement?)null;
        return Parse(subtypeField, defaultSubtypeCode, subtypesElement);
    }

    /// <summary>
    /// Parses Esri subtype metadata from its already-extracted parts (the
    /// <c>subtypeField</c> name, the optional <c>defaultSubtypeCode</c>, and the
    /// <c>subtypes</c> array element) into the canonical <see cref="MetadataV2Subtypes"/>
    /// model.
    /// </summary>
    /// <param name="subtypeField">The integer subtype field name, or <c>null</c>/blank when the layer has no subtype field.</param>
    /// <param name="defaultSubtypeCode">The default subtype code element, or <c>null</c> when none was declared.</param>
    /// <param name="subtypesElementOrNull">The Esri <c>subtypes</c> array element, or <c>null</c> when absent.</param>
    /// <returns>The parsed subtype result; <see cref="EsriSubtypeParseResult.None"/> when there is no usable subtype set.</returns>
    public static EsriSubtypeParseResult Parse(
        string? subtypeField,
        JsonElement? defaultSubtypeCode,
        JsonElement? subtypesElementOrNull)
    {
        if (string.IsNullOrWhiteSpace(subtypeField))
        {
            return EsriSubtypeParseResult.None;
        }

        if (subtypesElementOrNull is not { ValueKind: JsonValueKind.Array } subtypesElement ||
            subtypesElement.GetArrayLength() == 0)
        {
            // A subtypeField with no subtype definitions carries no usable lookup; the
            // field is still served as an ordinary attribute, so capture nothing.
            return EsriSubtypeParseResult.None;
        }

        if (subtypesElement.GetArrayLength() > SubtypeCap)
        {
            return EsriSubtypeParseResult.OverCap;
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
            return EsriSubtypeParseResult.None;
        }

        var ordered = subtypes
            .OrderBy(static subtype => subtype.Code.GetRawText(), StringComparer.Ordinal)
            .ThenBy(static subtype => subtype.Name, StringComparer.Ordinal)
            .ToArray();

        JsonElement? capturedDefault = defaultSubtypeCode is { } code && IsSupportedCode(code)
            ? code.Clone()
            : null;

        return new EsriSubtypeParseResult(
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

                // Reuse the shared domain parser so the same cap/consistency rules apply.
                // An over-cap per-subtype domain is dropped rather than persisted partial.
                var domain = EsriFieldDomainParser.ParseDomain(property.Value).Domain;
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
                if (string.IsNullOrWhiteSpace(property.Name) || property.Value.ValueKind == JsonValueKind.Null)
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
            DefaultValue = defaultValue ?? current.DefaultValue
        };
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

/// <summary>
/// Outcome of parsing an Esri layer's subtype metadata into the canonical model.
/// </summary>
/// <param name="Subtypes">The parsed canonical subtype set, or <c>null</c> when there is no usable subtype set or it was truncated.</param>
/// <param name="Truncated">True when the subtype set exceeded <see cref="EsriSubtypeParser.SubtypeCap"/> and was omitted.</param>
public readonly record struct EsriSubtypeParseResult(
    MetadataV2Subtypes? Subtypes,
    bool Truncated)
{
    /// <summary>
    /// Shared result for a layer that carries no subtypes.
    /// </summary>
    public static readonly EsriSubtypeParseResult None = new(Subtypes: null, Truncated: false);

    /// <summary>
    /// Shared result for a layer whose subtype set exceeded the cap and was omitted.
    /// </summary>
    public static readonly EsriSubtypeParseResult OverCap = new(Subtypes: null, Truncated: true);
}
