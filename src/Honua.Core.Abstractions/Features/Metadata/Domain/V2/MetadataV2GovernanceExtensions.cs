// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Metadata.Domain.V2;

/// <summary>
/// Resolves resource-scoped governance with service-scoped defaults.
/// </summary>
public static class MetadataV2GovernanceExtensions
{
    /// <summary>
    /// Returns the resource metadata with missing governance fields inherited from the publishing
    /// service. Resource values remain authoritative; service links are appended after resource links
    /// and exact duplicates are removed.
    /// </summary>
    public static MetadataV2ObjectMetadata WithServiceGovernanceFallbacks(
        this MetadataV2ObjectMetadata resourceMetadata,
        MetadataV2ObjectMetadata? serviceMetadata)
    {
        ArgumentNullException.ThrowIfNull(resourceMetadata);
        if (serviceMetadata is null)
        {
            return resourceMetadata;
        }

        return resourceMetadata with
        {
            License = FirstDefined(resourceMetadata.License, serviceMetadata.License),
            Attribution = FirstDefined(resourceMetadata.Attribution, serviceMetadata.Attribution),
            Publisher = FirstDefined(resourceMetadata.Publisher, serviceMetadata.Publisher),
            ContactPoint = resourceMetadata.ContactPoint ?? serviceMetadata.ContactPoint,
            Links = MergeLinks(resourceMetadata.Links, serviceMetadata.Links),
        };
    }

    private static string? FirstDefined(string? resourceValue, string? serviceValue)
        => !string.IsNullOrWhiteSpace(resourceValue) ? resourceValue : serviceValue;

    private static IReadOnlyList<MetadataV2Link> MergeLinks(
        IReadOnlyList<MetadataV2Link> resourceLinks,
        IReadOnlyList<MetadataV2Link> serviceLinks)
    {
        if (serviceLinks.Count == 0)
        {
            return resourceLinks;
        }

        var links = new List<MetadataV2Link>(resourceLinks.Count + serviceLinks.Count);
        links.AddRange(resourceLinks);
        foreach (var serviceLink in serviceLinks)
        {
            if (!links.Any(existing => LinksEqual(existing, serviceLink)))
            {
                links.Add(serviceLink);
            }
        }

        return links;
    }

    private static bool LinksEqual(MetadataV2Link left, MetadataV2Link right)
        => string.Equals(left.Href, right.Href, StringComparison.Ordinal) &&
           string.Equals(left.Rel, right.Rel, StringComparison.OrdinalIgnoreCase) &&
           string.Equals(left.Type, right.Type, StringComparison.OrdinalIgnoreCase) &&
           string.Equals(left.Title, right.Title, StringComparison.Ordinal) &&
           string.Equals(left.Hreflang, right.Hreflang, StringComparison.OrdinalIgnoreCase);
}
