// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Text.Json;
using Honua.Core.Features.Metadata.Domain.V2;

namespace Honua.Server.Features.Protocols.Stac.Services;

/// <summary>
/// Reads STAC-specific projection extras (license, keywords, declared STAC extensions)
/// from a <see cref="MetadataV2Resource"/>'s extension data. STAC-specific configuration
/// lives under <c>resource.Extensions["stac"]</c> as JSON; this helper exposes the small
/// surface STAC handlers actually consume so callers do not parse the JsonElement inline.
///
/// Expected shape under <c>resource.Extensions["stac"]</c>:
/// <code>
/// {
///   "license": "CC-BY-4.0",
///   "keywords": ["earth-observation", "weather"],
///   "extensions": ["https://stac-extensions.github.io/eo/v1.0.0/schema.json"]
/// }
/// </code>
/// </summary>
internal static class StacResourceExtensions
{
    private const string StacExtensionKey = "stac";

    /// <summary>
    /// Resolves the SPDX or STAC-recognized license identifier for a resource. Returns
    /// <c>"proprietary"</c> when no license is declared (matches the v1 default).
    /// </summary>
    public static string ResolveLicense(this MetadataV2Resource resource)
    {
        ArgumentNullException.ThrowIfNull(resource);
        var declared = ReadString(resource, "license");
        return string.IsNullOrWhiteSpace(declared)
            ? "proprietary"
            : declared.Trim();
    }

    /// <summary>
    /// Resolves the keywords declared for the resource. Returns null when none declared
    /// so the STAC collection response omits the field entirely.
    /// </summary>
    public static ImmutableArray<string>? ResolveKeywords(this MetadataV2Resource resource)
    {
        ArgumentNullException.ThrowIfNull(resource);
        var keywords = ReadStringArray(resource, "keywords");
        if (keywords is null || keywords.Value.Length == 0)
        {
            return ResourceTagsAsKeywords(resource);
        }
        return keywords;
    }

    /// <summary>
    /// Resolves the STAC extension URIs declared by the resource.
    /// </summary>
    public static ImmutableArray<string>? ResolveDeclaredExtensions(this MetadataV2Resource resource)
    {
        ArgumentNullException.ThrowIfNull(resource);
        return ReadStringArray(resource, "extensions");
    }

    private static ImmutableArray<string>? ResourceTagsAsKeywords(MetadataV2Resource resource)
    {
        // STAC keywords are now sourced from the resource's labels (the v2
        // Kubernetes-style label dictionary). A label entry with empty value is
        // treated as a bare tag.
        if (resource.Metadata.Labels.Count == 0)
        {
            return null;
        }

        var normalized = resource.Metadata.Labels.Keys
            .Where(static keyword => !string.IsNullOrWhiteSpace(keyword))
            .Select(static keyword => keyword.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToImmutableArray();

        return normalized.Length == 0 ? null : normalized;
    }

    private static string? ReadString(MetadataV2Resource resource, string propertyName)
    {
        if (!TryGetStacObject(resource, out var stac))
        {
            return null;
        }
        if (!stac.TryGetProperty(propertyName, out var value) ||
            value.ValueKind != JsonValueKind.String)
        {
            return null;
        }
        return value.GetString();
    }

    private static ImmutableArray<string>? ReadStringArray(MetadataV2Resource resource, string propertyName)
    {
        if (!TryGetStacObject(resource, out var stac))
        {
            return null;
        }
        if (!stac.TryGetProperty(propertyName, out var value) ||
            value.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var builder = ImmutableArray.CreateBuilder<string>();
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                continue;
            }
            var text = item.GetString();
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }
            builder.Add(text.Trim());
        }

        if (builder.Count == 0)
        {
            return null;
        }

        return builder
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToImmutableArray();
    }

    private static bool TryGetStacObject(MetadataV2Resource resource, out JsonElement stac)
    {
        if (resource.Extensions.TryGetValue(StacExtensionKey, out var element) &&
            element.ValueKind == JsonValueKind.Object)
        {
            stac = element;
            return true;
        }
        stac = default;
        return false;
    }
}
