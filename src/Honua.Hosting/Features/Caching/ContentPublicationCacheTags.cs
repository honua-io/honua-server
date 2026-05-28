// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;

namespace Honua.Server.Features.Infrastructure.Caching;

/// <summary>
/// Canonical output-cache tag strings for the content publication registry. Shared
/// by the cache policy (tagging public reads) and the invalidation service (evicting
/// on route pointer or policy changes) so the tag vocabulary stays in one place.
/// </summary>
internal static class ContentPublicationCacheTags
{
    /// <summary>Broadcast tag covering every published-route response.</summary>
    public const string All = "content-publication";

    /// <summary>Tag scoping a single publication id.</summary>
    public static string Publication(string publicationId)
        => $"content-publication:{publicationId.ToLowerInvariant()}";

    /// <summary>Tag scoping a single route slug.</summary>
    public static string Route(string routeSlug)
        => $"content-route:{routeSlug.ToLowerInvariant()}";

    /// <summary>Reads the route slug from the request route values, if present.</summary>
    public static string? ReadRouteSlug(IDictionary<string, object?> routeValues)
    {
        if (routeValues.TryGetValue("routeSlug", out var value) && value is not null)
        {
            var slug = Convert.ToString(value, CultureInfo.InvariantCulture);
            return string.IsNullOrWhiteSpace(slug) ? null : slug;
        }

        return null;
    }
}
