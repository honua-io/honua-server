// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Text.Json;
using Honua.Core.Features.Admin.Domain;
using Honua.Core.Features.Metadata.Domain.V2;

namespace Honua.Protocols.Stac.Services;

internal static class StacResourceExtensions
{
    private const string StacExtensionKey = "stac";

    public static MetadataV2ObjectMetadata WithStacServiceGovernanceFallbacks(
        this MetadataV2Object resource,
        MetadataV2ObjectMetadata serviceMetadata)
    {
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentNullException.ThrowIfNull(serviceMetadata);

        if (string.IsNullOrWhiteSpace(ReadString(resource, "license")))
        {
            return resource.Metadata.WithServiceGovernanceFallbacks(serviceMetadata);
        }

        var nonLicenseFallbacks = serviceMetadata with
        {
            License = null,
            Links = serviceMetadata.Links
                .Where(link => !string.Equals(link.Rel, "license", StringComparison.OrdinalIgnoreCase))
                .ToArray(),
        };
        return resource.Metadata.WithServiceGovernanceFallbacks(nonLicenseFallbacks);
    }

    public static string ResolveLicense(
        this MetadataV2Object resource,
        MetadataV2ObjectMetadata? fallbackMetadata = null)
    {
        ArgumentNullException.ThrowIfNull(resource);
        var declared = !string.IsNullOrWhiteSpace(resource.Metadata.License)
            ? resource.Metadata.License.Trim()
            : ReadString(resource, "license")?.Trim();
        if (string.IsNullOrWhiteSpace(declared))
        {
            if (resource.Metadata.Links.Any(link =>
                string.Equals(link.Rel, "license", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(link.Href)))
            {
                return "various";
            }

            declared = fallbackMetadata?.License?.Trim();
            if (string.IsNullOrWhiteSpace(declared))
            {
                return fallbackMetadata?.Links.Any(link =>
                    string.Equals(link.Rel, "license", StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrWhiteSpace(link.Href)) == true
                    ? "various"
                    : "proprietary";
            }
        }

        return IsSingleStacLicenseToken(declared)
            ? declared
            : "various";
    }

    private static bool IsSingleStacLicenseToken(string license)
    {
        if (string.Equals(license, "proprietary", StringComparison.Ordinal) ||
            string.Equals(license, "various", StringComparison.Ordinal))
        {
            return true;
        }

        return LayerSourceGovernance.IsSpdxLicenseIdentifier(license);
    }

    public static ImmutableArray<string>? ResolveKeywords(this MetadataV2Object resource)
    {
        ArgumentNullException.ThrowIfNull(resource);
        var keywords = ReadStringArray(resource, "keywords");
        if (keywords is null || keywords.Value.Length == 0)
        {
            return ResourceTagsAsKeywords(resource);
        }
        return keywords;
    }

    public static ImmutableArray<string>? ResolveDeclaredExtensions(this MetadataV2Object resource)
    {
        ArgumentNullException.ThrowIfNull(resource);
        return ReadStringArray(resource, "extensions");
    }

    private static ImmutableArray<string>? ResourceTagsAsKeywords(MetadataV2Object resource)
    {
        // STAC keywords are now sourced from the resource's labels (the v2
        // Kubernetes-style label dictionary). A label entry with empty value is
        // treated as a bare tag.
        var labels = resource.Metadata?.Labels;
        if (labels is null || labels.Count == 0)
        {
            return null;
        }

        var normalized = labels.Keys
            .Where(static keyword => !string.IsNullOrWhiteSpace(keyword))
            .Select(static keyword => keyword.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToImmutableArray();

        return normalized.Length == 0 ? null : normalized;
    }

    private static string? ReadString(MetadataV2Object resource, string propertyName)
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

    private static ImmutableArray<string>? ReadStringArray(MetadataV2Object resource, string propertyName)
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

    private static bool TryGetStacObject(MetadataV2Object resource, out JsonElement stac)
    {
        if (resource.Extensions is not null &&
            resource.Extensions.TryGetValue(StacExtensionKey, out var element) &&
            element.ValueKind == JsonValueKind.Object)
        {
            stac = element;
            return true;
        }
        stac = default;
        return false;
    }
}
