// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;

namespace Honua.Infrastructure.Caching;

internal static class ContentPublicationCacheTags
{
    public const string All = "content-publication";

    public static string Publication(string publicationId)
        => $"content-publication:{publicationId.ToLowerInvariant()}";

    public static string Route(string routeSlug)
        => $"content-route:{routeSlug.ToLowerInvariant()}";

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
