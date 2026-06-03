// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Metadata.Domain.V2;

namespace Honua.Core.Features.Migration.Services;

/// <summary>
/// Shared, deterministic parser that converts an Esri field <c>domain</c> JSON
/// fragment into the canonical <see cref="MetadataV2FieldDomain"/> model.
/// <para>
/// This is the single source of truth for Esri domain capture so the migration
/// inventory artifact and the import/publish field builder agree on cap
/// semantics (<see cref="CodedValueDomainCap"/>). Coded-value domains larger than
/// the cap are reported as truncated and not persisted, so a large domain is
/// surfaced as a review warning rather than being silently halved or
/// false-failing later reconciliation against a capped artifact.
/// </para>
/// </summary>
public static class EsriFieldDomainParser
{
    /// <summary>
    /// Maximum number of coded-value entries captured for a single domain. Domains
    /// that exceed this cap are reported via <see cref="EsriFieldDomainParseResult.Truncated"/>
    /// and are not persisted, keeping artifacts deterministic and bounded.
    /// </summary>
    public const int CodedValueDomainCap = 100;

    /// <summary>
    /// Canonical domain-kind token for an enumerated coded-value domain.
    /// </summary>
    public const string CodedValueDomainType = "codedValue";

    /// <summary>
    /// Canonical domain-kind token for a numeric range domain.
    /// </summary>
    public const string RangeDomainType = "range";

    /// <summary>
    /// Parses the Esri <c>domain</c> object on a field element into the canonical
    /// <see cref="MetadataV2FieldDomain"/> model.
    /// </summary>
    /// <param name="fieldElement">The Esri field element (the object that may carry a <c>domain</c> property).</param>
    /// <returns>
    /// A <see cref="EsriFieldDomainParseResult"/> describing the parsed domain (or
    /// <c>null</c> when the field carries no usable domain) and whether a coded-value
    /// domain was truncated by the cap.
    /// </returns>
    public static EsriFieldDomainParseResult Parse(JsonElement fieldElement)
    {
        if (fieldElement.ValueKind != JsonValueKind.Object ||
            !fieldElement.TryGetProperty("domain", out var domainElement))
        {
            return EsriFieldDomainParseResult.None;
        }

        return ParseDomain(domainElement);
    }

    /// <summary>
    /// Parses an Esri <c>domain</c> object directly (already extracted from a field).
    /// </summary>
    /// <param name="domainElement">The Esri <c>domain</c> object, or any non-object value when the field has no domain.</param>
    /// <returns>The parsed domain result; <see cref="EsriFieldDomainParseResult.None"/> when there is no usable domain.</returns>
    public static EsriFieldDomainParseResult ParseDomain(JsonElement? domainElement)
    {
        if (domainElement is not { ValueKind: JsonValueKind.Object } domain)
        {
            return EsriFieldDomainParseResult.None;
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
            return EsriFieldDomainParseResult.None;
        }

        return new EsriFieldDomainParseResult(
            new MetadataV2FieldDomain
            {
                Name = name,
                Type = type!,
                MergePolicy = mergePolicy,
                SplitPolicy = splitPolicy
            },
            Truncated: false);
    }

    private static EsriFieldDomainParseResult ParseCodedValueDomain(
        JsonElement domainElement,
        string? name,
        string? mergePolicy,
        string? splitPolicy)
    {
        if (!domainElement.TryGetProperty("codedValues", out var codedValuesElement) ||
            codedValuesElement.ValueKind != JsonValueKind.Array)
        {
            return new EsriFieldDomainParseResult(
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
            return new EsriFieldDomainParseResult(Domain: null, Truncated: true, DomainName: name);
        }

        var ordered = entries
            .OrderBy(static value => value.Code.GetRawText(), StringComparer.Ordinal)
            .ThenBy(static value => value.Name, StringComparer.Ordinal)
            .ToArray();

        return new EsriFieldDomainParseResult(
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

    private static EsriFieldDomainParseResult ParseRangeDomain(
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

        return new EsriFieldDomainParseResult(
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

/// <summary>
/// Outcome of parsing an Esri field domain into the canonical model.
/// </summary>
/// <param name="Domain">The parsed canonical domain, or <c>null</c> when there is no usable domain or it was truncated.</param>
/// <param name="Truncated">True when a coded-value domain exceeded <see cref="EsriFieldDomainParser.CodedValueDomainCap"/> and was omitted.</param>
/// <param name="DomainName">The source domain name when known (used for truncation reporting).</param>
public readonly record struct EsriFieldDomainParseResult(
    MetadataV2FieldDomain? Domain,
    bool Truncated,
    string? DomainName = null)
{
    /// <summary>
    /// Shared result for a field that carries no domain.
    /// </summary>
    public static readonly EsriFieldDomainParseResult None = new(Domain: null, Truncated: false);
}
