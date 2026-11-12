// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Text.Json;
using Honua.Core.Features.Admin.Domain;
using Honua.Core.Features.Metadata.Crosswalk;
using Honua.Core.Features.Metadata.Domain.V2;

namespace Honua.Protocols.Stac.Services;

internal static class StacResourceExtensions
{
    private const string StacExtensionKey = "stac";

    public static MetadataV2ObjectMetadata WithStacServiceGovernanceFallbacks(
        this MetadataV2Resource resource,
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
        this MetadataV2Resource resource,
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

    public static ImmutableArray<string>? ResolveKeywords(this MetadataV2Resource resource)
    {
        ArgumentNullException.ThrowIfNull(resource);
        var keywords = DescriptiveMetadataResolver.NormalizedTerms(resource.Metadata.Keywords);
        return keywords.Count == 0 ? null : [.. keywords];
    }

    public static ImmutableArray<string>? ResolveDeclaredExtensions(this MetadataV2Resource resource)
    {
        ArgumentNullException.ThrowIfNull(resource);
        return ReadStringArray(resource, "extensions");
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
