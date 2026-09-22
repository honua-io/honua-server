// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Studio.Domain;

/// <summary>
/// Server URL of an Active Studio publication (honua-server#4907). A publication intent's
/// <c>route</c> is a governed key, not a path the host serves; the published-route resolver
/// under <see cref="BasePath"/> serves the item's Active (published-pointer) version for it.
/// </summary>
public static class StudioPublishedRoutes
{
    /// <summary>Root-relative path of the Studio published-route resolver.</summary>
    public const string BasePath = "/api/v1/studio/published";

    /// <summary>
    /// Builds the root-relative URL that serves the Active version published at
    /// <paramref name="route"/>. Each route segment is percent-encoded so the resolver's
    /// catch-all parameter decodes back to the exact governed route.
    /// </summary>
    public static string BuildActiveUrl(string route)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(route);
        var key = route[0] == '/' ? route : "/" + route;
        return BasePath + string.Join('/', key.Split('/').Select(Uri.EscapeDataString));
    }

    /// <summary>Rebuilds the governed route key from the resolver's catch-all path value.</summary>
    public static string ToRouteKey(string? path) => "/" + (path ?? string.Empty);
}
